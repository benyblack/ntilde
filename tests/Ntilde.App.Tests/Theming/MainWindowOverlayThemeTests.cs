using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Ntilde.Shell;
using Ntilde.Shell.Theming;
using Ntilde.Tests.Core;
using Ntilde.VT;
using Xunit;

namespace Ntilde.Tests.Theming;

/// <summary>
/// The main window's overlays were fixed dark islands: the command palette (#252526 with a black
/// scrim), the toasts (#20242C), the transfer overlay (#1E1E1E). Under a light theme the palette's
/// search text turned black on that dark panel. They now resolve the tokens, live. Each test puts
/// the Default theme back: the tokens are application-wide.
/// </summary>
public sealed class MainWindowOverlayThemeTests : IDisposable
{
    public void Dispose() => TestMainWindowFactory.DisposeCreatedWindows();

    [AvaloniaFact]
    public void The_command_palette_and_toasts_follow_a_light_theme()
    {
        var window = TestMainWindowFactory.Create();
        window.Show();
        try
        {
            ThemeService.Apply(TestThemes.GitHubLight, 1.0);
            Dispatcher.UIThread.RunJobs();
            var expected = ThemeTokens.Derive(TestThemes.GitHubLight, 1.0);

            var overlay = window.FindControl<Grid>("CommandPaletteOverlay")!;
            var panel = Assert.IsType<Border>(overlay.Children.Single());
            Assert.Equal(expected[ThemeKeys.Scrim], Color(overlay.Background));
            Assert.Equal(expected[ThemeKeys.Overlay], Color(panel.Background));
            Assert.Equal(expected[ThemeKeys.AccentBorder], Color(panel.BorderBrush));

            foreach (string name in new[] { "RecordingToast", "UpdateToast", "TransferOverlay" })
            {
                var toast = window.FindControl<Border>(name)!;
                Assert.Equal(expected[ThemeKeys.Overlay], Color(toast.Background));
                Assert.Equal(expected[ThemeKeys.Hairline], Color(toast.BorderBrush));
            }

            Assert.Equal(expected[ThemeKeys.Fg], Color(window.FindControl<TextBlock>("RecordingToastTitle")!.Foreground));
            Assert.Equal(expected[ThemeKeys.Fg2], Color(window.FindControl<TextBlock>("UpdateToastMessage")!.Foreground));
        }
        finally
        {
            window.Close();
            ThemeService.Apply(new TerminalTheme(), 1.0);
        }
    }

    [AvaloniaFact]
    public void A_transfer_state_is_a_theme_token()
    {
        var converter = new StateBrushConverter();
        object Convert(TransferState state) => converter.Convert(state, typeof(IBrush), null, CultureInfo.InvariantCulture);
        object Token(string key) => Application.Current!.FindResource(key)!;

        Assert.Same(Token(ThemeKeys.Info), Convert(TransferState.Running));
        Assert.Same(Token(ThemeKeys.Success), Convert(TransferState.Completed));
        Assert.Same(Token(ThemeKeys.Error), Convert(TransferState.Failed));
        Assert.Same(Token(ThemeKeys.Fg4), Convert(TransferState.Canceled));
    }

    private static Color Color(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;
}
