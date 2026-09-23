# 用官方 SVT-AV1 v4.2.0 重编 avifenc.exe — 操作步骤

这条路线的目标产物：`avifenc.exe` = **上游官方 libavif 源码 + 官方 SVT-AV1 v4.2.0**，静态、只含 SVT 编码器 —— 与你现在那个 exe 形态一致（单文件、无旁挂 DLL），所以替换只需要换一个文件。

> 依据全部来自上游仓库，2026-09-10 核实：
> - `ext/svt.cmd`（main 分支）里就是 `git clone -b v4.2.0 --depth 1 https://gitlab.com/AOMediaCodec/SVT-AV1.git` → **libavif 主干已经把 SVT 钉到 4.2.0**
> - 官方 CI 编 SVT 的方式：`SVT-AV1/Build/windows/build.bat release static no-apps`，然后 `copy Source\API\*.h include\svt-av1`
> - 官方 Windows 静态产物配方（`ci-windows-artifacts.yml`）：`cmake -A x64 -DBUILD_SHARED_LIBS=OFF … -DAVIF_BUILD_APPS=ON` → `build\Release\avifenc.exe`
> - 官方 **release 的 `windows-artifacts.zip` 里那个 avifenc.exe 用的是 AOM 编码 + dav1d 解码，没有 SVT** → 网上不存在"官方 libavif+SVT"的预编译包，只能自己编（这也是你现在这个 exe 的来历）

## 你需要准备的机器（本机不满足，所以编不了）

| 依赖 | 要求 | 本机实测 |
|---|---|---|
| 网络 | 能访问 github.com + gitlab.com | ❌ 沙箱内 `Invoke-WebRequest` 超时（放开 `danger-full-access` 仍超时） |
| git / cmake / nasm | 在 PATH 上（SVT 的 x86 汇编必须有 nasm） | ❌ 三个都不在 PATH |
| MSVC x64 | `cl.exe` 可用（VS 2022 的「使用 C++ 的桌面开发」+「CMake tools for Visual Studio」） | 未检测到（`cl` 不在 PATH） |
| clang-cl | 编 libyuv 用（libyuv 的汇编是 GCC 内联格式，MSVC 直接编不过） | 可选，缺就加 `-NoLibyuv` |

也就是说：**在任意一台装了 VS 2022（带 CMake + ClangCL 组件）+ NASM + Git 且有网的机器上跑这个脚本即可**，不需要别的准备。

## 跑起来

```powershell
# 开始菜单打开 "Developer PowerShell for VS 2022"（它会把 cl/cmake 塞进 PATH）
# 如果 nasm 不在 PATH：把 nasm.exe 所在目录加进去，或用 VS 自带的外部工具链路径

cd build-engine

# 推荐：主干（已经钉 4.2.0，与 libavif 的 SVT plugin 保证匹配）
.\build-avifenc.ps1

# 或者：保持 libavif 1.4.2 这个正式版，只把 SVT pin 抬到 4.2.0
.\build-avifenc.ps1 -LibavifRef v1.4.2 -SvtTag v4.2.0

# 想顺手验证编码可用：给一张图
.\build-avifenc.ps1 -Sample 'test\samples\test_opaque.png'
```

脚本做的事，按顺序：
1. 检查 git/cmake/nasm/cl 是否就位（缺哪个直接报错，不会跑到一半才失败）
2. 浅克隆 libavif（`main` 或 `v1.4.2`）
3. 把 `ext/svt.cmd`、`ext/svt.sh` 里的 `-b vX.Y.Z` 改成目标 tag（并打印改动前后的 pin，改不动会明确提示）
4. 依次跑官方 `ext/*.cmd`：zlibpng → libjpeg → libyuv → libsharpyuv → **svt**（`svt.cmd` 自己 clone SVT-AV1 并 `build.bat release static no-apps`，产出静态 `svt-av1.lib` + 头文件）
5. `cmake -A x64 -DBUILD_SHARED_LIBS=OFF -DAVIF_CODEC_SVT=LOCAL`，其余 codec 全 `OFF`（对齐你现在的 SVT-only 形态），`-DAVIF_JPEG=LOCAL -DAVIF_ZLIBPNG=LOCAL`（PNG/JPEG 输入必需）、`-DAVIF_ENABLE_WERROR=OFF`（跨 SVT 小版本时避免警告当错误）
6. `cmake --build build --config Release --parallel`
7. 收 `build\Release\avifenc.exe` → `build-engine\out\avifenc.exe`，打印大小 / SHA-256 / `--version`
8. **自动把关**：`--version` 里没有 `svt [enc]` 就直接抛错 —— 因为 `AvifEncRunner.ProbeAsync` 靠这个字符串判定引擎可用，缺了装上也只会显示"avifenc 不含 SVT-AV1 编码器"

## 验收标准（脚本已自动检查前 3 条）

1. `avifenc.exe --version` 输出含 `svt [enc]:v4.2.0`
2. `dumpbin /imports avifenc.exe` 只看到 `KERNEL32/VCRUNTIME140/api-ms-win-crt-*`，**没有** `SVT-AV1.dll`/`avif.dll` → 确认还是静态单文件（可以用 `<旧的解包验证目录>\imports.ps1` 直接查，不需要 dumpbin）
3. 用 `-c svt -s 6 -q 60 -d 10 -o t.avif x.png` 能出文件
4. 装进去后跑 `dotnet run --project test\EngineTest -c Release`（6 用例，走应用自己的 `EncodeAsync` 管线）
5. 大小量级 ~6 MB（你现在的是 6,431,744 B；4.2.0 会略有出入，正常）

## 交回给我之后我会做的（第 4/5 步之后的全自动部分）

把 `build-engine\out\avifenc.exe`（或直接告诉我它在哪）给我，我在 **`<仓库根目录>`** 这个副本里完成，不碰你原项目：
1. 备份现有 `tools\avifenc.exe` → `tools\avifenc.exe.old-1.4.2-svt4.1.0`
2. 放入新 exe，重跑 `dotnet publish`（离线包源已验证可用）+ `.tools\wix.exe build` → 出 `AvifForge-1.0.3-win-x64.msi`
3. 校验 MSI 载荷哈希 == 新 exe、`--version` 串、并按需跑 EngineTest
4. 交付 MSI 路径 + 一份新旧引擎差异说明（同图对比：体积 / 用时 / `nclx` 元数据）

## 两个需要你知道的判断

- **收益预期要放低**：SVT-AV1 4.1.0 → 4.2.0 的 changelog 主要落在 VOD/RTC/ARM NEON/熵编码；静态图相关只有一条 "Optimized still-image screen content detection"（对**截图类**可能有点收益）。而你现有的 4.1.0 已经含 "Improve Still Image coding efficiency" 和 "Add mutexes to fix hangs when running multiple instances of the encoder in one process"（这条对多进程并发有实测意义）。
- **顺带可能修好的东西**：官方配方是 `libsharpyuv` LOCAL 一起编的。你 README 里写着「`--sharpyuv` 与本静态构建的 libyuv 不兼容，已移除」—— 重编一次同源 tree 上的 libavif + libyuv + libsharpyuv 之后，这个开关有可能就能用了。而 `--sharpyuv` 正是缓解 RGB→4:2:0 重采样导致的高频/色度断层的开关，比换 SVT 版本号更贴近你在意的那个画质问题。（是否启用是代码层面的改动，需要你再明确让我动。）
