using System;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Ntilde.Settings.Pages;
using Ntilde.Shell;
using Ntilde.Shell.Theming;
using Ntilde.Shell.TitleBar;
using Ntilde.VT;
using Xunit;

namespace Ntilde.Tests.Core;

/// <summary>
/// Regression tests for the theme-application fixes:
///
/// - the Settings title-bar rows and shortcut rows used hardcoded dark hex backgrounds, so light
///   themes left navy cards on an otherwise light page; they now hold the app-wide NtHairline
///   token brush instance, which ThemeService recolors in place.
/// - the Default theme's ANSI blue RGB(0,0,238) was lifted straight into every UI accent brush,
///   producing eye-searing primary buttons; the accent is now softened for chrome use. That
///   softening lives in ThemeColorMath.SoftenAccent and is tested under Theming/.
/// - MainWindow.RebuildTitleBar replaces the generated title-bar buttons with fresh instances, so
///   the theme foregrounds an earlier ApplyThemeToUI had applied were lost - white icons under the
///   app's default Dark variant, invisible on light themes. They now take the NtFg token through
///   Fluent's ButtonForeground, so a fresh instance is already right.
/// </summary>
public sealed class ThemeApplicationRegressionTests : IClassFixture<TestAppDataRoot>, IDisposable
{
    /// <summary>
    /// Disposes the panes of every window this class asked for, and with them the real shells
    /// behind them. xUnit builds a fresh instance per test, so this runs after each one.
    /// </summary>
    public void Dispose() => TestMainWindowFactory.DisposeCreatedWindows();

    /// <summary>
    /// Both code-built card kinds must hold the app's token brush instances, which ThemeService
    /// recolors in place. Holding hardcoded hex (the regression) or a copy means light themes render
    /// dark navy cards.
    /// </summary>
    [AvaloniaFact]
    public void SettingsWindow_CodeBuiltRows_UseTheAppTokenBrushes()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();

        var hairlineBrush = Assert.IsType<SolidColorBrush>(window.FindResource(ThemeKeys.Hairline));
        Assert.Same(hairlineBrush, Avalonia.Application.Current!.FindResource(ThemeKeys.Hairline));

        var titleBarRows = window.GetPage<TabsAndTitleBarPage>().FindControl<StackPanel>("TitleBarItemsPanel")!.Children.OfType<Border>().ToList();
        Assert.NotEmpty(titleBarRows);
        foreach (var row in titleBarRows)
        {
            Assert.Null(row.Background);
            Assert.Same(hairlineBrush, row.BorderBrush);
        }

        var shortcutRows = window.GetPage<ShortcutsPage>().FindControl<StackPanel>("ShortcutBindingsPanel")!.Children.OfType<Border>().ToList();
        Assert.NotEmpty(shortcutRows);
        foreach (var row in shortcutRows)
        {
            Assert.Null(row.Background);
            Assert.Same(hairlineBrush, row.BorderBrush);
        }
    }

    /// <summary>
    /// A rebuild replaces the generated buttons with fresh instances. They must come out in the
    /// active theme's ink with nothing re-applied: under a light theme a button and its icon read
    /// dark (the NtFg token), never the white a dark-variant default would give. Connections rather
    /// than Tab List: the Tab List button takes an attention tint whenever tabs are hidden.
    /// </summary>
    [AvaloniaFact]
    public void RebuildTitleBar_LightTheme_AButtonAndIconTakeTheThemeForeground()
    {
        var window = TestMainWindowFactory.Create();
        window.Show();
        try
        {
            ThemeService.Apply(Ntilde.Tests.Theming.TestThemes.GitHubLight, 1.0);
            InvokeRebuildTitleBar(window);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var expected = ThemeTokens.Derive(Ntilde.Tests.Theming.TestThemes.GitHubLight, 1.0)[ThemeKeys.Fg];
            var button = GetTitleBarHost(window).Children.OfType<Button>()
                .Single(b => b.Name == TitleBarViewFactory.ButtonName("connections"));
            Assert.Equal(expected, Assert.IsAssignableFrom<ISolidColorBrush>(button.Foreground).Color);

            var icon = Assert.IsType<PathIcon>(button.Content);
            Assert.Equal(expected, Assert.IsAssignableFrom<ISolidColorBrush>(icon.Foreground).Color);
        }
        finally
        {
            window.Close();
            ThemeService.Apply(new TerminalTheme(), 1.0);
        }
    }

    private static StackPanel GetTitleBarHost(Ntilde.MainWindow window)
    {
        var host = window.FindControl<StackPanel>("TitleBarItemsHost");
        Assert.NotNull(host);
        return host!;
    }

    private static void InvokeRebuildTitleBar(Ntilde.MainWindow window)
    {
        var method = typeof(Ntilde.MainWindow).GetMethod("RebuildTitleBar", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(window, System.Array.Empty<object>());
    }
}
