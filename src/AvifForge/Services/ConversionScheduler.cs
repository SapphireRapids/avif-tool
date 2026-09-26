using System.IO;
using AvifForge.Models;

namespace AvifForge.Services;

/// <summary>
/// 批量转换调度器：限制并发文件数，统一处理输出路径策略、取消与清理。
/// </summary>
public sealed class ConversionScheduler : IDisposable
{
    private readonly AvifEncRunner _runner;
    private readonly AppSettings _settings;
    private CancellationTokenSource? _cts;

    public ConversionScheduler(AvifEncRunner runner, AppSettings settings)
    {
        _runner = runner;
        _settings = settings;
    }

    public bool IsRunning { get; private set; }

    /// <summary>
    /// 运行一批任务。必须在 UI 线程调用（Progress&lt;T&gt; 与回调会切回 UI 线程）。
    /// </summary>
    public async Task RunAsync(
        IReadOnlyList<JobEntry> jobs,
        IProgress<double> progress,
        Action<JobEntry> onJobCompleted)
    {
        if (IsRunning || jobs.Count == 0)
        {
            return;
        }

        IsRunning = true;
        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;
        var syncContext = System.Threading.SynchronizationContext.Current;

        try
        {
            using var sem = new SemaphoreSlim(Math.Max(1, _settings.Parallelism));
            int done = 0;
            int total = jobs.Count;

            var tasks = jobs.Select(async job =>
            {
                try
                {
                    await sem.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    job.Status = JobStatus.Canceled;
                    return;
                }

                try
                {
                    if (ct.IsCancellationRequested)
                    {
                        job.Status = JobStatus.Canceled;
                        return;
                    }

                    await ExecuteJobAsync(job, ct).ConfigureAwait(false);
                }
                finally
                {
                    sem.Release();
                    int d = Interlocked.Increment(ref done);
                    double p = total == 0 ? 1 : (double)d / total;
                    Report(progress, p);
                    if (syncContext is not null)
                    {
                        syncContext.Post(_ => onJobCompleted(job), null);
                    }
                    else
                    {
                        onJobCompleted(job);
                    }
                }

                void Report(IProgress<double> pr, double v)
                {
                    pr.Report(v);
                }
            }).ToList();

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsRunning = false;
        }
    }

    public void Cancel()
    {
        _cts?.Cancel();
    }

    // 候选输出名预留表：保证同一批任务内（含同名不同扩展名的输入）输出路径互不重复。
    // 仅在单次 RunAsync 的并发窗口内持有，任务结束即释放，不影响跨批次的 rename 续号。
    private readonly object _reserveLock = new();
    private readonly HashSet<string> _reserved = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 在锁内原子挑选候选输出名：
    /// Rename —— 第一个「磁盘上不存在且未被本批预留」的名字；
    /// Skip   —— 目标名已存在或已被本批预留时跳过（避免与同胞任务撞同一路径）；
    /// Overwrite —— 固定用主名（用户显式选择覆盖，同路径是预期行为）。
    /// </summary>
    private string ReserveCandidate(string outDir, string baseName, out bool skip)
    {
        lock (_reserveLock)
        {
            string candidate = Path.Combine(outDir, baseName + ".avif");

            if (_settings.OverwritePolicy == OverwritePolicy.Overwrite)
            {
                skip = false;
                return candidate;
            }

            if (_settings.OverwritePolicy == OverwritePolicy.Skip)
            {
                bool usable = !File.Exists(candidate) && _reserved.Add(candidate);
                skip = !usable;
                return candidate;
            }

            // OverwritePolicy.Rename
            if (!File.Exists(candidate) && _reserved.Add(candidate))
            {
                skip = false;
                return candidate;
            }

            for (int n = 2; n < 10000; n++)
            {
                string alt = Path.Combine(outDir, $"{baseName} ({n}).avif");
                if (!File.Exists(alt) && _reserved.Add(alt))
                {
                    skip = false;
                    return alt;
                }
            }

            // 理论上不可达：同名输出已达 9999 个
            skip = false;
            return candidate;
        }
    }

