using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Ntilde.AgentHost;
using Ntilde.Controls;
using Ntilde.Shell;
using static Ntilde.Tests.Core.WindowTestSupport;

namespace Ntilde.Tests.Core;

/// <summary>
/// Focus mode, task 4: a user action that goes to another tab leaves focus mode first, and an agent's
/// spawn or close never moves the user while focus mode is on.
/// </summary>
public sealed class FocusModeNavigationTests : IClassFixture<TestAppDataRoot>, IDisposable
{
    public void Dispose() => TestMainWindowFactory.DisposeCreatedWindows();

    private static readonly RawInputModifiers CtrlShift = RawInputModifiers.Control | RawInputModifiers.Shift;

    private static TabControl TabsOf(MainWindow window) => window.FindControl<TabControl>("Tabs")!;

    private static TerminalPane? CurrentPane(MainWindow window) =>
        (TerminalPane?)typeof(MainWindow).GetProperty("_currentPane", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);

    private static void Select(MainWindow window, TabItem tab)
    {
        TabsOf(window).SelectedItem = tab;
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The window's first tab plus a second, single-pane tab opened by the user; the first is selected.</summary>
    private static (TabItem A, TabItem B) TwoTabs(MainWindow window)
    {
        var tabs = TabsOf(window);
        var a = (TabItem)tabs.Items[0]!;
        window.KeyPress(Key.T, CtrlShift, PhysicalKey.T, null);
        Dispatcher.UIThread.RunJobs();
        var b = (TabItem)tabs.SelectedItem!;
        Assert.NotSame(a, b);
        Select(window, a);
        return (a, b);
    }

    [AvaloniaFact]
    public void Next_tab_leaves_focus_mode_and_switches()
    {
        MainWindow window = NewShownWindow();
        var (a, b) = TwoTabs(window);
        // The MRU list only knows tabs that have been selected.
        Select(window, b);
        Select(window, a);
        window.EnterFocusMode();

        window.KeyPress(Key.Tab, RawInputModifiers.Control, PhysicalKey.Tab, null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.IsFocusMode);
        Assert.Same(b, TabsOf(window).SelectedItem);
    }

    [AvaloniaFact]
    public void New_tab_leaves_focus_mode_and_opens_it()
    {
        MainWindow window = NewShownWindow();
        var tabs = TabsOf(window);
        int before = tabs.Items.Count;
        window.EnterFocusMode();

        window.KeyPress(Key.T, CtrlShift, PhysicalKey.T, null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.IsFocusMode);
        Assert.Equal(before + 1, tabs.Items.Count);
        Assert.Same(tabs.Items[^1], tabs.SelectedItem);
    }

    [AvaloniaFact]
    public void Toggling_the_vertical_sidebar_leaves_focus_mode()
    {
        MainWindow window = NewShownWindow();
        bool vertical = TabStripLayout.IsVertical(
            ((TerminalSettings)typeof(MainWindow).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).TabStripOrientation);
        window.EnterFocusMode();

        window.KeyPress(Key.L, CtrlShift, PhysicalKey.L, null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.IsFocusMode);
        bool after = TabStripLayout.IsVertical(
            ((TerminalSettings)typeof(MainWindow).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).TabStripOrientation);
        Assert.NotEqual(vertical, after);
    }

    [AvaloniaFact]
    public void Opening_the_tab_list_leaves_focus_mode()
    {
        MainWindow window = NewShownWindow();
        window.EnterFocusMode();

        window.KeyPress(Key.O, CtrlShift, PhysicalKey.O, null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.IsFocusMode);
    }

    [AvaloniaFact]
    public void A_palette_tab_switch_leaves_focus_mode()
    {
        CommandRegistry.Clear();
        MainWindow window = NewShownWindow();
        var (a, b) = TwoTabs(window);
        Invoke(window, "SetupCommandPalette");
        window.EnterFocusMode();

        var tabCommands = CommandRegistry.GetCommands().Where(c => c.Category == "Tabs").ToList();
        Assert.Equal(2, tabCommands.Count);
        string labelOfB = (string)Invoke(window, "GetTabSwitchCommandLabel", b)!;
        Assert.Single(tabCommands, c => c.Title == labelOfB).Action();
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.IsFocusMode);
        Assert.Same(b, TabsOf(window).SelectedItem);
    }

