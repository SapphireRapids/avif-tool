using AvifForge.Models;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace AvifForge.Services;

/// <summary>浅色/深色主题切换（WPF-UI Fluent）。</summary>
public static class ThemeService
{
    public static void Apply(ThemeChoice choice)
    {
        ApplicationTheme theme = choice switch
        {
            ThemeChoice.Light => ApplicationTheme.Light,
            ThemeChoice.Dark => ApplicationTheme.Dark,
            _ => SystemThemeManager.GetCachedSystemTheme() switch
            {
                SystemTheme.Dark => ApplicationTheme.Dark,
                _ => ApplicationTheme.Light,
            },
        };

        // Windows 11 上获得 Mica 云母材质，Windows 10 自动回退为纯色背景
        ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, updateAccent: true);
    }
}
