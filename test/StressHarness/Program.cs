
using System.Diagnostics;
using System.Text.Json;
using AvifForge.Models;
using AvifForge.Services;
using IoPath = System.IO.Path;
using IoFile = System.IO.File;
using IoDir = System.IO.Directory;
using IoInfo = System.IO.FileInfo;

// ============ avif tool 压力回归 harness ============
// 走应用真实代码路径：AvifEncRunner.EncodeAsync（每文件一个 avifenc 子进程）+ ConversionScheduler。
// 用法：
//   dotnet run --project test\StressHarness -c Release -- --corpus <dir> --out <dir> [--engine <avifenc.exe>] [--race-in <dir>] [--avifdec <exe>] [--quick]
// 语料用 test\StressHarness\gen-corpus.py 生成（--scale quick|full）。

// ---------- 参数 ----------
var opt = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
for (int i = 0; i < args.Length; i++)
{
    if (!args[i].StartsWith("--")) continue;
    string key = args[i][2..];
    opt[key] = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "true";
}
string Opt(string k, string def) => opt.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v : def;
bool Quick = opt.ContainsKey("quick");

string Corpus = Opt("corpus", "stress-corpus");
string OutRoot = Opt("out", "stress-out");
string ResultsDir = Opt("results", OutRoot);
string RaceIn = Opt("race-in", IoPath.Combine(IoPath.GetDirectoryName(OutRoot.TrimEnd('/', '\\')) ?? ".", "race-in"));
string AvifDec = Opt("avifdec", "");
bool HasDecode() => AvifDec.Length > 0 && IoFile.Exists(AvifDec);

IoDir.CreateDirectory(OutRoot);
IoDir.CreateDirectory(ResultsDir);

// 引擎定位：--engine > 本进程目录 > 向上找 tools\avifenc.exe
string? engineArg = Opt("engine", "") is { Length: > 0 } s ? s : null;
string engineDst = IoPath.Combine(AppContext.BaseDirectory, "avifenc.exe");
if (engineArg is not null && IoFile.Exists(engineArg))
{
    if (!IoFile.Exists(engineDst)) IoFile.Copy(engineArg, engineDst, true);
}
else
{
    for (var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
    {
        string cand = IoPath.Combine(d.FullName, "tools", "avifenc.exe");
        if (IoFile.Exists(cand)) { if (!IoFile.Exists(engineDst)) IoFile.Copy(cand, engineDst, true); break; }
    }
}
if (!IoFile.Exists(engineDst))
{
    Console.WriteLine("engine missing: pass --engine <avifenc.exe> or place it next to this exe / in tools\\avifenc.exe");
    return 2;
}

Cfg.ResultsDir = ResultsDir;
var reporter = new Reporter();
using var sampler = new Sampler();
sampler.Start();

var runner = new AvifEncRunner();
bool probed = await runner.ProbeAsync(default);
Console.WriteLine($"[setup] engine probe={probed}  {runner.EngineStatus.Split('\n')[0]}");
if (!probed) { Console.WriteLine("ENGINE NOT READY"); return 2; }

if (!IoDir.Exists(Corpus) || IoDir.GetFiles(Corpus).Length == 0)
{
    Console.WriteLine($"corpus empty: {Corpus}. generate with: python test\\StressHarness\\gen-corpus.py --out <dir> --scale quick");
    return 2;
}
string[] all = IoDir.GetFiles(Corpus).OrderBy(f => f, StringComparer.Ordinal).ToArray();
// 干净集：剔除与 PNG 同基名的 JPG（隔离 rename 竞态变量，Phase 6 专项覆盖）
string[] clean = all.Where(f => !f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)).ToArray();
string[] screens = clean.Where(f => IoPath.GetFileName(f).StartsWith("screen_")).ToArray();
int engineFiles = Quick ? Math.Min(8, clean.Length) : Math.Min(24, clean.Length);
var levelFiles = clean.Where(f => IoPath.GetFileName(f).StartsWith("photo_")).Take(engineFiles).ToArray();
string[] pipeFiles = Quick ? clean.Take(Math.Min(24, clean.Length)).ToArray() : clean;
int cancelFiles = Quick ? Math.Min(12, screens.Length) : screens.Length;
int racePairs = Quick ? 4 : 12;
Console.WriteLine($"[setup] corpus={all.Length} clean={clean.Length} size={clean.Sum(f => new IoInfo(f).Length) / 1048576.0:N1} MB quick={Quick}");