    [AvaloniaFact]
    public void Splitting_and_equalising_stay_in_focus_mode()
    {
        MainWindow window = NewShownWindow();
        var selected = TabsOf(window).SelectedItem;
        window.EnterFocusMode();

        window.KeyPress(Key.D, CtrlShift, PhysicalKey.D, null);
        Dispatcher.UIThread.RunJobs();
        window.KeyPress(Key.G, CtrlShift, PhysicalKey.G, null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.IsFocusMode);
        Assert.Same(selected, TabsOf(window).SelectedItem);
    }

    [AvaloniaFact]
    public async Task Closing_one_pane_of_a_split_stays_and_closing_the_last_leaves()
    {
        MainWindow window = NewShownWindow();
        var (a, _) = TwoTabs(window);
        TabItem split = AddSplitTab(window, out _, out TerminalPane second);
        Select(window, split);
        Invoke(window, "UpdateActivePane", second);
        Assert.Same(second, CurrentPane(window));
        window.EnterFocusMode();

        await (Task)Invoke(window, "CloseActivePaneAsync", true)!;
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.IsFocusMode);

        Select(window, a);
        Assert.Same(a.Content, CurrentPane(window));
        window.EnterFocusMode();
        await (Task)Invoke(window, "CloseActivePaneAsync", true)!;
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.IsFocusMode);
    }

    [AvaloniaFact]
    public async Task Closing_the_zoomed_pane_of_a_split_stays_in_focus_mode()
    {
        MainWindow window = NewShownWindow();
        TwoTabs(window);
        TabItem split = AddSplitTab(window, out TerminalPane first, out TerminalPane second);
        Select(window, split);
        Invoke(window, "UpdateActivePane", first);
        Assert.True((bool)Invoke(window, "EnterPaneZoom", split, first, true)!);
        Assert.Same(first, CurrentPane(window));
        window.EnterFocusMode();

        // The zoom stash no longer holds the zoomed pane, but the tab still has two: it survives the close.
        await (Task)Invoke(window, "CloseActivePaneAsync", true)!;
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.IsFocusMode);
        Assert.Same(split, TabsOf(window).SelectedItem);
        Assert.Same(second, split.Content);
    }

    // Final review M2: reaching a pane of the tab on screen is not a tab change.

    [AvaloniaFact]
    public void Focusing_a_mux_session_shown_in_the_selected_tab_stays_in_focus_mode()
    {
        MainWindow window = NewShownWindow();
        TwoTabs(window);
        TabItem split = AddSplitTab(window, out TerminalPane first, out TerminalPane second);
        Select(window, split);
        Invoke(window, "UpdateActivePane", first);
        Guid id = Guid.NewGuid();
        second.MuxSessionIdToRestore = id;
        try
        {
            window.EnterFocusMode();

            Assert.True((bool)Invoke(window, "FocusPaneShowingMuxSession", Ntilde.Shell.Mux.MuxEndpointId.Local, id)!);
            Dispatcher.UIThread.RunJobs();

            Assert.True(window.IsFocusMode);
            Assert.Same(split, TabsOf(window).SelectedItem);
            Assert.Same(second, CurrentPane(window));
        }
        finally
        {
            second.MuxSessionIdToRestore = null;
        }
    }

    [AvaloniaFact]
    public void Focusing_a_mux_session_shown_in_another_tab_leaves_focus_mode_and_goes_there()
    {
        MainWindow window = NewShownWindow();
        var (a, b) = TwoTabs(window);
        var paneOfB = (TerminalPane)b.Content!;
        Guid id = Guid.NewGuid();
        paneOfB.MuxSessionIdToRestore = id;
        try
        {
            window.EnterFocusMode();

            Assert.True((bool)Invoke(window, "FocusPaneShowingMuxSession", Ntilde.Shell.Mux.MuxEndpointId.Local, id)!);
            Dispatcher.UIThread.RunJobs();

            Assert.False(window.IsFocusMode);
            Assert.Same(b, TabsOf(window).SelectedItem);
            Assert.Same(paneOfB, CurrentPane(window));
        }
        finally
        {
            paneOfB.MuxSessionIdToRestore = null;
        }
    }

    [AvaloniaFact]
    public async Task A_tab_closing_on_its_own_keeps_focus_mode_and_retitles_the_strip()
    {
        MainWindow window = NewShownWindow();
        var (a, b) = TwoTabs(window);
        var paneOfA = (TerminalPane)a.Content!;
        window.EnterFocusMode();

        // The shell-exit path (skipConfirm: true, no dialog): not a navigation verb.
        await (Task<bool>)Invoke(window, "ClosePaneAsync", paneOfA, true)!;
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.IsFocusMode);
        Assert.Same(b, TabsOf(window).SelectedItem);
        Assert.Equal((string)Invoke(window, "GetTabHeaderText", b)!, window.FindControl<TextBlock>("FocusStripTitle")!.Text);
    }

    [Fact]
    public void Agent_spawn_selects_only_outside_focus_mode()
    {
        Assert.True(MainWindow.ShouldAgentSpawnSelect(false));
        Assert.False(MainWindow.ShouldAgentSpawnSelect(true));
    }

    [AvaloniaFact]
    public async Task An_agent_spawn_in_focus_mode_opens_in_the_background_with_a_live_shell()
    {
        MainWindow window = NewShownWindow();
        var tabs = TabsOf(window);
        var selected = tabs.SelectedItem;
        var current = CurrentPane(window);
        window.EnterFocusMode();

        var (result, error) = await ((IAgentActionExecutor)window).SpawnAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Same(selected, tabs.SelectedItem);
        Assert.Same(current, CurrentPane(window));
        Assert.True(window.IsFocusMode);

        var spawnedTab = tabs.Items.Cast<TabItem>().Single(t => t.Content is TerminalPane p && p.PaneId == result!.Value.PaneId);
        Assert.NotSame(selected, spawnedTab);
        Assert.NotNull(((TerminalPane)spawnedTab.Content!).Session);
    }

    [AvaloniaFact]
    public async Task An_agent_close_in_focus_mode_does_not_select_the_target_tab()
    {
        MainWindow window = NewShownWindow();
        var tabs = TabsOf(window);
        var (a, b) = TwoTabs(window);
        var paneOfB = (TerminalPane)b.Content!;
        window.EnterFocusMode();

        bool closed = await ((IAgentActionExecutor)window).ClosePaneAsync(paneOfB.PaneId);
        Dispatcher.UIThread.RunJobs();

        Assert.True(closed);
        Assert.Same(a, tabs.SelectedItem);
        Assert.True(window.IsFocusMode);
        Assert.DoesNotContain(b, tabs.Items.Cast<TabItem>());
    }

    [AvaloniaFact]
    public async Task An_agent_close_in_a_background_split_moves_nothing()
    {
        MainWindow window = NewShownWindow();
        var tabs = TabsOf(window);
        var a = (TabItem)tabs.Items[0]!;
        TabItem splitTab = AddSplitTab(window, out TerminalPane b1, out TerminalPane b2);
        Select(window, a);
        var userPane = CurrentPane(window);
        Assert.Same(a.Content, userPane);
        window.EnterFocusMode();

        bool closed = await ((IAgentActionExecutor)window).ClosePaneAsync(b1.PaneId);
        Dispatcher.UIThread.RunJobs();

        Assert.True(closed);
        Assert.Same(a, tabs.SelectedItem);
        Assert.Same(userPane, CurrentPane(window));
        Assert.True(window.IsFocusMode);
        Assert.Same(b2, splitTab.Content);
    }

    [AvaloniaFact]
    public async Task An_agent_close_of_a_sibling_in_the_selected_split_keeps_the_users_pane()
    {
        MainWindow window = NewShownWindow();
        TabItem split = AddSplitTab(window, out TerminalPane x, out TerminalPane y);
        Select(window, split);
        Invoke(window, "UpdateActivePane", y);
        window.KeyPress(Key.D, CtrlShift, PhysicalKey.D, null);
        Dispatcher.UIThread.RunJobs();
        var z = CurrentPane(window)!;
        Assert.NotSame(x, z);
        Assert.NotSame(y, z);
        window.EnterFocusMode();

        bool closed = await ((IAgentActionExecutor)window).ClosePaneAsync(x.PaneId);
        Dispatcher.UIThread.RunJobs();

        Assert.True(closed);
        Assert.Same(z, CurrentPane(window));
        var focused = TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement();
        Assert.False(focused is Avalonia.Visual v && Avalonia.VisualTree.VisualExtensions.FindAncestorOfType<TerminalPane>(v, true) == y,
            "keyboard focus moved into the sibling pane");
        // The tab survives, so nothing forces the user out of focus mode.
        Assert.True(window.IsFocusMode);
    }

    // Final review C1 / R13: a closed pane takes its agent registration with it, so the strip light keeps its own mark of
    // the close; nothing else on screen in focus mode would say a background tab went.

    [AvaloniaFact]
    public async Task An_agent_close_in_focus_mode_lights_the_strip_until_focus_mode_is_left()
    {
        MainWindow window = NewShownWindow();
        var (a, b) = TwoTabs(window);
        string titleOfB = window.GetTabHeaderText(b);
        var paneOfB = (TerminalPane)b.Content!;
        window.EnterFocusMode();
        Assert.Empty(window.CollectUnseenWriteMarks());

        bool closed = await ((IAgentActionExecutor)window).ClosePaneAsync(paneOfB.PaneId);
        Dispatcher.UIThread.RunJobs();

        Assert.True(closed);
        Assert.Same(a, TabsOf(window).SelectedItem);
        Assert.True(window.IsFocusMode);
        var mark = Assert.Single(window.CollectUnseenWriteMarks());
        Assert.True(mark.Closed);
        Assert.Equal(titleOfB, mark.TabTitle);
        Assert.Null(mark.PaneTitle);
        Assert.NotNull(mark.LastWriteUtc);

        // Leaving focus mode brings back the tab strip, which shows the tab is gone: that is the acknowledgement.
        window.LeaveFocusMode();
        Assert.Empty(window.CollectUnseenWriteMarks());
        window.EnterFocusMode();
        Assert.Empty(window.CollectUnseenWriteMarks());
    }

    [AvaloniaFact]
    public async Task An_agent_close_in_a_background_split_names_the_tab_and_pane_on_the_strip()
    {
        MainWindow window = NewShownWindow();
        var a = (TabItem)TabsOf(window).Items[0]!;
        TabItem splitTab = AddSplitTab(window, out TerminalPane b1, out _, userTitle: "Builds");
        Select(window, a);
        string titleOfSplit = window.GetTabHeaderText(splitTab);
        string titleOfB1 = b1.GetBaseTabTitle();
        window.EnterFocusMode();

        bool closed = await ((IAgentActionExecutor)window).ClosePaneAsync(b1.PaneId);
        Dispatcher.UIThread.RunJobs();

        Assert.True(closed);
        var mark = Assert.Single(window.CollectUnseenWriteMarks());
        Assert.True(mark.Closed);
        Assert.Equal(titleOfSplit, mark.TabTitle);
        Assert.Equal(titleOfB1, mark.PaneTitle);
    }

    [AvaloniaFact]
    public async Task An_agent_closing_the_last_pane_of_the_selected_tab_leaves_focus_mode()
    {
        MainWindow window = NewShownWindow();
        var (a, b) = TwoTabs(window);
        var paneOfA = (TerminalPane)a.Content!;
        window.EnterFocusMode();

        bool closed = await ((IAgentActionExecutor)window).ClosePaneAsync(paneOfA.PaneId);
        Dispatcher.UIThread.RunJobs();

        Assert.True(closed);
        // The user's tab went and the selection had to move to another shell: the tab strip comes back to show it.
        Assert.False(window.IsFocusMode);
        Assert.Same(b, TabsOf(window).SelectedItem);
    }

    [AvaloniaFact]
    public async Task An_agent_close_outside_focus_mode_is_unchanged_and_leaves_no_mark()
    {
        MainWindow window = NewShownWindow();
        var (a, b) = TwoTabs(window);
        var paneOfB = (TerminalPane)b.Content!;

        bool closed = await ((IAgentActionExecutor)window).ClosePaneAsync(paneOfB.PaneId);
        Dispatcher.UIThread.RunJobs();

        Assert.True(closed);
        Assert.DoesNotContain(b, TabsOf(window).Items.Cast<TabItem>());
        Assert.Same(a, TabsOf(window).SelectedItem);
        Assert.False(window.IsFocusMode);
        Assert.Empty(window.CollectUnseenWriteMarks());
        // No close mark was kept for a later focus mode either.
        window.EnterFocusMode();
        Assert.Empty(window.CollectUnseenWriteMarks());
    }

    [AvaloniaFact]
    public async Task Agent_actions_outside_focus_mode_are_unchanged()
    {
        MainWindow window = NewShownWindow();
        var tabs = TabsOf(window);

        var (result, error) = await ((IAgentActionExecutor)window).SpawnAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(error);
        var spawnedTab = tabs.Items.Cast<TabItem>().Single(t => t.Content is TerminalPane p && p.PaneId == result!.Value.PaneId);
        Assert.Same(spawnedTab, tabs.SelectedItem);
        Assert.Same(spawnedTab.Content, CurrentPane(window));
    }

    // PR #595 review: a command leaves focus mode only when it does go to another tab.

    private static void Protect(MainWindow window, TabItem tab)
    {
        object state = Invoke(window, "GetOrCreateTabState", tab)!;
        state.GetType().GetProperty("IsProtected")!.SetValue(state, true);
    }

    [AvaloniaFact]
    public void Next_tab_with_no_other_tab_stays_in_focus_mode()
    {
        MainWindow window = NewShownWindow();
        Assert.Single(TabsOf(window).Items);
        window.EnterFocusMode();

        window.KeyPress(Key.Tab, RawInputModifiers.Control, PhysicalKey.Tab, null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.IsFocusMode);
    }

    [AvaloniaFact]
    public async Task Closing_the_selected_tab_leaves_focus_mode()
    {
        MainWindow window = NewShownWindow();
        var (a, b) = TwoTabs(window);
        ((TerminalSettings)typeof(MainWindow).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).PaneClosePolicy = "Force";
        window.EnterFocusMode();

        await (Task)Invoke(window, "CloseSelectedTabAsync")!;
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.IsFocusMode);
        Assert.DoesNotContain(a, TabsOf(window).Items.Cast<TabItem>());
        Assert.Same(b, TabsOf(window).SelectedItem);
    }

    [AvaloniaFact]
    public async Task Closing_a_protected_tab_stays_in_focus_mode()
    {
        MainWindow window = NewShownWindow();
        var (a, _) = TwoTabs(window);
        Protect(window, a);
        window.EnterFocusMode();

        await (Task)Invoke(window, "CloseSelectedTabAsync")!;
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.IsFocusMode);
        Assert.Same(a, TabsOf(window).SelectedItem);
    }

    [AvaloniaFact]
    public async Task Closing_the_only_pane_of_a_protected_tab_stays_in_focus_mode()
    {
        MainWindow window = NewShownWindow();
        var (a, _) = TwoTabs(window);
        Protect(window, a);
        window.EnterFocusMode();

        await (Task)Invoke(window, "CloseActivePaneAsync", true)!;
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.IsFocusMode);
        Assert.Same(a, TabsOf(window).SelectedItem);
        Assert.NotNull(a.Content);
    }

    [AvaloniaFact]
    public void Detaching_a_pane_that_cannot_be_detached_stays_in_focus_mode()
    {
        // These panes are plain shells, not persistent ones: the detach is refused with a notice.
        MainWindow window = NewShownWindow();
        var tab = (TabItem)TabsOf(window).SelectedItem!;
        window.EnterFocusMode();

        Invoke(window, "DetachActivePane");
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.IsFocusMode);
        Assert.IsType<TerminalPane>(tab.Content);
    }

    // An agent can only close the zoomed pane of a zoomed split: one the zoom hides has no tab ancestor, and the close
    // refuses it (#311 finding C), so it moves nothing.
    [AvaloniaFact]
    public async Task An_agent_close_of_the_zoomed_pane_in_a_background_split_keeps_the_users_pane()
    {
        MainWindow window = NewShownWindow();
        var userTab = (TabItem)TabsOf(window).Items[0]!;
        TabItem split = AddSplitTab(window, out TerminalPane zoomed, out TerminalPane other);
        Select(window, split);
        Assert.True((bool)Invoke(window, "EnterPaneZoom", split, zoomed, true)!);
        Select(window, userTab);
        var userPane = CurrentPane(window)!;
        Assert.Same(userTab.Content, userPane);
        window.EnterFocusMode();

        bool closed = await ((IAgentActionExecutor)window).ClosePaneAsync(zoomed.PaneId);
        Dispatcher.UIThread.RunJobs();

        Assert.True(closed);
        Assert.Same(userTab, TabsOf(window).SelectedItem);
        Assert.Same(userPane, CurrentPane(window));
        var focused = TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement();
        Assert.False(focused is Avalonia.Visual v && Avalonia.VisualTree.VisualExtensions.FindAncestorOfType<TerminalPane>(v, true) == other,
            "keyboard focus moved into the background split");
        Assert.Same(other, split.Content);
        Assert.True(window.IsFocusMode);
    }
}
