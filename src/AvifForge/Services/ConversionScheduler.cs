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
        Action onJobCompleted)
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
                        syncContext.Post(_ => onJobCompleted(), null);
                    }
                    else
                    {
                        onJobCompleted();
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

    private async Task ExecuteJobAsync(JobEntry job, CancellationToken ct)
    {
        job.Status = JobStatus.Running;
        job.Error = null;

        string candidate;
        try
        {
            string inputDir = Path.GetDirectoryName(job.InputPath)
                ?? throw new InvalidOperationException("无法解析源文件目录");

            string sub = _settings.SubfolderName.Trim();
            string outDir = sub.Length == 0 ? inputDir : Path.Combine(inputDir, sub);

            string baseName = Path.GetFileNameWithoutExtension(job.InputPath);
            candidate = Path.Combine(outDir, baseName + ".avif");

            if (File.Exists(candidate))
            {
                switch (_settings.OverwritePolicy)
                {
                    case OverwritePolicy.Skip:
                        job.Status = JobStatus.Skipped;
                        job.Error = "输出已存在（策略：跳过）";
                        return;
                    case OverwritePolicy.Rename:
                        for (int n = 2; n < 10000; n++)
                        {
                            string alt = Path.Combine(outDir, $"{baseName} ({n}).avif");
                            if (!File.Exists(alt))
                            {
                                candidate = alt;
                                break;
                            }
                        }

                        break;
                    // Overwrite：保持 candidate 原样
                }
            }

            Directory.CreateDirectory(outDir);
            job.OutputPath = candidate;

            var result = await _runner.EncodeAsync(job.InputPath, candidate, _settings, ct).ConfigureAwait(false);

            if (ct.IsCancellationRequested)
            {
                job.Status = JobStatus.Canceled;
                TryDeletePartial(candidate);
                return;
            }

            if (!result.Success)
            {
                job.Status = JobStatus.Failed;
                job.Error = result.ErrorDetail ?? "未知编码错误";
                TryDeletePartial(candidate);
                return;
            }

            job.ElapsedSeconds = result.ElapsedSeconds;
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
                TryDeletePartial(job.OutputPath);
            }
        }
        catch (Exception ex)
        {
            job.Status = JobStatus.Failed;
            job.Error = ex.Message;
            if (job.OutputPath is not null)
            {
                TryDeletePartial(job.OutputPath);
            }
        }
    }

    private static void TryDeletePartial(string path)
    {
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length == 0)
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 尽力清理
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
