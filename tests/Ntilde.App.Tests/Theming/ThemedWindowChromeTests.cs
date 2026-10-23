using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Ntilde.Shell.Theming;
using Ntilde.VT;
using Xunit;

namespace Ntilde.Tests.Theming;

/// <summary>
/// Code-built dialogs used to be painted once at open with copied colours, so one left open kept
/// the old theme. They now hold the token brushes and follow a switch while open.
/// </summary>
public class ThemedWindowChromeTests
{
    [AvaloniaFact]
    public void An_open_dialog_follows_a_theme_switch()
    {
        var dialog = new Window();
        ThemedWindowChrome.Apply(dialog);
        dialog.Show();
        try
        {
            ThemeService.Apply(TestThemes.GitHubLight, 1.0);
            Dispatcher.UIThread.RunJobs();

            var expected = ThemeTokens.Derive(TestThemes.GitHubLight, 1.0);
            Assert.Equal(expected[ThemeKeys.WindowBg], ((ISolidColorBrush)dialog.Background!).Color);
            Assert.Equal(expected[ThemeKeys.Fg], ((ISolidColorBrush)dialog.Foreground!).Color);
        }
        finally
        {
            dialog.Close();
            ThemeService.Apply(new TerminalTheme(), 1.0);
        }
    }
}
