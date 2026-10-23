using System;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ntilde.Shell;
using Ntilde.Shell.Theming;
using Ntilde.Tests.Core;
using Ntilde.VT;
using Xunit;

namespace Ntilde.Tests.Theming;

/// <summary>
/// The tab strip used to recompute a black-or-white contrast colour and allocate brushes on every
/// visual pass, with the raw ANSI blue as its selection line. Headers now inherit the NtTab*
/// tokens, so a theme switch reaches them without a rebuild, and a pass reuses the token
/// instances. Each test puts the Default theme back: the tokens are application-wide.
/// TestAppDataRoot keeps the session a closing window saves out of the real data folder.
/// </summary>
public sealed class TabStripThemeTests : IClassFixture<TestAppDataRoot>, IDisposable
{
    public void Dispose() => TestMainWindowFactory.DisposeCreatedWindows();

    [AvaloniaFact]
    public void Horizontal_headers_follow_a_theme_switch_without_a_rebuild()
    {
        var window = TestMainWindowFactory.Create();
        window.Show();
        try
        {
            var tabs = window.FindControl<TabControl>("Tabs")!;
            var unselected = new TabItem { Content = new Border() };
            tabs.Items.Add(unselected);
            window.UpdateTabVisuals();
            Dispatcher.UIThread.RunJobs();
            unselected.Header = new Border { Child = new TextBlock { Text = "plain" } };
            var selected = (TabItem)tabs.SelectedItem!;

            ThemeService.Apply(TestThemes.GitHubLight, 1.0);
            Dispatcher.UIThread.RunJobs();

            var expected = ThemeTokens.Derive(TestThemes.GitHubLight, 1.0);
            Assert.Equal(expected[ThemeKeys.TabFgSelected], TitleColor(selected));
            Assert.Equal(expected[ThemeKeys.TabFg], TitleColor(unselected));
        }
        finally
        {
            window.Close();
            ThemeService.Apply(new TerminalTheme(), 1.0);
        }
    }

    [AvaloniaFact]
    public void The_selection_line_is_the_indicator_token_and_a_pass_allocates_none()
    {
        var window = TestMainWindowFactory.Create();
        window.Show();
        try
        {
            var selected = (TabItem)window.FindControl<TabControl>("Tabs")!.SelectedItem!;
            var indicator = Application.Current!.FindResource(ThemeKeys.TabIndicator);

            window.UpdateTabVisuals();
            Assert.Same(indicator, selected.BorderBrush);

            window.UpdateTabVisuals();
            Assert.Same(indicator, selected.BorderBrush);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_vertical_sidebar_wears_the_sidebar_surface()
    {
        var window = TestMainWindowFactory.Create();
        window.Show();
        try
        {
            SetVertical(window);
            window.ApplyTabLayout();
            Dispatcher.UIThread.RunJobs();

            ThemeService.Apply(TestThemes.GitHubLight, 1.0);
            Dispatcher.UIThread.RunJobs();

            var tabs = window.FindControl<TabControl>("Tabs")!;
            var sidebar = tabs.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "PART_TabSidebar");
            var divider = tabs.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_TabSidebarDivider");
            var expected = ThemeTokens.Derive(TestThemes.GitHubLight, 1.0);
            Assert.Equal(expected[ThemeKeys.SidebarBg], ((ISolidColorBrush)sidebar.Background!).Color);
            Assert.True(divider.IsVisible);
            Assert.Equal(expected[ThemeKeys.SidebarDivider], ((ISolidColorBrush)divider.Background!).Color);
        }
        finally
        {
            window.Close();
            ThemeService.Apply(new TerminalTheme(), 1.0);
        }
    }

    private static Color TitleColor(TabItem tab)
    {
        var title = tab.Header switch
        {
            Border { Child: TextBlock text } => text,
            TextBlock text => text,
            _ => throw new InvalidOperationException("unexpected header shape"),
        };
        return Assert.IsAssignableFrom<ISolidColorBrush>(title.Foreground).Color;
    }

    private static void SetVertical(Ntilde.MainWindow window)
    {
        var field = typeof(Ntilde.MainWindow).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((TerminalSettings)field.GetValue(window)!).TabStripOrientation = "Vertical";
    }
}
