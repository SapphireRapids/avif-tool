using AvifForge.Models;
using AvifForge.Services;
using IoPath = System.IO.Path;
using IoFile = System.IO.File;
using IoInfo = System.IO.FileInfo;

// 端到端引擎测试：完全走应用自己的 AvifEncRunner 代码路径（参数拼装 + 进程执行 + 结果判定）。
// 输入图默认取仓库内的 test/samples；也可用第一个参数指向别的工作目录。
// （Path / File / Directory 需要别名或全限定：本 csproj 开了 UseWPF，System.Windows.Shapes.Path 会撞名。）
var w = args.Length > 0
    ? System.IO.Path.GetFullPath(args[0])
    : System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "samples"));
if (!System.IO.Directory.Exists(w))
{
    Console.WriteLine($"sample dir not found: {w}");
    Console.WriteLine("usage: dotnet run --project test\\EngineTest -- <dir with test_opaque.png / test_alpha.png / clean.jpg>");
    return 2;
}
var runner = new AvifEncRunner();

Console.WriteLine("probe: " + await runner.ProbeAsync(default));
Console.WriteLine(runner.EngineStatus.Replace("\n", "\n  "));
Console.WriteLine();

int failures = 0;

async Task Case(string name, string input, AppSettings settings)
{
    string output = IoPath.Combine(w, $"ET_{name}.avif");
    if (IoFile.Exists(output))
    {
        IoFile.Delete(output);
    }

    Console.WriteLine($"[args] {string.Join(' ', runner.BuildArguments(input, output, settings))}");
    var res = await runner.EncodeAsync(input, output, settings, default);
    long size = res.Success ? new IoInfo(output).Length : 0;
    Console.WriteLine($"[{(res.Success ? "PASS" : "FAIL")}] {name}: {size} bytes in {res.ElapsedSeconds:0.##}s {res.ErrorDetail}");
    if (!res.Success)
    {
        failures++;
    }
}

await Case("default", IoPath.Combine(w, "test_opaque.png"), new AppSettings());
await Case("maxq-alpha", IoPath.Combine(w, "test_alpha.png"), new AppSettings { MaxQuality = true });
await Case("depth10-qalpha", IoPath.Combine(w, "test_alpha.png"), new AppSettings { Depth = DepthChoice.Bit10, QualityAlpha = 90 });
await Case("adv-tune", IoPath.Combine(w, "clean.jpg"), new AppSettings { AdvancedParams = "tune=0;preset=7", KeepMetadata = true });
await Case("limited", IoPath.Combine(w, "clean.jpg"), new AppSettings { Range = RangeChoice.Limited, Speed = 8 });
await Case("q20", IoPath.Combine(w, "test_opaque.png"), new AppSettings { Quality = 20, Speed = 8 });

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL CASES PASSED" : $"{failures} CASE(S) FAILED");
return failures == 0 ? 0 : 1;
