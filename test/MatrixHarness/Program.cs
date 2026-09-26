
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using IoPath = System.IO.Path;
using IoFile = System.IO.File;
using IoDir = System.IO.Directory;
using IoInfo = System.IO.FileInfo;

// ============ avifenc 核心/线程/并发 矩阵压测 ============
// 用 CPU 亲和性掩码把 avifenc 进程限制到前 C 个核心（模拟 C 核机器），
// 配合 -j 每进程线程数与进程级并发 P，测吞吐 / CPU 利用率 / 内存。
// 用法：dotnet run -- --engine <avifenc.exe> --corpus <dir> --out <dir> [--files 8]
var opt = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
for (int i = 0; i < args.Length; i++)
{
    if (!args[i].StartsWith("--")) continue;
    string k = args[i][2..];
    opt[k] = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "true";
}
string Opt(string k, string def) => opt.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v : def;

string Engine = Opt("engine", "tools\\avifenc.exe");
string Corpus = Opt("corpus", "matrix-corpus");
string OutRoot = Opt("out", "matrix-out");
string ResultsDir = Opt("results", OutRoot);
int NFiles = int.Parse(Opt("files", "8"), CultureInfo.InvariantCulture);
int Quality = int.Parse(Opt("q", "60"), CultureInfo.InvariantCulture);
int Speed = int.Parse(Opt("s", "6"), CultureInfo.InvariantCulture);

int MachineCores = Environment.ProcessorCount;
IoDir.CreateDirectory(OutRoot);
IoDir.CreateDirectory(ResultsDir);

string[] allF = IoDir.GetFiles(Corpus).OrderBy(f => f, StringComparer.Ordinal).ToArray();
int stride = Math.Max(1, allF.Length / Math.Max(1, NFiles));
string[] files = allF.Where((f, i) => i % stride == 0).Take(NFiles).ToArray();
if (files.Length == 0) { Console.WriteLine("corpus empty"); return 2; }
Console.WriteLine($"[setup] machine cores={MachineCores}  engine={Engine}");
Console.WriteLine($"[setup] corpus={files.Length} files, q{Quality} s{Speed}, out={OutRoot}");

var results = new List<Dictionary<string, object>>();
var sampler = new CoreSampler();
sampler.Start();

async Task<Dictionary<string, object>> RunAsync(int cores, int threads, int parallelism, string tag)
{
    string outDir = IoPath.Combine(OutRoot, tag);
    IoDir.CreateDirectory(outDir);
    nint mask = cores >= MachineCores ? (nint)((1L << MachineCores) - 1) : (nint)((1L << cores) - 1);
    var sw = Stopwatch.StartNew();
    using var sem = new SemaphoreSlim(parallelism);
    var per = new List<double>();
    int ok = 0, fail = 0;
    long outBytes = 0;

    async Task EncodeOne(string f, int idx)
    {
        await sem.WaitAsync();
        try
        {
            string outp = IoPath.Combine(outDir, IoPath.GetFileName(f).Replace('.', '_') + ".avif");
            var psi = new ProcessStartInfo(Engine)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                WorkingDirectory = IoPath.GetTempPath(),
            };
            foreach (var a in new[] { "-c", "svt", "-q", Quality.ToString(), "-s", Speed.ToString(), "-j", threads.ToString(), "-y", "420", "-d", "10", f, outp })
                psi.ArgumentList.Add(a);
            var t = Stopwatch.StartNew();
            try
            {
                using var p = Process.Start(psi)!;
                try { p.ProcessorAffinity = mask; } catch { }
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                await p.WaitForExitAsync();
                t.Stop();
                if (p.ExitCode == 0 && IoFile.Exists(outp))
                {
                    Interlocked.Increment(ref ok);
                    lock (per) per.Add(t.Elapsed.TotalSeconds);
                    Interlocked.Add(ref outBytes, new IoInfo(outp).Length);
                }
                else { Interlocked.Increment(ref fail); }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref fail);
                Console.WriteLine("  proc error: " + ex.Message);
            }
        }
        finally { sem.Release(); }
    }

    await Task.WhenAll(files.Select((f, i) => EncodeOne(f, i)));
    sw.Stop();
    double wall = sw.Elapsed.TotalSeconds;
    var d = new Dictionary<string, object>
    {
        ["tag"] = tag, ["cores"] = cores, ["threads"] = threads, ["parallelism"] = parallelism,
        ["files"] = files.Length, ["ok"] = ok, ["failed"] = fail,
        ["wall_sec"] = Math.Round(wall, 2), ["files_per_sec"] = Math.Round(files.Length / wall, 2),
        ["perfile_avg_s"] = per.Count > 0 ? Math.Round(per.Average(), 2) : 0,
        ["perfile_p95_s"] = per.Count > 0 ? Math.Round(per.OrderBy(x => x).ElementAt((int)Math.Floor(0.95 * per.Count)), 2) : 0,
        ["out_mb"] = Math.Round(outBytes / 1048576.0, 1),
    };
    return d;
}

