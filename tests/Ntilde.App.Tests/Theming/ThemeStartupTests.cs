using System;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Ntilde.Shell;
using Ntilde.Shell.Theming;
using Ntilde.Tests.Core;
using Ntilde.VT;
using Xunit;

namespace Ntilde.Tests.Theming;

public sealed class ThemeStartupTests : IDisposable
{
    public void Dispose() => TestMainWindowFactory.DisposeCreatedWindows();

    [AvaloniaFact]
    public void App_installs_theme_tokens_before_any_window_exists()
    {
        Assert.True(Application.Current!.Resources.MergedDictionaries.Count > 0);
        Assert.True(Application.Current.TryFindResource(ThemeKeys.Panel, out var value));
        Assert.IsType<SolidColorBrush>(value);
    }

    [AvaloniaFact]
    public void MainWindow_theme_refresh_pushes_the_active_theme_to_the_app()
    {
        var window = TestMainWindowFactory.Create();
        var settings = GetSettings(window);
        // Mutating the cached ActiveTheme keeps this independent of the test theme directory.
        settings.ActiveTheme.Background = TermColor.FromRgb(245, 242, 233);

        InvokePrivate(window, "ApplyThemeToUI");

        Assert.Equal(ThemeVariant.Light, Application.Current!.RequestedThemeVariant);
        Assert.True(Application.Current.TryFindResource(ThemeKeys.WindowBg, out var value));
        Assert.Equal(Color.FromRgb(245, 242, 233), Assert.IsType<SolidColorBrush>(value).Color);
    }

    private static TerminalSettings GetSettings(Ntilde.MainWindow window)
    {
        var field = typeof(Ntilde.MainWindow).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (TerminalSettings)field!.GetValue(window)!;
    }

    private static void InvokePrivate(Ntilde.MainWindow window, string name)
    {
        var method = typeof(Ntilde.MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(window, Array.Empty<object>());
    }
}
