using System;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Ntilde.Controls;
using Ntilde.Shell;
using Ntilde.Shell.Theming;
using Ntilde.Tests.Core;
using Ntilde.VT;
using Xunit;

namespace Ntilde.Tests.Theming;

/// <summary>
/// Spec D7: a pane whose profile picks its own theme carries its own token scope, so its overlays
/// match its terminal. The app's theme reaches every other pane, an app switch never clobbers an
/// overriding pane, and clearing the profile theme hands the pane back to the app.
/// </summary>
public sealed class PaneThemeScopeTests
{
    [AvaloniaFact]
    public void A_profile_theme_gives_the_pane_its_own_tokens_and_an_app_switch_leaves_them()
    {
        using var root = SettingsTestRoot.WithSettings("""{"ThemeName":"Default"}""");
        root.CopyShippedTheme("GitHubLight.json");
        root.CopyShippedTheme("Nord.json");
        using var pane = new TerminalPane();
        var window = new Window { Content = pane };
        window.Show();
        try
        {
            var profile = new TerminalProfile { Name = "light profile", ThemeName = "GitHub Light" };
            SetProfile(pane, profile);
            var settings = new TerminalSettings { ThemeName = "Default" };

            pane.ApplySettings(settings);
            Dispatcher.UIThread.RunJobs();

            var paneLight = ThemeTokens.Derive(settings.ThemeManager.GetTheme("GitHub Light"), settings.WindowOpacity);
            var toast = pane.FindControl<Border>("ToastPanel")!;
            Assert.True(pane.HasPaneThemeScope);
            Assert.Equal(paneLight[ThemeKeys.Panel], Resolved(toast, ThemeKeys.Panel));
            Assert.Equal(Avalonia.Styling.ThemeVariant.Light, toast.ActualThemeVariant);
            Assert.Equal(paneLight[ThemeKeys.Overlay], ((ISolidColorBrush)toast.Background!).Color);
            Assert.Equal(paneLight[ThemeKeys.ChromeBg], ((ISolidColorBrush)pane.FindControl<Border>("StatusBar")!.Background!).Color);
            Assert.Equal(paneLight[ThemeKeys.PaneDim], ((ISolidColorBrush)pane.FindControl<Border>("InactiveOverlay")!.Background!).Color);

            // An app switch re-applies settings to every pane; this one keeps its own theme.
            var nord = settings.ThemeManager.GetTheme("Nord");
            ThemeService.Apply(nord, 1.0);
            settings.ThemeName = "Nord";
            pane.ApplySettings(settings);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(paneLight[ThemeKeys.Panel], Resolved(toast, ThemeKeys.Panel));

            // Clearing the profile theme hands the pane back to the app.
            profile.ThemeName = null;
            pane.ApplySettings(settings);
            Dispatcher.UIThread.RunJobs();
            Assert.False(pane.HasPaneThemeScope);
            Assert.Equal(ThemeTokens.Derive(nord, 1.0)[ThemeKeys.Panel], Resolved(toast, ThemeKeys.Panel));
        }
        finally
        {
            window.Close();
            ThemeService.Apply(new TerminalTheme(), 1.0);
        }
    }

    [AvaloniaFact]
    public void A_profile_naming_the_app_theme_needs_no_scope()
    {
        using var root = SettingsTestRoot.WithSettings("""{"ThemeName":"Default"}""");
        using var pane = new TerminalPane();
        SetProfile(pane, new TerminalProfile { Name = "same", ThemeName = "Default" });

        pane.ApplySettings(new TerminalSettings { ThemeName = "Default" });

        Assert.False(pane.HasPaneThemeScope);
    }

    private static Color Resolved(Control control, string key)
        => Assert.IsAssignableFrom<ISolidColorBrush>(control.FindResource(key)).Color;

    private static void SetProfile(TerminalPane pane, TerminalProfile profile)
        => typeof(TerminalPane).GetProperty(nameof(TerminalPane.Profile), BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(pane, profile);
}
