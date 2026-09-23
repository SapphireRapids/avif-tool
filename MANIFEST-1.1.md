# avif tool 1.1 — 产物清单

构建时间 2026-09-10 15:16 · 独立副本目录 `<仓库根目录>`（不依赖任何 C 盘原项目）

## 主产物

| 项 | 值 |
|---|---|
| 安装包 | `dist\avif tool 1.1.msi` |
| 大小 | 59,695,104 B |
| SHA-256 | `C96BF37EFFB4380505A4B6F084D4B25CE93F69D69C20F5FEF5162CC1437BE95C` |
| 范围 / 签名 | per-user（无需管理员）· **未签名** |

## 产品身份（MSI 表实测）

| 字段 | AvifForge 1.0.2 | avif tool 1.1 |
|---|---|---|
| ProductName | `AvifForge — SVT-AV1 图片转 AVIF` | `avif tool` |
| ProductVersion | 1.0.2 | **1.1.0** |
| ProductCode | `{A27A01CE-…}` | `{0F7A1AA5-EFF2-4AF1-9A04-B2D6BC0D0B10}` |
| UpgradeCode | `{7E9F1F5E-…}` | `{815FB4C3-AD00-4867-8EF2-5D758C7115D0}` |
| Manufacturer | AvifForge | `avif tool` |
| 安装目录 | `%LOCALAPPDATA%\Programs\AvifForge` | `%LOCALAPPDATA%\Programs\avif tool` |
| 开始菜单 / 桌面快捷方式 | AvifForge | `avif tool` |
| HKCU 安装标记 | `Software\AvifForge\Installer` | `Software\avif tool\Installer` |
| 5 个组件 GUID | — | **与 1.0.2 零交集**（逐值比对） |

新 UpgradeCode + 新安装目录 + 新组件 GUID ⇒ 与任何已装的 AvifForge **完全独立，不会互相卸载/串台**。

> 更正一条过程记录：我一度以为 `CmpNotices` 与 1.0.2 撞了 GUID，于是把组件 Id 全改名重打。实际是我读错了第一次转储 —— WiX 的自动组件 GUID 由**安装后的绝对路径**推导，目录一变 GUID 就变，所以本来就不会撞。改名无害（最终包的 GUID 与改名前一致 `{BF180489-…}`），但这一步是多余的。最终值以上表为准，交集已按值复核为 0。

## 载荷（从 MSI 里 7-Zip 解出，逐个 SHA-256 与源文件比对）

| 文件 | 大小 | SHA-256（前 16） | 比对 |
|---|---|---|---|
| `AvifForge.exe` | 64,288,788 B | `8BD83236E33A80EE` | = `publish\AvifForge.exe` ✓✓ **1.1 全新构建**（1.0.2 载荷是 `6093B153…`） |
| `avifenc.exe` | 6,431,744 B | `EB82A66E70F15DE0` | = 1.0.0/1.0.1/1.0.2 同一个引擎 ✓ **未替换** |
| `THIRD-PARTY-NOTICES.txt` | 3,558 B | `B7BED5950D74ADBC` | = `installer\` 源文件 ✓ |

载荷 PE 版本信息：`FileVersion 1.1.0.0` · `ProductName "avif tool"` · `CompanyName "avif tool"`。

## 相对 1.0.2 改了什么

**只改身份，没改逻辑**：`AvifForge.csproj` 的 `Version/Product/Company`；`installer\package.wxs` 的名称、版本、UpgradeCode、目录、快捷方式名、注册表键、组件 Id（`CmpToolExe/CmpEncExe/CmpToolNotices/CmpStartMenu/CmpDesktop`）、`SummaryInformation`。

1.0.2 的 5 项默认改动全部继承，并在 IL 里复核过：`subfolderName ← ldstr "%USERPROFILE%\Pictures"`、`overwritePolicy ← 2 (Rename)`、`keepMetadata ← 1`、`parallelism ← 1`、`jobsPerImage ← 1`；`BrowseOutputFolder` / `BrowseOutputFolderCommand` / `DefaultOutputFolder` 符号在位 ✓ 资源管理器选文件夹按钮仍在。

## 验证记录

1. `wix build` exit 0，无 warning。
2. 表转储比对（`_tables`，脚本 `dump-tables-102.ps1`）：身份字段、组件 GUID 交集、目录、快捷方式、注册表、Upgrade 边界（min/max=1.1.0）、`File` 表载荷大小 64,288,788 ✓。
3. 7-Zip 解出 cab 载荷 → 三文件 SHA-256 与磁盘源文件一致 ✓（证明包内就是这次编出来的东西）。
4. `msiexec /a` 管理员抽取测试：`InstallValidate` / `InstallAdminPackage` / `InstallFiles` 全部通过，**commit 阶段 2502/2503**，日志显示 `RESTART MANAGER: Failed to open session (29)` 与 `1402 HKLM\...\Installer\Rollback\Scripts` ⇒ **沙箱拦住了安装事务，不是包缺陷**；已干净回滚。真实安装请你自己双击。

## 已知遗留（都是刻意的，说一声）

- 可执行文件仍叫 `AvifForge.exe`（`AssemblyName` 未改），窗口标题也仍是 `AvifForge`，PE `FileDescription` 仍是 `AvifForge`。要连这些都换成 avif tool，说一声，一次改完（会连带改 `%APPDATA%` 配置目录）。
- 配置仍读写 `%APPDATA%\AvifForge\settings.json` —— 与旧版共用，所以**装 1.1 后不会重置成默认值**。你现在这份是 `Quality 20 / Speed 3 / Bit10 / Parallelism 1 / JobsPerImage 1 / SubfolderName %USERPROFILE%\Pictures\avif_out / KeepMetadata true / SchemaVersion 3`。**`Quality 20` 就是上一轮"紫绿色块"实验 H2 的头号嫌疑**，装好后先别急着判断，把质量拉到 60 再转一次对比。
- 引擎仍是 libavif 1.4.2 + **SVT-AV1 v4.1.0**（静态）。4.2.0 的编译方案在 `build-engine\`，需要你自己那台有 git/cmake/nasm/MSVC 的机器跑；编出来把 `tools\avifenc.exe` 换掉再 `.\build.ps1` 即可。
- UI 的「12 bit（专业）」档位必然失败（SVT 只支持 8/10 bit），未改动。

## 重编

```powershell
cd <仓库根目录>
.\build.ps1            # 自动探测离线包目录；找不到就从 nuget.org 在线还原
```
本机构建产物当时仍在原位（`dist\`、`publish\`、`tools\avifenc.exe`、`.tools\wix.exe`），
但 `.gitignore` 已把这些全部排除：clone 出去的人需要自己准备 `tools\avifenc.exe`
（见 `tools\README.md`），WiX 5.0.2 由 `build.ps1` 自动 `dotnet tool install`，
所以外部机器上的构建输入只有 **.NET 10 SDK** 一项。

## 目录

```
avif-tool-1.1/
  dist\avif tool 1.1.msi      交付物
  publish\                    AvifForge.exe + avifenc.exe（打包输入）
  src\AvifForge\              19 个源文件（含 1.0.2 的 5 项默认改动）
  installer\                  package.wxs + THIRD-PARTY-NOTICES.txt
  tools\avifenc.exe           编码引擎（未被替换）
  build-engine\               SVT-AV1 4.2.0 编译方案（待你在有网络的机器上跑）
  .tools\                     WiX 5.0.2
  build.ps1                   一键重编（离线）
  dump-tables-102.ps1         MSI 表转储（验证用）
  ENGINE-REPLACEMENT.md       引擎替换分析
```
