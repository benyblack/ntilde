using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Ntilde.Shell.Theming;
using Ntilde.VT;
using Xunit;

namespace Ntilde.Tests.Theming;

/// <summary>
/// The connection manager used to carry its own palette, recoloured only when MainWindow
/// remembered to call its ApplyTheme, and its hover fills were frozen dark hex. It now resolves
/// the app-wide tokens, so it follows every switch with no call at all.
/// </summary>
public class ConnectionManagerThemeTests
{
    [AvaloniaFact]
    public void ConnectionManager_FollowsAThemeSwitchWithoutAnApplyThemeCall()
    {
        var window = new Ntilde.ConnectionManagerWindow();
        window.Show();
        try
        {
            ThemeService.Apply(TestThemes.GitHubLight, 1.0);
            Dispatcher.UIThread.RunJobs();

            var manager = window.Manager!;
            var expected = ThemeTokens.Derive(TestThemes.GitHubLight, 1.0);
            Assert.Equal(expected[ThemeKeys.WindowBg], ((ISolidColorBrush)manager.Background!).Color);
            Assert.Equal(expected[ThemeKeys.WindowBg], ((ISolidColorBrush)window.Background!).Color);
            Assert.Equal(expected[ThemeKeys.Fg], ((ISolidColorBrush)manager.Foreground!).Color);
        }
        finally
        {
            window.Close();
            ThemeService.Apply(new TerminalTheme(), 1.0);
        }
    }

    [AvaloniaFact]
    public void ConnectionManager_DeclaresNoLocalThemeTokens()
    {
        var manager = new Ntilde.Controls.ConnectionManager();

        var localKeys = manager.Resources.Keys
            .Concat(manager.Resources.MergedDictionaries.OfType<IResourceDictionary>().SelectMany(merged => merged.Keys))
            .OfType<string>();
        Assert.DoesNotContain(localKeys, key => ThemeKeys.All.Contains(key));
    }

    /// <summary>Controls hold the app's brush instances, which ThemeService recolours in place.</summary>
    [AvaloniaFact]
    public void ConnectionManager_ControlsHoldTheAppTokenBrushes()
    {
        var manager = new Ntilde.Controls.ConnectionManager();
        var host = new Window { Width = 1080, Height = 720, Content = manager };
        host.Show();
        try
        {
            var windowBg = Assert.IsType<SolidColorBrush>(Application.Current!.FindResource(ThemeKeys.WindowBg));
            var rootGrid = Assert.IsType<Grid>(manager.Content);
            Assert.Same(windowBg, rootGrid.Background);
        }
        finally
        {
            host.Close();
        }
    }
}