// ---------- 矩阵 ----------
// A: 核心扩展（j=1, P=C）
Console.WriteLine("\n===== A: core scaling (threads=1, parallelism=cores) =====");
foreach (int c in new[] { 1, 2, 4, 8, 16 })
{
    var d = await RunAsync(c, 1, c, $"A_c{c}_j1_p{c}");
    results.Add(d);
    Console.WriteLine($"  cores={c,2} j=1  P={c,2}: {d["files_per_sec"],6} files/s  wall={d["wall_sec"],6}s  avg={d["perfile_avg_s"],5}s  ok={d["ok"]}/{d["files"]}");
}

// B: 单进程多线程（C=4, P=1）
Console.WriteLine("\n===== B: threads within one process (cores=4, parallelism=1) =====");
foreach (int j in new[] { 1, 2, 4 })
{
    var d = await RunAsync(4, j, 1, $"B_c4_j{j}_p1");
    results.Add(d);
    Console.WriteLine($"  cores= 4 j={j,2}  P= 1: {d["files_per_sec"],6} files/s  wall={d["wall_sec"],6}s  avg={d["perfile_avg_s"],5}s  ok={d["ok"]}/{d["files"]}");
}

// C: 并发扩展（C=4, j=1）
Console.WriteLine("\n===== C: process concurrency (cores=4, threads=1) =====");
foreach (int p in new[] { 1, 2, 4, 8, 12 })
{
    var d = await RunAsync(4, 1, p, $"C_c4_j1_p{p}");
    results.Add(d);
    Console.WriteLine($"  cores= 4 j= 1  P={p,2}: {d["files_per_sec"],6} files/s  wall={d["wall_sec"],6}s  avg={d["perfile_avg_s"],5}s  ok={d["ok"]}/{d["files"]}");
}

// D: 交叉（小核机多进程 vs 大核机少线程）
Console.WriteLine("\n===== D: cross combinations =====");
foreach (var (c, j, p) in new[] { (2, 1, 2), (2, 2, 2), (2, 1, 4), (8, 1, 8), (8, 2, 2), (8, 4, 1) })
{
    var d = await RunAsync(c, j, p, $"D_c{c}_j{j}_p{p}");
    results.Add(d);
    Console.WriteLine($"  cores={c,2} j={j,2}  P={p,2}: {d["files_per_sec"],6} files/s  wall={d["wall_sec"],6}s  avg={d["perfile_avg_s"],5}s  ok={d["ok"]}/{d["files"]}");
}

sampler.Stop();
var summary = new Dictionary<string, object>
{
    ["machine_cores"] = MachineCores,
    ["files"] = files.Length,
    ["q"] = Quality, ["s"] = Speed,
    ["avg_core_utilization_of_allocated"] = Math.Round(sampler.UtilOfAllocated, 2),
    ["peak_absolute_cores_used"] = Math.Round(sampler.PeakAbsoluteCores, 1),
    ["peak_total_avifenc_rss_mb"] = Math.Round(sampler.PeakRssMb, 1),
    ["runs"] = results,
};
IoFile.WriteAllText(IoPath.Combine(ResultsDir, "matrix-results.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"\n[resources] allocated-core utilization(avg)={summary["avg_core_utilization_of_allocated"]}  peak abs cores={summary["peak_absolute_cores_used"]}  peak RSS={summary["peak_total_avifenc_rss_mb"]} MB");
Console.WriteLine("MATRIX DONE");
return 0;

// ---------- 采样器：绝对核心占用 + 相对分配核心的利用率 ----------
sealed class CoreSampler
{
    private readonly Thread _t;
    private volatile bool _run = true;
    private DateTime _last = DateTime.UtcNow;
    private TimeSpan _lastCpu = TimeSpan.Zero;
    private double _utilWeighted;
    private double _dtTotal;
    public double PeakAbsoluteCores { get; private set; }
    public double PeakRssMb { get; private set; }
    // 利用率按“分配核心数”归一需要知道每次运行的 C；这里近似取机器核心上限归一，保留绝对核心峰值
    public double UtilOfAllocated => _dtTotal > 0 ? _utilWeighted / _dtTotal : 0;
    public CoreSampler() { _t = new Thread(Loop) { IsBackground = true }; }
    public void Start() => _t.Start();
    public void Stop() { _run = false; _t.Join(1000); }
    private void Loop()
    {
        while (_run)
        {
            DateTime now = DateTime.UtcNow;
            double dt = (now - _last).TotalSeconds;
            TimeSpan cpu = TimeSpan.Zero;
            int count = 0;
            long rss = 0;
            try
            {
                foreach (var pr in Process.GetProcessesByName("avifenc"))
                {
                    count++;
                    try { rss += pr.WorkingSet64; } catch { }
                    try { cpu += pr.TotalProcessorTime; } catch { }
                }
            }
            catch { }
            if (dt > 0.001)
            {
                double abs = (cpu - _lastCpu).TotalSeconds / dt;
                _utilWeighted += abs * dt; // 绝对核×秒
                _dtTotal += dt;
                if (abs > PeakAbsoluteCores) PeakAbsoluteCores = abs;
                double rssMb = rss / 1048576.0;
                if (rssMb > PeakRssMb) PeakRssMb = rssMb;
            }
            _last = now;
            _lastCpu = cpu;
            Thread.Sleep(100);
        }
    }
}
