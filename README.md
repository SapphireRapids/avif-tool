# avif tool（AvifForge）— SVT-AV1 图片转 AVIF（Windows）

把大量或单张 **PNG / JPEG** 图片转换为 **AVIF**。编码引擎**锁定 SVT-AV1**，
SIMD（AVX2/AVX-512）与多核并行由内核按 CPU 自动派发；纯 CPU、无 GPU、无后台常驻。

## 下载
到 [Releases](../../releases) 取 `avif tool 1.1.msi`：**per-user 安装，无需管理员**，
装到 `%LOCALAPPDATA%\Programs\avif tool`，自动创建开始菜单 + 桌面快捷方式。
支持 Windows 10 / 11 x64；卸载走「设置 → 应用」。
安装包**未做代码签名**，首次运行 SmartScreen 会拦一次，点「仍要运行」即可。

## 使用
1. 拖入文件或文件夹（或「添加文件 / 添加文件夹」）。
2. 右侧面板调开关与质量：
   - **图像质量** 0–100（越大越好；摄影 50–65，截图 60–75）；
   - **最高质量档**（`-q 100 --qalpha 100`，近无损*）；
   - **编码速度** 0–10（越大越快、文件越大）；
   - 位深 8/10/12、YUV 范围、Alpha 质量、并发数、单图线程、
     元数据保留、成功后原图入回收站、输出目录（默认「图片」文件夹；填相对名则作为
     原图下的子文件夹，留空即与原图同目录）、
     已存在策略（跳过/覆盖/重命名）、`-a` 高级透传。
3. 「开始转换」；队列实时状态、速率、节省比例；「停止」立即终止全部子进程。
4. 主题：跟随系统 / 浅色 / 深色（Mica；Win10 自动回退）。

\* SVT-AV1 仅支持 4:2:0/4:0:0，不提供 4:4:4 真无损（libavif `-l` 会报错），
   q100 为当前内核的最高档（视觉近无损）。

## 架构
WPF (.NET 10, 自包含单文件) + WPF-UI(Fluent) + CommunityToolkit.Mvvm；
编码经 `avifenc.exe`（libavif 1.4.2 + SVT-AV1 v4.1.0 静态构建，见
`installer\THIRD-PARTY-NOTICES.txt`，zip SHA-256 `d9a2eb…0b9e5`，exe `EB82A6…2FAB`）子进程完成，
参数用 `ArgumentList` 传递（无字符串注入）；删除原图走 `shell32!SHFileOperationW`（回收站）。
关窗即杀全部 avifenc 子进程，`ShutdownMode=OnMainWindowClose`，无常驻。

## 构建
仓库只含源码：不含构建产物，也不含 `tools\avifenc.exe`（第三方编码引擎，约 6 MB，
见 [`tools\README.md`](tools/README.md)）。缺它应用照样编译（`csproj` 里是
`Condition="Exists(...)"`），但转图会报引擎缺失。

前置：**.NET 10 SDK**（`net10.0-windows` + WPF）。

```powershell
.\build.ps1 -NoMsi        # 只要 publish\AvifForge.exe，不需要 WiX
.\build.ps1               # 完整产物 dist\avif tool 1.1.msi（.tools\wix.exe 缺失时自动装 wix 5.0.2）
.\build.ps1 -NugetRoot 'D:\path\to\.nuget-packages'
                          # 完全离线：指向已解包、含 wpf-ui + communitytoolkit.mvvm +
                          # 10.0.x win-x64 runtime packs 的 NuGet 目录
```
默认按 `nuget.config` 从 nuget.org 在线还原；`build.ps1` 顶部注释里有等价的手敲命令。

引擎测试：`dotnet run --project test\EngineTest -c Release`（6 用例走真实 `EncodeAsync`
管线，输入图在 `test\samples\`，也可传自己的目录作参数）。
无头自检：`AvifForge.exe --smoke`（建窗 + 探针 avifenc，4 秒自毁，退出码 0=OK）。

## 已知限制
- SVT-AV1 v4.1：无 4:2:2/4:4:4、无真无损；`--sharpyuv` 与本静态构建的 libyuv 不兼容，已移除。
- `-a` 透传仅少数键被 SVT 接受（如 `tune=0`、`preset=8`），错键会逐文件报错。
- UI 的「12 bit（专业）」档位必然失败：SVT-AV1 只支持 8/10 bit。
- 可执行文件仍叫 `AvifForge.exe`、配置仍读写 `%APPDATA%\AvifForge\settings.json`（与 1.0.x 共用），
  详见 `MANIFEST-1.1.md` 的「已知遗留」。

## 许可
MIT（见 `LICENSE`）。随包分发的 `avifenc.exe` 是 libavif / SVT-AV1 等第三方项目的构建产物，
其许可证见 `installer\THIRD-PARTY-NOTICES.txt`。
