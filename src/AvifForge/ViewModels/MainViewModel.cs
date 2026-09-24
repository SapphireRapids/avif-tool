using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using AvifForge.Models;
using AvifForge.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AvifForge.ViewModels;

public sealed record OptionItem<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

public partial class MainViewModel : ObservableObject
{
    public const int MaxQueueSize = 20_000;

    public AppSettings Settings { get; }

    private readonly SettingsStore _store;
    private readonly AvifEncRunner _runner;
    private readonly ConversionScheduler _scheduler;
    private readonly HashSet<string> _queuedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<JobEntry, JobAggregate> _aggregates = new();
    private readonly DispatcherTimer _clockTimer;
    private DateTime _batchStartUtc;

    public ObservableCollection<JobEntry> Jobs { get; } = new();

    public IReadOnlyList<OptionItem<DepthChoice>> DepthOptions { get; } =
    [
        new(DepthChoice.Bit8, "8 bit（兼容性最好）"),
        new(DepthChoice.Bit10, "10 bit（渐变更好，体积略大）"),
    ];

    public IReadOnlyList<OptionItem<RangeChoice>> RangeOptions { get; } =
    [
        new(RangeChoice.Default, "默认（跟随输入）"),
        new(RangeChoice.Limited, "Limited（视频惯例）"),
        new(RangeChoice.Full, "Full（图片惯例）"),
    ];

    public IReadOnlyList<OptionItem<OverwritePolicy>> OverwriteOptions { get; } =
    [
        new(OverwritePolicy.Skip, "跳过（保留已有输出）"),
        new(OverwritePolicy.Overwrite, "覆盖"),
        new(OverwritePolicy.Rename, "自动改名 (2)(3)…"),
    ];

    public IReadOnlyList<OptionItem<ThemeChoice>> ThemeOptions { get; } =
    [
        new(ThemeChoice.FollowSystem, "跟随系统"),
        new(ThemeChoice.Light, "浅色"),
        new(ThemeChoice.Dark, "深色"),
    ];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool engineReady;

    [ObservableProperty]
    private string engineStatus = "正在检测编码引擎…";

    [ObservableProperty]
    private bool isDragOver;

    [ObservableProperty]
    private bool isEmpty = true;

