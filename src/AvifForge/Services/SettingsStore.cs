using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AvifForge.Models;

namespace AvifForge.Services;

/// <summary>设置持久化：%APPDATA%\AvifForge\settings.json，原子写入。</summary>
public sealed class SettingsStore
{
    private static readonly string Directory_ = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AvifForge");

    private static readonly string FilePath = Path.Combine(Directory_, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (settings is not null)
                {
                    if (settings.SchemaVersion < 2)
                    {
                        // 1.0.1 起默认改为 10-bit（防天空断层）；旧配置一次性迁移
                        settings.Depth = DepthChoice.Bit10;
                        settings.SchemaVersion = 2;
                    }

                    if (settings.SchemaVersion < 3)
                    {
                        // 1.0.2 起的默认值：固定输出目录、已存在自动改名、保留元数据、并发与单图线程各 1。
                        // 一次性覆盖旧配置里的对应项，否则升级后用户看到的仍是上一版的默认值。
                        settings.SubfolderName = AppSettings.DefaultOutputFolder;
                        settings.OverwritePolicy = OverwritePolicy.Rename;
                        settings.KeepMetadata = true;
                        settings.Parallelism = 1;
                        settings.JobsPerImage = 1;
                        settings.SchemaVersion = 3;
                    }

                    if (settings.SchemaVersion < 4)
                    {
                        // 1.3.0 起默认并发按 min(CPU 核数, 内存水位) 自适应（压测：一核一进程吞吐线性，
                        // 默认 1 在 4 核机只用 1/4 算力）。旧配置里仍是默认 1 的一并抬到新默认，
                        // 显式调过并发的不动（ClampValues 兜底范围）。
                        if (settings.Parallelism <= 1)
                        {
                            settings.Parallelism = AppSettings.DefaultParallelism;
                        }

                        settings.SchemaVersion = 4;
                    }

                    settings.ClampValues();

                    // 1.1 及以前 UI 提供过「12 bit」档位但 SVT-AV1 必然编码失败；读到旧配置统一迁移到 10 bit
                    if (settings.Depth == DepthChoice.Bit12)
                    {
                        settings.Depth = DepthChoice.Bit10;
                    }

                    return settings;
                }
            }
        }
        catch
        {
            // 配置损坏时静默回退默认值
        }

        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        try
        {
            settings.SchemaVersion = 4;
            Directory.CreateDirectory(Directory_);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch
        {
            // 保存失败不影响使用
        }
    }
}
