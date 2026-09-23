# avifenc.exe（SVT-AV1 引擎）替换可行性核查

副本目录：`<仓库根目录>`（基于 1.0.2 MSI 的工程副本；原项目 `<工作区>\avif-forge` 未改动）
核查时间：2026-09-10

## 1. 当前版本与部署结构（实测）

```
> avifenc.exe --version
Version: 1.4.2 (svt [enc]:v4.1.0)
libyuv : available (1924)
```

| 项 | 值 |
|---|---|
| 文件 | `tools\avifenc.exe`（发布时邻接 `AvifForge.exe`） |
| 大小 | 6,431,744 B |
| SHA-256 | `EB82A66E70F15DE0009C09F3A1F60EDD24EB2F635093C5799921238E7E662FAB` |
| 架构 | PE32+ x64，7 个节（`.text .rodata .rdata .data .pdata .rsrc .reloc`） |
| 导入表（12 个） | `KERNEL32.dll`、`VCRUNTIME140.dll`、`api-ms-win-crt-*`（10 个 UCRT api-set） |
| 旁挂 DLL | **无** —— `tools\` 与 `publish\` 里除 exe 外没有任何 DLL |
| 编码器 | 只链了 SVT（help 里 `-c,--codec` 的可选版本列表只出现 `svt [enc]:v4.1.0`；无 aom、无 rav1d） |
| CRT | **动态 CRT**（依赖 `VCRUNTIME140.dll`）。本机 VC++ 2015-2022 x64 运行库 14.51.36247 已装 → 能跑。MSI 不含运行库，换到没装过的机器需注意 |

**静态/动态结论：静态自包含。** 导入表里没有任何 `avif.dll` / `libavif.dll` / `SVT-AV1.dll` / `aom.dll` / `libyuv.dll` —— SVT-AV1 是被编译进 `avifenc.exe` 这一个二进制的。
→ 好消息：替换时确实只需换一个文件；坏消息：**没有可以单独换掉的"SVT-AV1 库"**，库里不存在这个模块边界。

## 2. 更正：这个 exe 不是官方 release 产物，而是"官方源码 + 自建 SVT-only 配置"

（先前本节写过"现在这个就是官方的"，看到官方 CI 定义后**推翻**，以下为核实结果。）

- 官方 [`.github/workflows/ci-windows-artifacts.yml`](https://github.com/AOMediaCodec/libavif/blob/main/.github/workflows/ci-windows-artifacts.yml)（就是产出 release 里 `windows-artifacts.zip` 的那个 workflow）的 CMake 配置是：
  `-DAVIF_CODEC_AOM=LOCAL -DAVIF_CODEC_AOM_ENCODE=ON -DAVIF_CODEC_AOM_DECODE=OFF -DAVIF_CODEC_DAV1D=LOCAL -DAVIF_LIBYUV=LOCAL -DAVIF_LIBSHARPYUV=LOCAL -DAVIF_JPEG=LOCAL -DAVIF_ZLIBPNG=LOCAL -DBUILD_SHARED_LIBS=OFF -DAVIF_BUILD_APPS=ON`
  —— **没有任何 SVT 步骤，也没有 `-DAVIF_CODEC_SVT`**。所以官方 zip 里的 `avifenc.exe` 的 `--version` 会是 `aom [enc]` + `dav1d [dec]`，不可能报 `svt [enc]`。
- 而实测你的 exe 只报 `svt [enc]:v4.1.0`（help 里连 `aom`、`rav1d` 字样都没有）→ **它是用官方 libavif 1.4.2 源码 + 官方 SVT-AV1 4.1.0 源码，按 SVT-only 配置自己编的**（`README.md` 里"GitHub Actions CI 产物"这句指的是自建 CI 流水线的产物，不是 libavif 官方 release 资产；这也解释了 zip SHA-256 `d9a2eb…0b9e5` 与官方 `windows-artifacts.zip` 的 digest `cb2d9fea…484a` 为什么不一致）。
- 静态/单文件、`VCRUNTIME140` 动态 CRT 这两点与官方 CI 配方一致 ✓（官方 `-DBUILD_SHARED_LIBS=OFF`、用 `/MD`，且官方 CI 手工链接那步就是 `/MD`）。

结论修正：没有"官方预编译的 libavif+SVT"可下 —— 官方 release 资产不含 SVT。想要官方 SVT 的新版本，**只能自己编**，而这恰好是你这个 exe 的原始来历，可以完整复现（见第 6 节）。

## 2b. libavif 主干已经把 SVT 钉到 v4.2.0

[`ext/svt.cmd`](https://github.com/AOMediaCodec/libavif/blob/main/ext/svt.cmd)（main，2026-09-10 抓取原文）：

```
git clone -b v4.2.0 --depth 1 https://gitlab.com/AOMediaCodec/SVT-AV1.git
cd SVT-AV1
cd Build/windows
call build.bat release static no-apps
cd ../..
mkdir include\svt-av1
copy Source\API\*.h include\svt-av1
```

即：**"官方 libavif + 官方 SVT-AV1 4.2.0" 的配对在上游已经成立**，只是还没进任何一个 release 资产。用 `main` 编，或者用 `v1.4.2` 把这个 pin 抬到 `v4.2.0` 再编，都是官方源码组合。

## 3. 唯一真实的"更新"是 SVT-AV1 v4.2.0，但它没有官方二进制

[SVT-AV1 官方（GitLab AOMediaCodec/SVT-AV1）](https://gitlab.com/AOMediaCodec/SVT-AV1/-/releases)：

| 版本 | 发布 | 官方资产 |
|---|---|---|
| v4.2.0 | 2026-07-14 | **仅源码**（`/-/archive/v4.2.0/SVT-AV1-v4.2.0.zip` 等 4 个格式，`links: []`） |
| v4.1.0 | 2026-03-23 | 仅源码。含 "Improve Still Image coding efficiency"、"Optimize Screen Content coding for Still Image"、"Add mutexes to fix hangs when running multiple instances in one process" |
| v4.0.0 | 2026-01-23 | 仅源码。**API 破坏性变更**（"not backwards compatible"），AVIF/静态图模式大幅提速（tune MS-SSIM 下 M11-M0 约 5-8x） |

即：官方**不发** Windows 二进制，任何"官方 SVT-AV1 4.2.0 的 avifenc.exe"都不存在。要用 4.2.0 只有重编 libavif+SVT。

对静态图（本项目的实际用途）而言 4.2.0 的增量主要落在 VOD/RTC/ARM/熵编码，静态图相关的只有 "Optimized still-image screen content detection"。而 4.0/4.1 带来的静态图收益（tune iq / MS-SSIM、still-image 效率）**已经在你现在的 v4.1.0 里**了。

## 4. 本机可行性与可选路线

已验证的环境限制：
- 沙箱内 `Invoke-WebRequest` 到 api.github.com **超时**，且 `danger-full-access` 放开后仍超时 → 我这边下不了东西。
- `cmake` / `nasm` / `cl` / `git` 均不在 PATH → 本机也无法从源码编。

路线：

**A. 不动（推荐）** —— 已经是"官方最新 release 的官方配对"，且上一轮排查出的画质/断层问题与 SVT 版本无关。

**B. 你下载，我做哈希比对与替换** —— 用浏览器下载上面那个 `windows-artifacts.zip`（校验 SHA-256 = `cb2d9fea…484a`），放进 `<你的下载目录>\`。我解压比对里面的 `avifenc.exe`：与现有相同 → 结案（证明你手上就是官方包）；不同 → 换进 `tools\`、重跑 publish + wix，出新 MSI。

**C. 真的要 SVT-AV1 v4.2.0** —— 在**有网 + 装好 VS 2022(CMake + MSVC) + NASM** 的机器上编：拉 libavif 源码，把 SVT pin 改到 `v4.2.0`（**已核实 pin 位置：`ext/svt.cmd` 与 `ext/svt.sh` 里的 `git clone -b v4.1.0`**；main 分支已是 `-b v4.2.0`，所以直接编 main 即自带 4.2.0），用 libavif 官方 `ext/`+CMake 流程编出**静态** `avifenc.exe`（保持单文件、保持 `--version` 里出现 `svt [enc]`，否则 `AvifEncRunner.ProbeAsync` 会判"不含 SVT-AV1 编码器"而拒用）。产出 exe 交给我，我在副本里替换 + 打包。**→ 已落地为可执行脚本，见第 6 节。**
- 注意 ABI：libavif 1.4.2 的 svt_av1 plugin 是按 4.1.0 头文件写的；4.0 起 API 破坏性变更，跨版本只换 DLL 不换 libavif 不可行 —— 必须整包（libavif + SVT）一起编。

**D. 改成动态链接版（libavif.dll + SVT-AV1.dll）** —— 技术上"能换库"了，但：官方无 Windows 二进制（回到 C 的编译问题）、MSI 要多加若干 DLL 组件、`AvifForge.csproj` 的 `Content` 项要跟着改、还要处理 CRT。**不推荐**。

## 5. 交接给我的替换清单（B/C 任一路线，产物到位后我执行）

1. 新 exe 放到 `tools\avifenc.exe`（覆盖前先把旧的备份为 `avifenc.exe.old-1.4.2-svt4.1.0`）。
2. 跑 `avifenc.exe --version` 确认串里含 `svt [enc]`；跑 `avifenc.exe -c svt -s 6 -q 60 -o t.avif <png>` 冒烟。
3. `dotnet publish … -r win-x64 --self-contained`（离线包源 `<本机 NuGet 缓存>`）。
4. `.tools\wix.exe build installer\package.wxs -arch x64 -o dist\…msi -d PublishDir=… -d SrcDir=… -d InstallerDir=…`。
5. 校验 MSI 载荷哈希 == 新 exe；`test\EngineTest` 6 用例过（需 `test\samples` 里的测试图）。
6. 若新包不是单文件静态 exe → 还要改 `AvifForge.csproj` 的 `Content` 与 `installer\package.wxs` 的组件表。

## 6. 路线 C 已落地（用户选定：要 SVT-AV1 4.2.0）

- `build-engine\build-avifenc.ps1`：照搬官方 CI 配方（`ext/*.cmd` 依赖自举 + `cmake -A x64 -DBUILD_SHARED_LIBS=OFF -DAVIF_CODEC_SVT=LOCAL`，其余 codec OFF），带 pin 改写、前置依赖检查、`--version` 必须含 `svt [enc]` 的硬门禁、SHA-256 输出。参数：`-LibavifRef main|v1.4.2`、`-SvtTag v4.2.0`、`-NoLibyuv`、`-Sample <图>`。
- `build-engine\README.md`：依赖清单（含本机缺什么、为什么缺）、运行方式、5 条验收标准、交回后我接管的部分。
- 推荐 `-LibavifRef main`：主干 `ext/svt.cmd` 已钉 `v4.2.0`，libavif 的 SVT plugin 与该版本头文件必然匹配；`v1.4.2` + 抬 pin 属于"跨版本自配"，若编译期报 API 不匹配就回 main。