// ---------- Phase 1: 引擎并发扩展性 ----------
async Task<PhaseStats> EngineLevelAsync(int conc, string[] files, int quality, int speed)
{
    string outDir = IoPath.Combine(OutRoot, $"engine_c{conc}");
    IoDir.CreateDirectory(outDir);
    var settings = new AppSettings { Quality = quality, Speed = speed, Depth = DepthChoice.Bit10, JobsPerImage = 1, KeepMetadata = true };
    var sw = Stopwatch.StartNew();
    using var sem = new SemaphoreSlim(conc);
    var tasks = files.Select(async f =>
    {
        await sem.WaitAsync();
        try
        {
            string outp = IoPath.Combine(outDir, IoPath.GetFileName(f).Replace('.', '_') + ".avif");
            return await runner.EncodeAsync(f, outp, settings, default);
        }
        finally { sem.Release(); }
    }).ToArray();
    var results = await Task.WhenAll(tasks);
    sw.Stop();
    return PhaseStats.From($"engine-c{conc}-q{quality}-s{speed}", files, results, sw.Elapsed.TotalSeconds, outDir);
}

Console.WriteLine($"\n===== PHASE 1: engine concurrency scaling (photo x{levelFiles.Length}, q60 s6) =====");
foreach (int c in Quick ? new[] { 1, 4 } : new[] { 1, 4, 8, 16 })
{
    var st = await EngineLevelAsync(c, levelFiles, 60, 6);
    reporter.Report(st);
    Console.WriteLine($"[phase1] c={c,2}  {st.Files / st.WallSec,5:F2} files/s  wall={st.PerFile.Average():F2}s  cpu={st.Cpu.DefaultIfEmpty(0).Average():F2}s  ok={st.Ok} fail={st.Failed}");
}

// ---------- 管线 runner ----------
async Task<PhaseStats> PipelineAsync(string tag, string[] files, AppSettings settings, string outDir)
{
    IoDir.CreateDirectory(outDir);
    var jobs = files.Select(f => new JobEntry(f)).ToList();
    var sw = Stopwatch.StartNew();
    var scheduler = new ConversionScheduler(runner, settings);
    await scheduler.RunAsync(jobs, new Progress<double>(_ => { }), _ => { });
    sw.Stop();
    scheduler.Dispose();
    var el = jobs.Where(j => j.Status == JobStatus.Done).Select(j => j.ElapsedSeconds).ToList();
    var cpu = jobs.Where(j => j.Status == JobStatus.Done).Select(j => j.CpuSeconds).ToList();
    return new PhaseStats
    {
        Name = tag, Files = files.Length, OutDir = outDir,
        Ok = jobs.Count(j => j.Status == JobStatus.Done),
        Failed = jobs.Count(j => j.Status == JobStatus.Failed),
        Canceled = jobs.Count(j => j.Status == JobStatus.Canceled),
        Skipped = jobs.Count(j => j.Status == JobStatus.Skipped),
        WallSec = sw.Elapsed.TotalSeconds,
        PerFile = el, Cpu = cpu,
        InBytes = files.Sum(f => new IoInfo(f).Length),
        OutBytes = IoDir.Exists(outDir) ? IoDir.GetFiles(outDir).Sum(f => new IoInfo(f).Length) : 0,
    };
}

AppSettings PipeSettings(string outDir, OverwritePolicy policy, int speed = 8) => new AppSettings
{
    Quality = 50, Speed = speed, Depth = DepthChoice.Bit10,
    Parallelism = Math.Min(16, Math.Max(1, Environment.ProcessorCount)),
    JobsPerImage = 1, SubfolderName = outDir, OverwritePolicy = policy, KeepMetadata = true,
};

