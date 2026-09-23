using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using AvifForge.Models;

namespace AvifForge.Services;

public sealed record EncodeResult(bool Success, double ElapsedSeconds, string? ErrorDetail);

/// <summary>
/// AVIF 编码引擎：libavif avifenc（构建时仅启用 SVT-AV1 编码器，AVX2/AVX-512 SIMD 由 SVT 内核运行时自动派发）。
/// 负责：定位可执行文件、探测版本、拼装参数、启动单次转换、追踪与清理子进程。
/// </summary>
public sealed partial class AvifEncRunner : IDisposable
{
    private readonly HashSet<Process> _running = new();
    private readonly object _sync = new();

    private string? _avifencPath;

    public bool EngineReady { get; private set; }

    public string EngineStatus { get; private set; } = "正在检测编码引擎…";

    public static readonly string[] SupportedExtensions = [".png", ".jpg", ".jpeg"];

    private static readonly Regex AdvancedTokenRegex = new(@"^[A-Za-z][A-Za-z0-9_]*(=[A-Za-z0-9_\-\.\+:,]{1,64})?$", RegexOptions.Compiled);

    public static bool IsSupportedImage(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return SupportedExtensions.Contains(ext);
    }

    /// <summary>定位 avifenc.exe（优先程序目录，其次 PATH）并读取版本能力。</summary>
    public async Task<bool> ProbeAsync(CancellationToken ct)
    {
        try
        {
            string local = Path.Combine(AppContext.BaseDirectory, "avifenc.exe");
            _avifencPath = File.Exists(local) ? local : FindInPath("avifenc.exe");

            if (_avifencPath is null)
            {
                EngineReady = false;
                EngineStatus = "未找到 avifenc.exe（应随应用一同安装）。";
                return false;
            }

            var (code, output) = await RunToolAsync(_avifencPath, ["--version"], ct);
            if (code != 0 || !output.Contains("svt [enc]", StringComparison.OrdinalIgnoreCase))
            {
                EngineReady = false;
                EngineStatus = "avifenc 不含 SVT-AV1 编码器，不满足“仅 SVT-AV1”要求。\n" + output.Trim();
                return false;
            }

            EngineReady = true;
            string versionLine = output.Split('\n')[0].Trim();
            EngineStatus = $"{versionLine}\n构建：libavif 上游官方源码 + SVT-AV1（静态、GitHub Actions CI 产物）\nSIMD：AVX2/AVX-512 由 SVT 内核按 CPU 自动派发";
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            EngineReady = false;
            EngineStatus = $"引擎探测失败：{ex.Message}";
            return false;
        }
    }

