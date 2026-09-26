using CommunityToolkit.Mvvm.ComponentModel;

namespace AvifForge.Models;

/// <summary>
/// 用户可调设置。直接作为可观察对象绑定到 UI，属性变化由 MainViewModel 监听并持久化。
/// </summary>
public partial class AppSettings : ObservableObject
{
    public const int QualityMin = 0;
    public const int QualityMax = 100;   // 100 = 无损级（近无损）
    public const int MinSpeed = 0;
    public const int MaxSpeed = 10;

    /// <summary>
    /// 输出目录默认值：当前用户的「图片」文件夹。放在 SubfolderName 字段里：ConversionScheduler 用
    /// Path.Combine(原图目录, 该值)，而 Path.Combine 在第二参数为绝对路径时直接返回它 ——
    /// 所以填绝对路径即可把产物集中到一个固定目录。
    /// </summary>
    public static string DefaultOutputFolder =>
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

    /// <summary>色彩质量 0-100，越大越好（默认 75）。</summary>
    [ObservableProperty]
    private int quality = 75;

    /// <summary>最高质量档：等效 -q 100 --qalpha 100（SVT 无真 4:4:4 无损）。</summary>
    [ObservableProperty]
    private bool maxQuality;

    /// <summary>Alpha 通道质量 0-100；0 = 跟随色彩质量。</summary>
    [ObservableProperty]
    private int qualityAlpha;

    /// <summary>编码速度 0（最慢、压缩最好）～ 10（最快）。</summary>
    [ObservableProperty]
    private int speed = 6;

    /// <summary>默认 10-bit：实测平滑渐变（天空）断层显著（8bit 最大台阶 29 级 → 10bit 21 → q80/s4 仅 6），体积反而略小。</summary>
    [ObservableProperty]
    private DepthChoice depth = DepthChoice.Bit10;

    [ObservableProperty]
    private RangeChoice range = RangeChoice.Default;

    /// <summary>
    /// 同时转换的文件数（每文件一个 avifenc 进程）。默认 DefaultParallelism：
    /// 矩阵压测证明「一核一进程」吞吐随核数线性扩展，默认 1 在 4 核机上只用到 1/4 算力；
    /// 上限同时受 CPU 核数与可用内存约束（见 MaxParallelism）。
    /// </summary>
    [ObservableProperty]
    private int parallelism = DefaultParallelism;

    /// <summary>单文件内部工作线程数；0 = 自动（用满 CPU）。默认 1：不把核数全吃满。</summary>
    [ObservableProperty]
    private int jobsPerImage = 1;

    /// <summary>输出目录：绝对路径 = 固定输出到该目录；相对名 = 原图下的子文件夹；留空 = 与原图同目录。</summary>
    [ObservableProperty]
    private string subfolderName = DefaultOutputFolder;

    /// <summary>已存在时默认自动改名追加 " (2)"、" (3)"…，不覆盖也不跳过。</summary>
    [ObservableProperty]
    private OverwritePolicy overwritePolicy = OverwritePolicy.Rename;

    /// <summary>默认保留 EXIF/XMP/ICC（不传 --ignore-*）。</summary>
    [ObservableProperty]
    private bool keepMetadata = true;

    [ObservableProperty]
    private bool deleteSourceOnSuccess;

    [ObservableProperty]
    private bool includeSubfolders = true;

    /// <summary>透传给 avifenc 的 SVT 高级参数（-a key=value，可分号分隔多个）。留空即不传。</summary>
    [ObservableProperty]
    private string advancedParams = string.Empty;

    [ObservableProperty]
    private ThemeChoice theme = ThemeChoice.FollowSystem;

    /// <summary>设置结构版本：保存时统一写 4；低于 4 的旧文件在 Load 时触发一次性迁移。1.0.1→2（10-bit），1.0.2→3（输出目录/重命名/元数据/并发默认），1.3.0→4（默认并发改为按核数/内存自适应）。</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>进程可提交内存总量（GC 视角，含 cgroup 限额）。用于按内存水位收敛并发。</summary>
    public static long AvailableMemoryBytes
    {
        get
        {
            try
            {
                return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            }
            catch
            {
                return 4L * 1024 * 1024 * 1024; // 读不到时按 4 GB 保守估计
            }
        }
    }

    /// <summary>单个 avifenc 进程的预留内存（实测 1600x900@10bit 约 160 MB，留余量取 220 MB）。</summary>
    public const long MemoryPerEncodeProcess = 220L * 1024 * 1024;

    /// <summary>按「可用内存的 60% ÷ 每进程预留」推算的最大安全并发数。</summary>
    public static int MaxParallelismByMemory
    {
        get
        {
            long budget = AvailableMemoryBytes * 6 / 10;
            return (int)Math.Clamp(budget / MemoryPerEncodeProcess, 1, 64);
        }
    }

    /// <summary>有效并发上限：CPU 核数与内存水位取小，再夹到 [4, 16]。</summary>
    public int MaxParallelism => (int)Math.Clamp(
        Math.Min(Environment.ProcessorCount, MaxParallelismByMemory), 4, 16);

    /// <summary>
    /// 默认并发：min(CPU 核数, 内存水位) 夹到 [2, 8]。
    /// 压测数据：并发≈核数时吞吐线性；2 核保底避免退化成串行；8 封顶避免超大机默认过激。
    /// </summary>
    public static int DefaultParallelism
    {
        get
        {
            long byMem = AvailableMemoryBytes * 6 / 10 / MemoryPerEncodeProcess;
            long byCpu = Environment.ProcessorCount;
            return (int)Math.Clamp(Math.Min(byCpu, byMem), 2, 8);
        }
    }

    /// <summary>并发上限的由来说明（UI 提示用）。</summary>
    public string ParallelismHint
    {
        get
        {
            int byCpu = Math.Clamp(Environment.ProcessorCount, 4, 16);
            int byMem = MaxParallelismByMemory;
            long memMb = AvailableMemoryBytes / (1024 * 1024);
            return byMem < byCpu
                ? $"已按可用内存（约 {memMb} MB，每进程预留 220 MB）将上限调整为 {MaxParallelism}（CPU 原可达 {byCpu}）"
                : $"上限 {MaxParallelism}（按 {Environment.ProcessorCount} 核 CPU）；峰值内存预估约 {MaxParallelism * 220} MB，可用约 {memMb} MB";
        }
    }

    public void ClampValues()
    {
        Quality = Math.Clamp(Quality, QualityMin, QualityMax);
        QualityAlpha = Math.Clamp(QualityAlpha, 0, QualityMax);
        Speed = Math.Clamp(Speed, MinSpeed, MaxSpeed);
        Parallelism = Math.Clamp(Parallelism, 1, MaxParallelism);
        JobsPerImage = Math.Clamp(JobsPerImage, 0, 64);
        SubfolderName = SubfolderName.Trim();
        AdvancedParams = AdvancedParams.Trim();
    }
}