// ---------- Phase 2: 应用管线 x2（第二轮 rename） ----------
Console.WriteLine($"\n===== PHASE 2: app pipeline x2 (rename on pass2), corpus={pipeFiles.Length} =====");
var pipe1 = IoPath.Combine(OutRoot, "pipe1");
var ps = PipeSettings(pipe1, OverwritePolicy.Rename);
ps.ClampValues();
var p1 = await PipelineAsync("pipeline-pass1", pipeFiles, ps, pipe1);
reporter.Report(p1);
Console.WriteLine($"[phase2] pass1: {p1.Ok} ok / {p1.Failed} fail, {p1.Files / p1.WallSec:F2} files/s, wall={p1.WallSec:F1}s, cpu-avg={p1.Cpu.DefaultIfEmpty(0).Average():F2}s, saved {100.0 * (1 - (double)p1.OutBytes / p1.InBytes):F1}%");
var p2 = await PipelineAsync("pipeline-pass2-rename", pipeFiles, ps, pipe1);
reporter.Report(p2);
int renamed2 = IoDir.GetFiles(pipe1).Count(f => f.Contains("(2).avif"));
Console.WriteLine($"[phase2] pass2: {p2.Ok} ok / {p2.Failed} fail; '(2)' files={renamed2} (expect {pipeFiles.Length}); pipe1 total={IoDir.GetFiles(pipe1).Length} (expect {pipeFiles.Length * 2})");

// ---------- Phase 3: 取消清理 ----------
Console.WriteLine($"\n===== PHASE 3: cancel/cleanup ({cancelFiles} files, s2, parallelism=16) =====");
{
    string outDir = IoPath.Combine(OutRoot, "cancel");
    IoDir.CreateDirectory(outDir);
    var settings = new AppSettings
    {
        Quality = 60, Speed = 2, Depth = DepthChoice.Bit10,
        Parallelism = Math.Min(16, Math.Max(1, Environment.ProcessorCount)), JobsPerImage = 1,
        SubfolderName = outDir, OverwritePolicy = OverwritePolicy.Rename, KeepMetadata = true,
    };
    settings.ClampValues();
    var jobs = screens.Take(cancelFiles).Select(f => new JobEntry(f)).ToList();
    var scheduler = new ConversionScheduler(runner, settings);
    var runTask = scheduler.RunAsync(jobs, new Progress<double>(_ => { }), _ => { });
    await Task.Delay(3000);
    int procsBefore = Process.GetProcessesByName("avifenc").Length;
    var swCancel = Stopwatch.StartNew();
    scheduler.Cancel();
    await runTask;
    int waited = 0;
    while (Process.GetProcessesByName("avifenc").Length > 0 && waited < 10000) { await Task.Delay(100); waited += 100; }
    swCancel.Stop();
    int procsAfter = Process.GetProcessesByName("avifenc").Length;
    await Task.Delay(600);
    int procsLater = Process.GetProcessesByName("avifenc").Length;
    string[] leftFiles = IoDir.GetFiles(outDir);
    int zeroByte = leftFiles.Count(f => new IoInfo(f).Length == 0);
    scheduler.Dispose();
    int d = jobs.Count(j => j.Status == JobStatus.Done), c2 = jobs.Count(j => j.Status == JobStatus.Canceled), f2 = jobs.Count(j => j.Status == JobStatus.Failed);
    Console.WriteLine($"[phase3] procs before={procsBefore} after={procsAfter} later={procsLater}, time-to-zero={swCancel.ElapsedMilliseconds}ms");
    Console.WriteLine($"[phase3] jobs done={d} canceled={c2} failed={f2}; files left={leftFiles.Length}, zero-byte={zeroByte}");
    Console.WriteLine($"[phase3] PASS={procsAfter == 0 && zeroByte == 0 && f2 == 0}");
    reporter.Raw["cancel_cleanup"] = new Dictionary<string, object>
    {
        ["procs_before"] = procsBefore, ["procs_after"] = procsAfter, ["procs_later"] = procsLater,
        ["time_to_zero_ms"] = swCancel.ElapsedMilliseconds,
        ["done"] = d, ["canceled"] = c2, ["failed"] = f2,
        ["files_left"] = leftFiles.Length, ["zero_byte_left"] = zeroByte,
    };
}

// ---------- Phase 5: 极限档 rename 风暴 ----------
if (!Quick)
{
    Console.WriteLine($"\n===== PHASE 5: extreme {pipeFiles.Length * 5} jobs (clean corpus x5, q50 s10) =====");
    var files5 = Enumerable.Repeat(pipeFiles, 5).SelectMany(x => x).ToArray();
    var s5 = PipeSettings(IoPath.Combine(OutRoot, "pipe2"), OverwritePolicy.Rename, 10);
    s5.ClampValues();
    string out5 = IoPath.Combine(OutRoot, "pipe2");
    var st5 = await PipelineAsync("extreme-rename-storm", files5, s5, out5);
    reporter.Report(st5);
    Console.WriteLine($"[phase5] {st5.Ok} ok / {st5.Failed} fail, wall={st5.WallSec:F1}s, {(st5.Files / st5.WallSec):F1} files/s; pipe2 files={IoDir.GetFiles(out5).Length} (expect {files5.Length})");
}

