using System.Windows;
using System.Windows.Threading;
using AvifForge.Services;
using AvifForge.ViewModels;

namespace AvifForge;

/// <summary>
/// 应用入口。组合根：在这里创建服务与 MainViewModel，并保证退出时清理一切子进程。
/// </summary>
public partial class App : Application
{
    public static MainViewModel ViewModel { get; private set; } = null!;

    private static bool _smokeMode;
    private static string? _smokeFile;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnUnhandledException;

        _smokeMode = e.Args.Contains("--smoke", StringComparer.OrdinalIgnoreCase);
        if (_smokeMode)
        {
            _smokeFile = Environment.GetEnvironmentVariable("AVIFFORGE_SMOKE_FILE");
        }

        var store = new SettingsStore();
        var settings = store.Load();
        var runner = new AvifEncRunner();
        var scheduler = new ConversionScheduler(runner, settings);

        ViewModel = new MainViewModel(settings, store, runner, scheduler);
        ViewModel.ApplyThemeFromSettings();

        var window = new MainWindow();
        MainWindow = window;
        window.DataContext = ViewModel;
        window.Show();

        // 引擎探测在后台进行，不阻塞窗口显示
        _ = ViewModel.InitializeEngineProbeAsync();

        if (_smokeMode)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    string line = $"SMOKE-OK width={window.ActualWidth:0} height={window.ActualHeight:0} " +
                                  $"engineReady={ViewModel.EngineReady} theme={Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme()}";
                    if (_smokeFile is not null)
                    {
                        System.IO.File.WriteAllText(_smokeFile, line);
                    }
                    else
                    {
                        Console.Error.WriteLine(line);
                    }
                }
                finally
                {
                    // Environment.Exit 不走 Run 循环，OnExit 不会触发：显式执行同样的清理
                    try
                    {
                        ViewModel.RequestShutdown();
                        ViewModel.SaveSettings();
                    }
                    catch
                    {
                        // 退出路径不弹窗
                    }

                    Environment.Exit(0);
                }
            };
            timer.Start();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            // 释放内存与句柄：取消任务、杀死全部 avifenc 子进程、Dispose 资源
            ViewModel.RequestShutdown();
            ViewModel.SaveSettings();
        }
        catch
        {
            // 退出路径绝不弹错误框
        }
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        string detail = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}{Environment.NewLine}";
        try
        {
            string logDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvifForge", "logs");
            System.IO.Directory.CreateDirectory(logDir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(logDir, "crash.log"), detail);
        }
        catch
        {
            // 日志失败不改变弹窗行为
        }

        if (_smokeMode)
        {
            try
            {
                if (_smokeFile is not null)
                {
                    System.IO.File.WriteAllText(_smokeFile, "SMOKE-FAIL " + e.Exception.Message);
                }
            }
            catch
            {
                // 忽略
            }

            Environment.Exit(2);
            return;
        }

        MessageBox.Show(
            e.Exception.Message,
            "AvifForge 遇到错误",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true;
    }
}
