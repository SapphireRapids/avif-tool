using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AvifForge.Models;

/// <summary>队列中的一个转换任务条目。</summary>
public partial class JobEntry : ObservableObject
{
    public string InputPath { get; }

    public string InputName => Path.GetFileName(InputPath);

    public long InputSize { get; }

    [ObservableProperty]
    [property: System.Text.Json.Serialization.JsonIgnore]
    private JobStatus status = JobStatus.Waiting;

    [ObservableProperty]
    private string? outputPath;

    [ObservableProperty]
    private long outputSize;

    [ObservableProperty]
    private double elapsedSeconds;

    [ObservableProperty]
    private string? error;

    public JobEntry(string inputPath)
    {
        InputPath = inputPath;
        try
        {
            InputSize = new FileInfo(inputPath).Length;
        }
        catch
        {
            InputSize = 0;
        }
    }

    public bool IsRunning => Status == JobStatus.Running;

    public bool IsFinished => Status is JobStatus.Done or JobStatus.Failed or JobStatus.Canceled or JobStatus.Skipped;

    public string StatusText => Status switch
    {
        JobStatus.Waiting => "等待",
        JobStatus.Running => "转换中",
        JobStatus.Done => "完成",
        JobStatus.Failed => "失败",
        JobStatus.Canceled => "已取消",
        JobStatus.Skipped => "跳过",
        _ => "",
    };

    public string InputSizeText => FormatSize(InputSize);

    public string OutputSizeText => OutputSize > 0 ? FormatSize(OutputSize) : "—";

    public string SavedText =>
        Status == JobStatus.Done && InputSize > 0 && OutputSize > 0
            ? $"↓ {(1.0 - (double)OutputSize / InputSize) * 100.0:0.#}%"
            : string.Empty;

    public string ElapsedText =>
        Status == JobStatus.Done && ElapsedSeconds > 0 ? $"{ElapsedSeconds:0.#}s" : string.Empty;

    partial void OnStatusChanged(JobStatus value)
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsFinished));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(OutputSizeText));
        OnPropertyChanged(nameof(SavedText));
        OnPropertyChanged(nameof(ElapsedText));
    }

    partial void OnOutputSizeChanged(long value)
    {
        OnPropertyChanged(nameof(OutputSizeText));
        OnPropertyChanged(nameof(SavedText));
    }

    internal static string FormatSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "—";
        }

        string[] units = ["B", "KB", "MB", "GB"];
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < units.Length - 1)
        {
            v /= 1024;
            i++;
        }

        return i == 0 ? $"{bytes} B" : $"{v:0.#} {units[i]}";
    }
}