// ---------- Phase 6: 同名基件竞态 + 策略回归 ----------
Console.WriteLine($"\n===== PHASE 6: same-basename race ({racePairs} pairs png+jpg) + policy regression =====");
{
    string outDir = IoPath.Combine(OutRoot, "race");
    IoDir.CreateDirectory(outDir);
    IoDir.CreateDirectory(RaceIn);
    var pairs = new List<string>();
    string srcPng = all.FirstOrDefault(f => f.EndsWith(".png")) ?? all[0];
    string srcJpg = all.FirstOrDefault(f => f.EndsWith(".jpg")) ?? all[0];
    for (int i = 0; i < racePairs; i++)
    {
        string p = IoPath.Combine(RaceIn, $"racer_{i:00}.png");
        string j = IoPath.Combine(RaceIn, $"racer_{i:00}.jpg");
        if (!IoFile.Exists(p)) IoFile.Copy(srcPng, p, true);
        if (!IoFile.Exists(j)) IoFile.Copy(srcJpg, j, true);
        pairs.Add(p); pairs.Add(j);
    }
    var s6 = PipeSettings(outDir, OverwritePolicy.Rename, 10);
    s6.ClampValues();
    var st6 = await PipelineAsync("race-same-basename", pairs.ToArray(), s6, outDir);
    reporter.Report(st6);
    int files6 = IoDir.GetFiles(outDir).Length;
    Console.WriteLine($"[phase6] pass1: jobs={pairs.Count} ok={st6.Ok} failed={st6.Failed}; output files={files6} (expect {pairs.Count}; fewer => 竞态未修)");
    var st6b = await PipelineAsync("race-same-basename-pass2", pairs.ToArray(), s6, outDir);
    reporter.Report(st6b);
    Console.WriteLine($"[phase6] pass2: ok={st6b.Ok}; files={IoDir.GetFiles(outDir).Length} (expect {pairs.Count * 2})");
    reporter.Raw["race"] = new Dictionary<string, object>
    {
        ["pass1_files"] = files6, ["pass2_files"] = IoDir.GetFiles(outDir).Length,
        ["jobs_per_pass"] = pairs.Count, ["ok1"] = st6.Ok, ["ok2"] = st6b.Ok,
    };

    // 6b: Skip / Overwrite 策略回归
    string outSkip = IoPath.Combine(OutRoot, "policy_skip");
    string outOver = IoPath.Combine(OutRoot, "policy_over");
    IoDir.CreateDirectory(outSkip);
    IoDir.CreateDirectory(outOver);
    foreach (var f in pairs.Where(x => x.EndsWith(".png")))
    {
        string plain = IoPath.Combine(outSkip, IoPath.GetFileNameWithoutExtension(f) + ".avif");
        if (!IoFile.Exists(plain) && IoDir.GetFiles(outDir).Length > 0)
            IoFile.Copy(IoDir.GetFiles(outDir)[0], plain, true);
    }
    var skipSet = PipeSettings(outSkip, OverwritePolicy.Skip, 10);
    skipSet.ClampValues();
    var stSkip = await PipelineAsync("policy-skip-existing", pairs.ToArray(), skipSet, outSkip);
    reporter.Report(stSkip);
    int skipFiles = IoDir.GetFiles(outSkip).Length;
    Console.WriteLine($"[phase6b] Skip: ok={stSkip.Ok} skipped={stSkip.Skipped} failed={stSkip.Failed}; dir files={skipFiles} (expect {racePairs} untouched)");

    var overSet = PipeSettings(outOver, OverwritePolicy.Overwrite, 10);
    overSet.ClampValues();
    var stOver1 = await PipelineAsync("policy-overwrite-fresh", pairs.ToArray(), overSet, outOver);
    reporter.Report(stOver1);
    var stOver2 = await PipelineAsync("policy-overwrite-again", pairs.ToArray(), overSet, outOver);
    reporter.Report(stOver2);
    int overFiles = IoDir.GetFiles(outOver).Length;
    Console.WriteLine($"[phase6b] Overwrite: pass1 ok={stOver1.Ok} failed={stOver1.Failed}; pass2 ok={stOver2.Ok} failed={stOver2.Failed}; dir files={overFiles} (expect {racePairs})");
    reporter.Raw["policy_regression"] = new Dictionary<string, object>
    {
        ["skip_ok"] = stSkip.Ok, ["skip_skipped"] = stSkip.Skipped, ["skip_failed"] = stSkip.Failed, ["skip_dir_files"] = skipFiles,
        ["overwrite_ok1"] = stOver1.Ok, ["overwrite_failed1"] = stOver1.Failed,
        ["overwrite_ok2"] = stOver2.Ok, ["overwrite_failed2"] = stOver2.Failed, ["overwrite_dir_files"] = overFiles,
    };
}

