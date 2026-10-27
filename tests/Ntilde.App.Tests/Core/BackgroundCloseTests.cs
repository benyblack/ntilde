using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.Controls;
using static Ntilde.Tests.Core.WindowTestSupport;

namespace Ntilde.Tests.Core;

/// <summary>
/// A close in a tab the user is not on - a shell exiting there, a middle-click on it - moves nothing: the user's active
/// pane (what Close Pane, Split and the other pane commands act on) and keyboard stay where they are. The tab only
/// remembers which of its panes to give back when it is next selected. Before, a background split made its surviving
/// pane the active one, and closing a zoomed background tab left no active pane at all.
/// </summary>
public sealed class BackgroundCloseTests : IClassFixture<TestAppDataRoot>, IDisposable
{
    public void Dispose() => TestMainWindowFactory.DisposeCreatedWindows();

    private static TabControl TabsOf(MainWindow window) => window.FindControl<TabControl>("Tabs")!;

    private static TerminalPane? CurrentPane(MainWindow window) =>
        (TerminalPane?)typeof(MainWindow).GetProperty("_currentPane", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);

    private static void Select(MainWindow window, TabItem tab)
    {
        TabsOf(window).SelectedItem = tab;
        Dispatcher.UIThread.RunJobs();
    }

    private static void AssertKeyboardNotIn(MainWindow window, TabItem tab)
    {
        var focused = TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement();
        var owner = focused is Avalonia.Visual v ? Avalonia.VisualTree.VisualExtensions.FindAncestorOfType<TerminalPane>(v, true) : null;
        Assert.False(owner is not null && ReferenceEquals(Invoke(window, "ResolveOwningTabForPane", owner), tab), "keyboard focus moved into the background tab");
    }

    /// <summary>The window's first tab, a split opened beside it, and the user back on the first tab.</summary>
    private static (TabItem UserTab, TerminalPane UserPane, TabItem Split) UserTabAndBackgroundSplit(
        MainWindow window, out TerminalPane first, out TerminalPane second)
    {
        var userTab = (TabItem)TabsOf(window).Items[0]!;
        TabItem split = AddSplitTab(window, out first, out second);
        Select(window, userTab);
        var userPane = CurrentPane(window)!;
        Assert.Same(userTab.Content, userPane);
        return (userTab, userPane, split);
    }

    [AvaloniaFact]
    public async Task A_shell_exiting_in_a_background_split_keeps_the_users_pane()
    {
        MainWindow window = NewShownWindow();
        var (userTab, userPane, split) = UserTabAndBackgroundSplit(window, out TerminalPane first, out TerminalPane second);

        bool closed = await (Task<bool>)Invoke(window, "ClosePaneAsync", first, true)!; // the shell-exit path
        Dispatcher.UIThread.RunJobs();

        Assert.True(closed);
        Assert.Same(userTab, TabsOf(window).SelectedItem);
        Assert.Same(userPane, CurrentPane(window));
        AssertKeyboardNotIn(window, split);
        Assert.Same(second, split.Content);

        // The split gives its surviving pane back when the user goes there.
        Select(window, split);
        Assert.Same(second, CurrentPane(window));
    }

    [AvaloniaFact]
    public async Task A_shell_exiting_in_a_zoomed_background_split_keeps_the_users_pane()
    {
        MainWindow window = NewShownWindow();
        var userTab = (TabItem)TabsOf(window).Items[0]!;
        TabItem split = AddSplitTab(window, out TerminalPane zoomed, out TerminalPane other);
        Select(window, split);
        Assert.True((bool)Invoke(window, "EnterPaneZoom", split, zoomed, true)!);
        Select(window, userTab);
        var userPane = CurrentPane(window)!;

        bool closed = await (Task<bool>)Invoke(window, "ClosePaneAsync", zoomed, true)!;
        Dispatcher.UIThread.RunJobs();

        Assert.True(closed);
        Assert.Same(userPane, CurrentPane(window));
        AssertKeyboardNotIn(window, split);
        Assert.Same(other, split.Content);
        Select(window, split);
        Assert.Same(other, CurrentPane(window));
    }

    [AvaloniaFact]
    public async Task Closing_a_zoomed_background_tab_keeps_the_users_pane()
    {
        MainWindow window = NewShownWindow();
        var userTab = (TabItem)TabsOf(window).Items[0]!;
        TabItem split = AddSplitTab(window, out TerminalPane zoomed, out _);
        Select(window, split);
        Assert.True((bool)Invoke(window, "EnterPaneZoom", split, zoomed, true)!);
        Select(window, userTab);
        var userPane = CurrentPane(window)!;

        bool closed = await (Task<bool>)Invoke(window, "CloseTabAsync", split, true)!; // a middle-click on it
        Dispatcher.UIThread.RunJobs();

        Assert.True(closed);
        Assert.DoesNotContain(split, TabsOf(window).Items.Cast<TabItem>());
        Assert.Same(userTab, TabsOf(window).SelectedItem);
        Assert.Same(userPane, CurrentPane(window));
    }

    [AvaloniaFact]
    public async Task A_shell_exiting_beside_the_users_pane_in_their_own_split_still_moves_to_the_sibling()
    {
        // Unchanged where the user is: the pane they were on went, so they land on what took its place.
        MainWindow window = NewShownWindow();
        TabItem split = AddSplitTab(window, out TerminalPane first, out TerminalPane second);
        Select(window, split);
        Invoke(window, "UpdateActivePane", first);

        bool closed = await (Task<bool>)Invoke(window, "ClosePaneAsync", first, true)!;
        Dispatcher.UIThread.RunJobs();

        Assert.True(closed);
        Assert.Same(second, CurrentPane(window));
    }
}
