using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Ntilde.Settings.Pages;
using Ntilde.Shell.Theming;
using Ntilde.Tests.Core;
using Ntilde.VT;
using Xunit;

namespace Ntilde.Tests.Theming;

/// <summary>
/// The Settings window has no palette of its own any more: every page, style and code-built row
/// resolves the app-wide tokens ThemeService installs, so a theme switch made anywhere recolours it.
/// Each test puts the Default theme back, because the tokens are application-wide.
/// </summary>
public class SettingsWindowThemeTests
{
    [AvaloniaFact]
    public void SettingsWindow_DeclaresNoLocalThemeTokens()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();

        // Own entries and merged dictionaries alike: either would shadow the app's tokens.
        var localKeys = window.Resources.Keys
            .Concat(window.Resources.MergedDictionaries.OfType<IResourceDictionary>().SelectMany(merged => merged.Keys))
            .OfType<string>();
        Assert.DoesNotContain(localKeys, key => ThemeKeys.All.Contains(key));
    }

    // Review Focus 4: a switch made elsewhere (the palette's "Theme: X", Cancel) must reach an open Settings.
    [AvaloniaFact]
    public void SettingsWindow_FollowsAThemeSwitchMadeElsewhere()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();
        window.Show();
        try
        {
            var cancel = window.FindControl<Button>("BtnCancel")!; // Pill: Background = NtPanel via NtStyles
            var footer = cancel.GetLogicalAncestors().OfType<Border>().First(); // NtChromeBg

            ThemeService.Apply(TestThemes.GitHubLight, 1.0);
            Dispatcher.UIThread.RunJobs();

            var expected = ThemeTokens.Derive(TestThemes.GitHubLight, 1.0);
            Assert.Equal(expected[ThemeKeys.WindowBg], ((ISolidColorBrush)window.Background!).Color);
            Assert.Equal(expected[ThemeKeys.Fg], ((ISolidColorBrush)window.Foreground!).Color);
            Assert.Same(AppBrush(ThemeKeys.Panel), cancel.Background);
            Assert.Same(AppBrush(ThemeKeys.ChromeBg), footer.Background);
            Assert.Equal(expected[ThemeKeys.ChromeBg], ((ISolidColorBrush)footer.Background!).Color);
        }
        finally
        {
            window.Close();
            ThemeService.Apply(new TerminalTheme(), 1.0);
        }
    }

    /// <summary>
    /// The Delete button was fixed dark maroon (#3a1a1d), which read as a dark blot on light themes.
    /// It now takes the theme's error tint, and its text the theme's error colour.
    /// </summary>
    [AvaloniaFact]
    public void DangerButton_UsesTheErrorTokens()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();
        window.Show();
        try
        {
            ThemeService.Apply(TestThemes.GitHubLight, 1.0);
            Dispatcher.UIThread.RunJobs();

            var delete = window.GetPage<AppearancePage>().FindControl<Button>("BtnDeleteTheme")!;
            Assert.Same(AppBrush(ThemeKeys.ErrorDim), delete.Background);
            Assert.Same(AppBrush(ThemeKeys.ErrorBorder), delete.BorderBrush);
            Assert.Same(AppBrush(ThemeKeys.Error), delete.Foreground);
        }
        finally
        {
            window.Close();
            ThemeService.Apply(new TerminalTheme(), 1.0);
        }
    }

    /// <summary>
    /// A slider preview is not written to the session until Save. Picking a theme after dragging
    /// the opacity slider used to repaint with the saved opacity, undoing the preview app-wide.
    /// </summary>
    [AvaloniaFact]
    public void PickingATheme_KeepsThePreviewedOpacity()
    {
        using var root = SettingsTestRoot.WithSettings("""{"ThemeName":"Default","WindowOpacity":1.0}""");
        root.CopyShippedTheme("GitHubLight.json");
        var window = new Ntilde.SettingsWindow();
        window.Show();
        try
        {
            var page = window.GetPage<AppearancePage>();
            page.FindControl<Slider>("WindowOpacitySlider")!.Value = 0.5;
            var themes = page.FindControl<ComboBox>("ThemeList")!;
            themes.SelectedItem = themes.Items.OfType<ComboBoxItem>().Single(item => (string?)item.Content == "GitHub Light");
            Dispatcher.UIThread.RunJobs();

            var expected = ThemeTokens.Derive(new Ntilde.Shell.ThemeManager().GetTheme("GitHub Light"), 0.5);
            Assert.Equal(expected[ThemeKeys.SidebarBg], ThemeService.Current[ThemeKeys.SidebarBg]);
        }
        finally
        {
            window.Close();
            ThemeService.Apply(new TerminalTheme(), 1.0);
        }
    }

    private static SolidColorBrush AppBrush(string key) =>
        Assert.IsType<SolidColorBrush>(Application.Current!.FindResource(key));
}