// ---------- Phase 4: 输出合法性 ----------
Console.WriteLine("\n===== PHASE 4: output validity (ALL dirs) =====");
{
    var allOut = IoDir.GetFiles(OutRoot, "*.avif", System.IO.SearchOption.AllDirectories);
    int badHeader = 0, badBrand = 0, empty = 0;
    foreach (string f in allOut)
    {
        if (new IoInfo(f).Length == 0) { empty++; continue; }
        byte[] head = new byte[16];
        using var fh = IoFile.OpenRead(f);
        int n = fh.Read(head, 0, 16);
        if (n < 12 || head[4] != (byte)'f' || head[5] != (byte)'t' || head[6] != (byte)'y' || head[7] != (byte)'p') { badHeader++; continue; }
        string brand = System.Text.Encoding.ASCII.GetString(head, 8, 4);
        if (brand != "avif" && brand != "avis") badBrand++;
    }
    Console.WriteLine($"[phase4] all outputs={allOut.Length} badHeader={badHeader} badBrand={badBrand} empty={empty}");
    reporter.Raw["validity"] = new Dictionary<string, object> { ["files"] = allOut.Length, ["bad_header"] = badHeader, ["bad_brand"] = badBrand, ["empty"] = empty };

    int decOk = 0, decTotal = 0;
    if (HasDecode())
    {
        var samples = allOut.Where((f, i) => i % Math.Max(1, allOut.Length / (Quick ? 4 : 10)) == 0).Take(Quick ? 4 : 10).ToArray();
        decTotal = samples.Length;
        foreach (string f in samples)
        {
            string png = IoPath.Combine(IoPath.GetTempPath(), "avifdec_check.png");
            try
            {
                var psi = new ProcessStartInfo(AvifDec) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
                psi.ArgumentList.Add(f); psi.ArgumentList.Add(png);
                using var p = Process.Start(psi)!;
                p.WaitForExit(20000);
                if (p.ExitCode == 0 && IoFile.Exists(png) && new IoInfo(png).Length > 0) decOk++;
            }
            catch { }
        }
        Console.WriteLine($"[phase4] avifdec decode: {decOk}/{decTotal} OK");
    }
    else
    {
        Console.WriteLine("[phase4] avifdec not provided -> decode check skipped");
    }
    reporter.Raw["validity_decode"] = new Dictionary<string, object> { ["ok"] = decOk, ["total"] = decTotal, ["skipped"] = !HasDecode() };
}

// ---------- 汇总 ----------
reporter.Raw["harness_peak_ws_mb"] = Math.Round(Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0, 1);
reporter.Raw["max_concurrent_avifenc"] = sampler.MaxProcs;
reporter.Raw["avg_cores_used"] = Math.Round(sampler.AvgCores, 1);
reporter.Raw["peak_cores_used"] = Math.Round(sampler.PeakCores, 1);
reporter.Raw["peak_total_avifenc_rss_mb"] = Math.Round(sampler.PeakRssMb, 1);
reporter.Raw["env"] = new Dictionary<string, object>
{
    ["cpu_cores"] = Environment.ProcessorCount,
    ["os"] = Environment.OSVersion.VersionString,
    ["available_memory_mb"] = AppSettings.AvailableMemoryBytes / (1024 * 1024),
    ["max_parallelism_effective"] = new AppSettings().MaxParallelism,
    ["max_parallelism_by_memory"] = AppSettings.MaxParallelismByMemory,
};
sampler.Stop();
runner.Dispose();
reporter.Save();
Console.WriteLine("\nALL PHASES DONE");
return 0;

// ---------- 辅助类型 ----------
internal static class Cfg
{
    public static string ResultsDir = string.Empty;
}

