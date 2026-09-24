namespace AvifForge.Models;

/// <summary>输出文件已存在时的策略。</summary>
public enum OverwritePolicy
{
    /// <summary>跳过，不转换。</summary>
    Skip,
    /// <summary>直接覆盖。</summary>
    Overwrite,
    /// <summary>自动改名，追加 " (2)"、" (3)"…</summary>
    Rename,
}

public enum ThemeChoice
{
    FollowSystem,
    Light,
    Dark,
}

public enum DepthChoice
{
    Bit8,
    Bit10,
    /// <summary>旧版本遗留：SVT-AV1 只支持 8/10 bit，UI 不再提供该档；读到旧配置时迁移为 Bit10。</summary>
    Bit12,
}

/// <summary>YUV range。默认沿用 avifenc 对 PNG/JPEG 的处理（full）。</summary>
public enum RangeChoice
{
    Default,
    Limited,
    Full,
}

public enum JobStatus
{
    Waiting,
    Running,
    Done,
    Failed,
    Canceled,
    Skipped,
}
