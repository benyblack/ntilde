using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ntilde.AgentOutput;
using Ntilde.Shell.Theming;
using Ntilde.VT;
using Xunit;

namespace Ntilde.Tests.Theming;

/// <summary>
/// Agent Output's markdown asked for Nt* keys nothing in its tree defined, so it always fell back
/// to a fixed dark palette. It now resolves the app tokens, and the panel around it does too, so a
/// light theme gets dark text on a light panel rather than either half alone.
/// </summary>
public class MarkdownThemeTokenTests
{
    [AvaloniaFact]
    public void Markdown_resolves_the_app_tokens_it_used_to_miss()
    {
        var anchor = new Border();
        var window = new Window { Content = anchor };
        window.Show();
        try
        {
            var theme = MarkdownTheme.Resolve(anchor);

            Assert.Same(Application.Current!.FindResource(ThemeKeys.Link), theme.Accent);
            Assert.Same(Application.Current.FindResource(ThemeKeys.DiffAdded), theme.Added);
            Assert.Same(Application.Current.FindResource(ThemeKeys.PanelAlt), theme.CodeBackground);
        }
        finally
        {
            window.Close();
        }
    }

    // A detached Border has no parent and no StylingParent; only a TopLevel links to the
    // Application, so the lookup misses and takes the fallback.
    [AvaloniaFact]
    public void A_detached_anchor_falls_back_to_the_current_token_colors()
    {
        ThemeService.Apply(TestThemes.Nord, 1.0);
        try
        {
            var theme = MarkdownTheme.Resolve(new Border());

            Assert.Equal(ThemeService.Current[ThemeKeys.Link], ((ISolidColorBrush)theme.Accent).Color);
        }
        finally
        {
            ThemeService.Apply(new TerminalTheme(), 1.0);
        }
    }

    [AvaloniaFact]
    public void The_panel_chrome_follows_a_light_theme()
    {
        var panel = new AgentOutputPanel();
        var window = new Window { Content = panel };
        window.Show();
        try
        {
            ThemeService.Apply(TestThemes.GitHubLight, 1.0);
            Dispatcher.UIThread.RunJobs();

            var chrome = panel.GetVisualDescendants().OfType<Border>().First();
            var expected = ThemeTokens.Derive(TestThemes.GitHubLight, 1.0);
            Assert.Equal(expected[ThemeKeys.Panel], ((ISolidColorBrush)chrome.Background!).Color);
            Assert.Equal(expected[ThemeKeys.Hairline], ((ISolidColorBrush)chrome.BorderBrush!).Color);
        }
        finally
        {
            window.Close();
            ThemeService.Apply(new TerminalTheme(), 1.0);
        }
    }
}