    private static string? FindInPath(string exeName)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim(), exeName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // 非法 PATH 段忽略
            }
        }

        return null;
    }

    /// <summary>转换单张图片（透明通道由 libavif 自动拆分并同样以 SVT 编码）。取消时杀死子进程树。</summary>
    public async Task<EncodeResult> EncodeAsync(string input, string output, AppSettings settings, CancellationToken ct)
    {
        if (_avifencPath is null)
        {
            return new EncodeResult(false, 0, "avifenc 未定位");
        }

        var sw = Stopwatch.StartNew();
        var psi = new ProcessStartInfo(_avifencPath)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
        };
        foreach (string arg in BuildArguments(input, output, settings))
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = new Process { StartInfo = psi };
        var logSb = new StringBuilder();
        proc.ErrorDataReceived += (_, e) => AppendLog(logSb, e.Data);
        proc.OutputDataReceived += (_, e) => AppendLog(logSb, e.Data);

        try
        {
            proc.Start();
            proc.BeginErrorReadLine();
            proc.BeginOutputReadLine();
            Track(proc);

            await proc.WaitForExitAsync(ct);
            sw.Stop();

            if (ct.IsCancellationRequested)
            {
                return new EncodeResult(false, sw.Elapsed.TotalSeconds, "已取消");
            }

            bool ok = proc.ExitCode == 0 && File.Exists(output) && new FileInfo(output).Length > 0;
            return new EncodeResult(ok, sw.Elapsed.TotalSeconds, ok ? null : CompactError(logSb.ToString(), proc.ExitCode));
        }
        catch (OperationCanceledException)
        {
            TryKill(proc);
            sw.Stop();
            return new EncodeResult(false, sw.Elapsed.TotalSeconds, "已取消");
        }
        catch (Exception ex)
        {
            TryKill(proc);
            sw.Stop();
            return new EncodeResult(false, sw.Elapsed.TotalSeconds, ex.Message);
        }
        finally
        {
            Untrack(proc);
        }
    }

    /// <summary>组装 avifenc 命令行（ArgumentList 传参，不拼接字符串，避免注入）。</summary>
    public List<string> BuildArguments(string input, string output, AppSettings settings)
    {
        var args = new List<string>
        {
            "-c", "svt",
        };

        if (settings.MaxQuality)
        {
            args.AddRange(["-q", "100"]);
            args.AddRange(["--qalpha", "100"]);
        }
        else
        {
            args.AddRange(["-q", Math.Clamp(settings.Quality, AppSettings.QualityMin, AppSettings.QualityMax).ToString()]);
            if (settings.QualityAlpha > 0)
            {
                args.AddRange(["--qalpha", Math.Clamp(settings.QualityAlpha, AppSettings.QualityMin, AppSettings.QualityMax).ToString()]);
            }
        }

        args.AddRange(["-s", Math.Clamp(settings.Speed, AppSettings.MinSpeed, AppSettings.MaxSpeed).ToString()]);

        if (settings.JobsPerImage > 0)
        {
            args.AddRange(["-j", settings.JobsPerImage.ToString()]);
        }

        // SVT-AV1 v4.x 彩色图像仅接受 4:2:0；不传 --sharpyuv（此静态构建的 libyuv 在 Windows 上转换失败）。
        args.AddRange(["-y", "420"]);

        args.AddRange(["-d", settings.Depth switch
        {
            DepthChoice.Bit12 => "12",
            DepthChoice.Bit10 => "10",
            _ => "8",
        }]);

        if (settings.Range == RangeChoice.Limited)
        {
            args.AddRange(["-r", "limited"]);
        }
        else if (settings.Range == RangeChoice.Full)
        {
            args.AddRange(["-r", "full"]);
        }

        if (!settings.KeepMetadata)
        {
            args.AddRange(["--ignore-exif", "--ignore-xmp", "--ignore-icc"]);
        }

        foreach (string token in settings.AdvancedParams.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (AdvancedTokenRegex.IsMatch(token))
            {
                args.Add("-a");
                args.Add(token);
            }
        }

        args.Add(input);
        args.Add(output);
        return args;
    }

    private static void AppendLog(StringBuilder sb, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (sb)
        {
            if (sb.Length < 8000)
            {
                sb.AppendLine(line);
            }
        }
    }

    private static string CompactError(string log, int exitCode)
    {
        string text = log.Trim();
        if (text.Length == 0)
        {
            return $"avifenc 退出码 {exitCode}";
        }

        string[] lines = text.Split('\n');
        string result = string.Join(" | ", lines.Where(l => l.Contains("ERROR", StringComparison.OrdinalIgnoreCase) || l.Contains("error", StringComparison.Ordinal)).TakeLast(3)).Trim();
        if (result.Length == 0)
        {
            result = string.Join(" | ", lines.TakeLast(2)).Trim();
        }

        return result.Length > 400 ? result[..400] : result;
    }

    private async Task<(int Code, string Output)> RunToolAsync(string exe, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = new Process { StartInfo = psi };
        var outSb = new StringBuilder();
        proc.OutputDataReceived += (_, e) => AppendLog(outSb, e.Data);
        proc.ErrorDataReceived += (_, e) => AppendLog(outSb, e.Data);

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        Track(proc);
        try
        {
            await proc.WaitForExitAsync(ct);
            return (proc.ExitCode, outSb.ToString());
        }
        finally
        {
            Untrack(proc);
        }
    }

    private void Track(Process p)
    {
        lock (_sync)
        {
            _running.Add(p);
        }
    }

    private void Untrack(Process p)
    {
        lock (_sync)
        {
            _running.Remove(p);
        }
    }

    private static void TryKill(Process p)
    {
        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 进程可能刚好已退出
        }
    }

    /// <summary>退出清理：杀掉所有在跑的 avifenc 子进程。</summary>
    public void KillAll()
    {
        Process[] snapshot;
        lock (_sync)
        {
            snapshot = [.. _running];
        }

        foreach (Process p in snapshot)
        {
            TryKill(p);
        }
    }

    public void Dispose()
    {
        KillAll();
        EngineReady = false;
    }
}