    private void ReleaseCandidate(string candidate)
    {
        lock (_reserveLock)
        {
            _reserved.Remove(candidate);
        }
    }

    private async Task ExecuteJobAsync(JobEntry job, CancellationToken ct)
    {
        job.Status = JobStatus.Running;
        job.Error = null;

        // 记录编码前输出文件的状态，供失败清理判断「这个文件是不是被本次编码写坏的」
        bool preexisted = false;
        DateTime timestampBefore = default;

        string candidate = string.Empty;
        bool holdsReservation = false;
        try
        {
            string inputDir = Path.GetDirectoryName(job.InputPath)
                ?? throw new InvalidOperationException("无法解析源文件目录");

            string sub = _settings.SubfolderName.Trim();
            string outDir = sub.Length == 0 ? inputDir : Path.Combine(inputDir, sub);

            string baseName = Path.GetFileNameWithoutExtension(job.InputPath);

            // 原子分配候选名：同一调度内多个任务（含同名不同扩展名）并发时也不会选中同一路径。
            // 旧实现先 File.Exists 再写，两个同基件任务会同时选中同一候选文件并发写入，静默丢失一份输出。
            candidate = ReserveCandidate(outDir, baseName, out bool skipThis);
            if (skipThis)
            {
                job.Status = JobStatus.Skipped;
                job.Error = "输出已存在（策略：跳过）";
                return;
            }

            holdsReservation = true;
            Directory.CreateDirectory(outDir);
            job.OutputPath = candidate;
            preexisted = File.Exists(candidate);
            timestampBefore = preexisted ? GetLastWriteUtc(candidate) : default;

            var result = await _runner.EncodeAsync(job.InputPath, candidate, _settings, ct).ConfigureAwait(false);

            if (ct.IsCancellationRequested)
            {
                job.Status = JobStatus.Canceled;
                TryDeletePartial(candidate, preexisted, timestampBefore);
                return;
            }

            if (!result.Success)
            {
                job.Status = JobStatus.Failed;
                job.Error = result.ErrorDetail ?? "未知编码错误";
                TryDeletePartial(candidate, preexisted, timestampBefore);
                return;
            }

            job.ElapsedSeconds = result.ElapsedSeconds;
            job.CpuSeconds = result.CpuSeconds;
            job.OutputSize = new FileInfo(candidate).Length;
            job.Status = JobStatus.Done;

            if (_settings.DeleteSourceOnSuccess)
            {
                if (NativeFileOps.TryRecycle(job.InputPath))
                {
                    job.Error = "原图已移入回收站";
                }
                else
                {
                    job.Error = "原图删除失败（已保留）";
                }
            }
        }
        catch (OperationCanceledException)
        {
            job.Status = JobStatus.Canceled;
            if (job.OutputPath is not null)
            {
                TryDeletePartial(job.OutputPath, preexisted, timestampBefore);
            }
        }
        catch (Exception ex)
        {
            job.Status = JobStatus.Failed;
            job.Error = ex.Message;
            if (job.OutputPath is not null)
            {
                TryDeletePartial(job.OutputPath, preexisted, timestampBefore);
            }
        }
        finally
        {
            if (holdsReservation)
            {
                ReleaseCandidate(candidate);
            }
        }
    }

    /// <summary>
    /// 失败/取消后清理输出文件。编码中途失败会留下半截非零字节的 .avif，与 0 字节文件一样必须删掉；
    /// 唯一例外是覆盖策略下原本存在、且本次编码没有动过的旧文件（如进程未能启动），删它就是误删用户数据。
    /// </summary>
    private static void TryDeletePartial(string path, bool preexisted, DateTime timestampBefore)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            if (preexisted && GetLastWriteUtc(path) == timestampBefore)
            {
                return;
            }

            File.Delete(path);
        }
        catch
        {
            // 尽力清理
        }
    }

    private static DateTime GetLastWriteUtc(string path)
    {
        try
        {
            return new FileInfo(path).LastWriteTimeUtc;
        }
        catch
        {
            return default;
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        lock (_reserveLock)
        {
            _reserved.Clear();
        }
    }
}
