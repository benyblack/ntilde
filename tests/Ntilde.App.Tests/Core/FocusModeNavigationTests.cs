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
}
