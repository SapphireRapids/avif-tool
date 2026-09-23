# `avifenc.exe` — 编码引擎（不入库）

本目录是应用运行时的**编码引擎**存放位置：

```
tools\avifenc.exe      # libavif avifenc，静态编译，仅内置 SVT-AV1 编码器
```

`src\AvifForge\AvifForge.csproj` 会在文件存在时把它作为邻接文件随 `AvifForge.exe` 一起
发布（`Condition="Exists(...)"`，所以缺失也能编译，只是无法真正转码）。

## 为什么仓库里没有它

`avifenc.exe` 是**第三方开源项目的构建产物**（[libavif](https://github.com/AOMediaCodec/libavif)
+ [SVT-AV1](https://gitlab.com/AOMediaCodec/SVT-AV1)），约 6 MB，不属于本仓库的源码，
因此被 `.gitignore` 排除。请自己获取，两种方式：

### 方式 A：自己构建（可复现，推荐）

```powershell
cd build-engine
.\build-avifenc.ps1 -Sample ..\..\test\samples\test_opaque.png
# 产物在 build-engine\out\avifenc.exe，确认后复制到 tools\avifenc.exe
```
前置条件与逐步说明见 `build-engine\README.md`（需要 VS 2022 C++ 工作负载、cmake、nasm，
以及联网）。构建脚本会校验 `--version` 输出里含 `svt [enc]` —— 应用的引擎自检依赖这个字符串。

### 方式 B：官方发布包

从 libavif 的 CI Windows 产物里取 `avifenc.exe`，**注意**：官方包通常同时链入 aom / dav1d /
rav1e 等多个编解码器，而本应用默认 `-c svt`，功能上可用，但体积与哈希会和方式 A 不同。
放入 `tools\avifenc.exe` 后跑一次 `dotnet run --project test\EngineTest` 验证。

## 校验

当前 1.1.0 发行包内附的引擎（SHA-256 前缀）：`EB82A6…FAB`，
来自 libavif 1.4.2 + SVT-AV1 v4.1.0 静态构建；许可证见 `installer\THIRD-PARTY-NOTICES.txt`。