    [ObservableProperty]
    private double globalProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusSummary))]
    private int totalCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusSummary))]
    private int doneCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusSummary))]
    private int failedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusSummary))]
    private int skippedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusSummary))]
    private long savedBytes;

    [ObservableProperty]
    private string clockText = string.Empty;

    public string StatusSummary
    {
        get
        {
            var sb = new StringBuilder();
            sb.Append($"共 {TotalCount} · 完成 {DoneCount} · 失败 {FailedCount} · 跳过 {SkippedCount}");
            if (SavedBytes > 0)
            {
                sb.Append($" · 已节省 {JobEntry.FormatSize(SavedBytes)}");
            }

            return sb.ToString();
        }
    }

    public MainViewModel(AppSettings settings, SettingsStore store, AvifEncRunner runner, ConversionScheduler scheduler)
    {
        Settings = settings;
        _store = store;
        _runner = runner;
        _scheduler = scheduler;

        Settings.PropertyChanged += OnSettingChanged;
        Jobs.CollectionChanged += (_, e) =>
        {
            IsEmpty = Jobs.Count == 0;
            if (e.NewItems is not null)
            {
                foreach (JobEntry job in e.NewItems)
                {
                    RefreshJob(job);
                }
            }

            if (e.OldItems is not null)
            {
                foreach (JobEntry job in e.OldItems)
                {
                    ForgetJob(job);
                }
            }

            TotalCount = Jobs.Count;
        };

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClock();
    }

    public void ApplyThemeFromSettings() => ThemeService.Apply(Settings.Theme);

    public void SaveSettings() => _store.Save(Settings);

    public async Task InitializeEngineProbeAsync()
    {
        bool ok = await _runner.ProbeAsync(CancellationToken.None);
        EngineStatus = _runner.EngineStatus;
        EngineReady = ok;
    }

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        _store.Save(Settings);

        if (e.PropertyName == nameof(AppSettings.Theme))
        {
            ApplyThemeFromSettings();
        }
    }

    // ---------------- 队列管理 ----------------

    public void AddPaths(IEnumerable<string> paths)
    {
        var additions = new List<JobEntry>();

        foreach (string raw in paths)
        {
            if (Jobs.Count + additions.Count >= MaxQueueSize)
            {
                break;
            }

            string path = raw.Trim().Trim('"');
            if (!AvifEncRunner.IsSupportedImage(path) || !File.Exists(path))
            {
                continue;
            }

            if (_queuedPaths.Contains(path))
            {
                continue;
            }

            additions.Add(new JobEntry(path));
        }

        foreach (JobEntry job in additions)
        {
            if (_queuedPaths.Add(job.InputPath))
            {
                Jobs.Add(job);
            }
        }
    }

    public void AddFolder(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        try
        {
            IEnumerable<string> files = Settings.IncludeSubfolders
                ? EnumerateSafe(folder)
                : Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly);

            AddPaths(files.Where(AvifEncRunner.IsSupportedImage));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "读取文件夹失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        static IEnumerable<string> EnumerateSafe(string root)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                string[] files = Array.Empty<string>();
                string[] subs = Array.Empty<string>();
                try
                {
                    files = Directory.GetFiles(dir);
                    subs = Directory.GetDirectories(dir);
                }
                catch
                {
                    // 无权限目录跳过
                }

                foreach (string f in files)
                {
                    yield return f;
                }

                foreach (string s in subs)
                {
                    stack.Push(s);
                }
            }
        }
    }

    public void RemoveSelected(IEnumerable selectedItems)
    {
        var toRemove = selectedItems.OfType<JobEntry>()
            .Where(j => j.Status is JobStatus.Waiting or JobStatus.Done or JobStatus.Failed or JobStatus.Canceled or JobStatus.Skipped)
            .ToList();

        foreach (JobEntry job in toRemove)
        {
            _queuedPaths.Remove(job.InputPath);
            Jobs.Remove(job);
        }
    }

    [RelayCommand]
    private void AddFiles()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要转换的图片",
            Multiselect = true,
            Filter = "PNG / JPEG 图片|*.png;*.jpg;*.jpeg|所有文件|*.*",
        };

        if (dlg.ShowDialog(Application.Current.MainWindow) == true)
        {
            AddPaths(dlg.FileNames);
        }
    }

    [RelayCommand]
    private void AddFolderViaDialog()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择包含图片的文件夹" };
        if (dlg.ShowDialog(Application.Current.MainWindow) == true)
        {
            AddFolder(dlg.FolderName);
        }
    }

    /// <summary>
    /// 用 Windows 资源管理器的可视化文件夹选择器设定输出目录。
    /// 与「添加文件夹」用同一个 Microsoft.Win32.OpenFolderDialog（IFileOpenDialog + FOS_PICKFOLDERS），
    /// 保持项目内目录选择行为一致；选中后写回 Settings，属性变更即自动持久化。
    /// </summary>
    [RelayCommand]
    private void BrowseOutputFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择 AVIF 输出文件夹" };

        string current = Settings.SubfolderName.Trim().Trim('"');
        if (current.Length > 0)
        {
            try
            {
                if (Path.IsPathRooted(current) && Directory.Exists(current))
                {
                    dlg.InitialDirectory = current;
                }
            }
            catch
            {
                // 路径非法或不可达时忽略，交给系统默认起始位置
            }
        }

        if (dlg.ShowDialog(Application.Current.MainWindow) == true)
        {
            Settings.SubfolderName = dlg.FolderName;
        }
    }

    [RelayCommand]
    private void ClearCompleted()
    {
        var completed = Jobs.Where(j => j.IsFinished).ToList();
        foreach (JobEntry job in completed)
        {
            _queuedPaths.Remove(job.InputPath);
            Jobs.Remove(job);
        }
    }

    [RelayCommand]
    private void RetryFailed()
    {
        foreach (JobEntry job in Jobs.Where(j => j.Status is JobStatus.Failed or JobStatus.Canceled or JobStatus.Skipped))
        {
            job.Error = null;
            job.Status = JobStatus.Waiting;
            RefreshJob(job);
        }

        GlobalProgress = 0;
    }

    [RelayCommand]
    private void OpenOutputFolder(object? selectedItems)
    {
        JobEntry? done = (selectedItems as IEnumerable)?.OfType<JobEntry>().FirstOrDefault(j => j.Status == JobStatus.Done && j.OutputPath is not null)
            ?? Jobs.LastOrDefault(j => j.Status == JobStatus.Done && j.OutputPath is not null);

        if (done?.OutputPath is null || !File.Exists(done.OutputPath))
        {
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{done.OutputPath}\"") { UseShellExecute = true });
    }

    // ---------------- 转换执行 ----------------

    private bool CanStart() => !IsBusy && EngineReady;

    private bool CanCancel() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var pending = Jobs.Where(j => j.Status == JobStatus.Waiting).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        foreach (JobEntry job in pending)
        {
            job.Error = null;
            job.OutputSize = 0;
            job.ElapsedSeconds = 0;
            job.OutputPath = null;
            RefreshJob(job);
        }

        IsBusy = true;
        GlobalProgress = 0;
        _batchStartUtc = DateTime.UtcNow;
        _clockTimer.Start();

        var progress = new Progress<double>(p => GlobalProgress = p);
        await _scheduler.RunAsync(pending, progress, RefreshJob);

        _clockTimer.Stop();
        UpdateClock();
        IsBusy = false;
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _scheduler.Cancel();
    }

    // ---------------- 聚合统计 ----------------

    /// <summary>单个任务对聚合数的贡献快照：终态 + 该状态下节省的字节数。</summary>
    private readonly record struct JobAggregate(JobStatus Status, long Saved);

    private static long SavedOf(JobEntry job) =>
        job.Status == JobStatus.Done && job.OutputSize > 0 && job.InputSize > job.OutputSize
            ? job.InputSize - job.OutputSize
            : 0;

    /// <summary>
    /// 按任务当前状态刷新聚合统计：只增减该任务自己的贡献，O(1)。
    /// 每个任务状态/体积变化点（入队、出队、完成回调、重试重置）都要调用一次。
    /// </summary>
    private void RefreshJob(JobEntry job)
    {
        // 未跟踪过的新任务按零贡献处理（TryGetValue 失败时 previous 即 default）
        _aggregates.TryGetValue(job, out JobAggregate previous);
        Apply(previous, -1);

        JobAggregate current = new(job.Status, SavedOf(job));
        _aggregates[job] = current;
        Apply(current, +1);
    }

    private void ForgetJob(JobEntry job)
    {
        if (_aggregates.Remove(job, out JobAggregate previous))
        {
            Apply(previous, -1);
        }
    }

    private void Apply(JobAggregate aggregate, int sign)
    {
        switch (aggregate.Status)
        {
            case JobStatus.Done:
                DoneCount += sign;
                break;
            case JobStatus.Failed:
                FailedCount += sign;
                break;
            case JobStatus.Skipped:
                SkippedCount += sign;
                break;
        }

        SavedBytes += sign * aggregate.Saved;
    }

    private void UpdateClock()
    {
        if (!IsBusy)
        {
            ClockText = DoneCount > 0 ? ClockText : string.Empty;
            return;
        }

        var elapsed = DateTime.UtcNow - _batchStartUtc;
        double rate = elapsed.TotalSeconds > 1 ? DoneCount / elapsed.TotalMinutes : 0;
        ClockText = $"⏱ {elapsed:hh\\:mm\\:ss} · {rate:0.#} 文件/分";
    }

    // ---------------- 退出清理 ----------------

    /// <summary>取消一切、同步杀死所有 avifenc 子进程、释放计时器。保证退出后无后台残留。</summary>
    public void RequestShutdown()
    {
        _clockTimer.Stop();
        _scheduler.Cancel();
        _runner.KillAll();
        _runner.Dispose();
    }
}