sealed class Reporter
{
    public Dictionary<string, object> Raw { get; } = new();
    private readonly List<PhaseStats> _stats = new();
    public void Report(PhaseStats st) => _stats.Add(st);
    public void Save()
    {
        foreach (var st in _stats)
        {
            Raw["phase_" + st.Name] = new Dictionary<string, object>
            {
                ["files"] = st.Files, ["ok"] = st.Ok, ["failed"] = st.Failed, ["canceled"] = st.Canceled, ["skipped"] = st.Skipped,
                ["wall_sec"] = Math.Round(st.WallSec, 2),
                ["files_per_sec"] = st.WallSec > 0 ? Math.Round(st.Files / st.WallSec, 2) : 0,
                ["perfile_wall_avg_s"] = st.PerFile.Count > 0 ? Math.Round(st.PerFile.Average(), 2) : 0,
                ["perfile_cpu_avg_s"] = st.Cpu.Count > 0 ? Math.Round(st.Cpu.Average(), 3) : 0,
                ["perfile_cpu_total_s"] = st.Cpu.Count > 0 ? Math.Round(st.Cpu.Sum(), 2) : 0,
                ["in_mb"] = Math.Round(st.InBytes / 1048576.0, 1), ["out_mb"] = Math.Round(st.OutBytes / 1048576.0, 1),
                ["compress_pct"] = st.InBytes > 0 ? Math.Round(100.0 * (1 - (double)st.OutBytes / st.InBytes), 1) : 0,
            };
        }
        string json = JsonSerializer.Serialize(Raw, new JsonSerializerOptions { WriteIndented = true });
        IoFile.WriteAllText(IoPath.Combine(Cfg.ResultsDir, "stress-results.json"), json);
        Console.WriteLine("\n===== SUMMARY =====");
        foreach (var kv in Raw) Console.WriteLine(kv.Key + " : " + JsonSerializer.Serialize(kv.Value));
    }
}

sealed class PhaseStats
{
    public string Name = ""; public int Files, Ok, Failed, Canceled, Skipped;
    public double WallSec; public List<double> PerFile = new(); public List<double> Cpu = new(); public string OutDir = "";
    public long InBytes, OutBytes;
    public static PhaseStats From(string name, string[] files, EncodeResult[] results, double wall, string outDir)
    {
        var st = new PhaseStats
        {
            Name = name, Files = files.Length, WallSec = wall, OutDir = outDir,
            InBytes = files.Sum(f => new IoInfo(f).Length),
            OutBytes = IoDir.Exists(outDir) ? IoDir.GetFiles(outDir).Sum(f => new IoInfo(f).Length) : 0,
        };
        foreach (var r in results)
        {
            if (r.Success) st.Ok++; else st.Failed++;
            st.PerFile.Add(r.ElapsedSeconds);
            st.Cpu.Add(r.CpuSeconds);
        }
        return st;
    }
}

sealed class Sampler : IDisposable
{
    private readonly Thread _t;
    private volatile bool _run = true;
    private DateTime _last = DateTime.UtcNow;
    private TimeSpan _lastCpu = TimeSpan.Zero;
    private double _coresWeighted;
    private double _dtTotal;
    public int MaxProcs { get; private set; }
    public double AvgCores => _dtTotal > 0 ? _coresWeighted / _dtTotal : 0;
    public double PeakCores { get; private set; }
    public double PeakRssMb { get; private set; }
    public Sampler() { _t = new Thread(Loop) { IsBackground = true }; }
    public void Start() => _t.Start();
    public void Stop() { _run = false; _t.Join(1000); }
    public void Dispose() => Stop();
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
                Process[] procs = Process.GetProcessesByName("avifenc");
                count = procs.Length;
                foreach (var pr in procs)
                {
                    try { rss += pr.WorkingSet64; } catch { }
                    try { cpu += pr.TotalProcessorTime; } catch { }
                }
            }
            catch { }
            if (dt > 0.001)
            {
                double cores = (cpu - _lastCpu).TotalSeconds / dt;
                if (cores < 0) cores = 0;
                _coresWeighted += cores * dt;
                _dtTotal += dt;
                if (cores > PeakCores) PeakCores = cores;
                if (count > MaxProcs) MaxProcs = count;
                double rssMb = rss / 1048576.0;
                if (rssMb > PeakRssMb) PeakRssMb = rssMb;
            }
            _last = now;
            _lastCpu = cpu;
            Thread.Sleep(200);
        }
    }
}
