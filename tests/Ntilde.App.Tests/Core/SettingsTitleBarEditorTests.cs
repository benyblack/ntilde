using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Ntilde.Settings;
using Ntilde.Settings.Pages;
using Ntilde.Shell.TitleBar;

namespace Ntilde.Tests.Core;

/// <summary>The grouped title-bar editor and its mini bar (spec §6).</summary>
public sealed class SettingsTitleBarEditorTests
{
    private static (SettingsTestRoot Root, Ntilde.SettingsWindow Window, TabsAndTitleBarPage Page) Open(string settingsJson = "{}")
    {
        var root = SettingsTestRoot.WithSettings(settingsJson);
        var window = new Ntilde.SettingsWindow(new SettingsTarget(SettingsPage.TabsAndTitleBar));
        return (root, window, window.GetPage<TabsAndTitleBarPage>());
    }

    private static StackPanel Rows(TabsAndTitleBarPage page) => page.FindControl<StackPanel>("TitleBarItemsPanel")!;

    internal static Border Row(TabsAndTitleBarPage page, string id)
        => Rows(page).Children.OfType<Border>().Single(row => (string?)row.Tag == id);

    private static ToggleButton Segment(Border row, TitleBarItemState state)
        => row.GetLogicalDescendants().OfType<ToggleButton>().Single(toggle => Equals(toggle.Tag, state));

    private static void Click(Control control) => control.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    internal static string GroupOf(TabsAndTitleBarPage page, string id)
    {
        string? group = null;
        foreach (Control child in Rows(page).Children)
        {
            if (child is Grid header)
            {
                group = (string?)header.Tag;
            }
            else if (child is Border row && (string?)row.Tag == id)
            {
                return group!;
            }
        }

        throw new Xunit.Sdk.XunitException($"No row for {id}.");
    }

    /// <summary>Eight pinned: the four defaults plus four that default to the menu.</summary>
    private const string FullBar = """{"TitleBarItems":{"toggle_recording":"Pinned","command_palette":"Pinned","find":"Pinned","split_vertical":"Pinned"}}""";

    /// <summary>Ten pinned, as a hand-edited settings.json can hold. The layout keeps them all, so no saved icon is lost.</summary>
    private const string OverFullBar = """{"TitleBarItems":{"toggle_recording":"Pinned","command_palette":"Pinned","find":"Pinned","split_vertical":"Pinned","split_horizontal":"Pinned","sftp_remote_files":"Pinned"}}""";

    private static TextBlock OverLimitMessage(TabsAndTitleBarPage page) => page.FindControl<TextBlock>("TitleBarValidationMessage")!;

    /// <summary>The header reads "10 of 8"; the message says what that means and what to do about it.</summary>
    [AvaloniaFact]
    public void ABarOverTheLimit_SaysSo_AndHowManyToMove()
    {
        var (root, _, page) = Open(OverFullBar);
        using (root)
        {
            Assert.True(OverLimitMessage(page).IsVisible);
            Assert.Equal("The title bar holds up to 8 actions, and 10 are in it. Move 2 to the menu or hide them.", OverLimitMessage(page).Text);
        }
    }

    [AvaloniaFact]
    public void MovingActionsOutOfABarOverTheLimit_UpdatesTheMessage_AndHidesItAtTheLimit()
    {
        var (root, _, page) = Open(OverFullBar);
        using (root)
        {
            Click(Segment(Row(page, "find"), TitleBarItemState.Overflow));

            Assert.True(OverLimitMessage(page).IsVisible);
            Assert.Equal("The title bar holds up to 8 actions, and 9 are in it. Move 1 to the menu or hide it.", OverLimitMessage(page).Text);

            Click(Segment(Row(page, "split_horizontal"), TitleBarItemState.Hidden));

            Assert.False(OverLimitMessage(page).IsVisible);
        }
    }

    /// <summary>Reordering leaves as many actions in the bar, so the message stays.</summary>
    [AvaloniaFact]
    public void ReorderingABarOverTheLimit_KeepsTheMessage()
    {
        var (root, _, page) = Open(OverFullBar);
        using (root)
        {
            Click(Row(page, "settings").GetLogicalDescendants().OfType<Button>().Single(b => b.Tag as string == "up"));

            Assert.True(OverLimitMessage(page).IsVisible);
        }
    }

    /// <summary>Restore and import reload the page; the message follows the reloaded bar.</summary>
    [AvaloniaFact]
    public void AReload_ToABarWithinTheLimit_HidesTheMessage()
    {
        var (root, _, page) = Open(OverFullBar);
        using (root)
        {
            Assert.True(OverLimitMessage(page).IsVisible);

            page.Load(SettingsPageTests.SessionFor(new Ntilde.Shell.TerminalSettings()));

            Assert.False(OverLimitMessage(page).IsVisible);
        }
    }

    [AvaloniaFact]
    public void ABarAtTheLimit_ShowsNoMessage()
    {
        var (root, _, page) = Open(FullBar);
        using (root)
        {
            Assert.False(OverLimitMessage(page).IsVisible);
        }
    }

    [AvaloniaFact]
    public void ChoosingAPlacement_MovesTheRowToThatGroup()
    {
        var (root, window, page) = Open();
        using (root)
        {
            Click(Segment(Row(page, "settings"), TitleBarItemState.Overflow));

            Assert.Equal("IN THE … MENU", GroupOf(page, "settings"));
            Assert.Equal(TitleBarItemState.Overflow, window.Session.TitleBarDraft.GetState("settings"));
            Assert.True(Segment(Row(page, "settings"), TitleBarItemState.Overflow).IsChecked);
        }
    }

    [AvaloniaFact]
    public void AFullBar_DisablesBarOnUnpinnedRows_AndSaysWhy()
    {
        var (root, _, page) = Open(FullBar);
        using (root)
        {
            ToggleButton bar = Segment(Row(page, "split_horizontal"), TitleBarItemState.Pinned);

            Assert.False(bar.IsEnabled);
            Assert.Equal(TabsAndTitleBarPage.BarFullTooltip, ToolTip.GetTip(bar));
            Assert.Equal(TabsAndTitleBarPage.BarFullTooltip, AutomationProperties.GetHelpText(bar));
            Assert.True(ToolTip.GetShowOnDisabled(bar));
            Assert.True(Segment(Row(page, "find"), TitleBarItemState.Pinned).IsEnabled); // already pinned
            Assert.Contains(
                Rows(page).Children.OfType<Grid>().Single(g => (string?)g.Tag == "IN THE BAR").Children.OfType<TextBlock>(),
                text => text.Text == "8 of 8");
        }
    }

    [AvaloniaFact]
    public void UnpinningFromAFullBar_ReenablesBar()
    {
        var (root, _, page) = Open(FullBar);
        using (root)
        {
            Assert.False(Segment(Row(page, "split_horizontal"), TitleBarItemState.Pinned).IsEnabled);

            Click(Segment(Row(page, "find"), TitleBarItemState.Overflow));

            ToggleButton bar = Segment(Row(page, "split_horizontal"), TitleBarItemState.Pinned);
            Assert.True(bar.IsEnabled);
            Assert.Null(ToolTip.GetTip(bar));
            Assert.True(string.IsNullOrEmpty(AutomationProperties.GetHelpText(bar)));
        }
    }

    private static object? Focused(Ntilde.SettingsWindow window) => window.FocusManager!.GetFocusedElement();

    [AvaloniaFact]
    public void PlacementByKeyboard_KeepsFocusOnTheMovedRow()
    {
        var (root, window, page) = Open();
        using (root)
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Segment(Row(page, "settings"), TitleBarItemState.Overflow).Focus(NavigationMethod.Tab);

            Click(Segment(Row(page, "settings"), TitleBarItemState.Overflow));
            Dispatcher.UIThread.RunJobs();

            Assert.Same(Segment(Row(page, "settings"), TitleBarItemState.Overflow), Focused(window));
        }
    }

    /// <summary>
    /// A placement change restores focus only when it was in the rows: focus elsewhere on the page stays put (nothing
    /// focused takes the same path - CaptureRowFocus finds no row either way).
    /// </summary>
    [AvaloniaFact]
    public void APlacementChange_LeavesFocusOutsideTheRowsAlone()
    {
        var (root, window, page) = Open();
        using (root)
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var orientation = page.FindControl<ComboBox>("TabOrientationList")!;
            orientation.Focus(NavigationMethod.Tab);
            Dispatcher.UIThread.RunJobs();

            Click(Segment(Row(page, "settings"), TitleBarItemState.Overflow));
            Dispatcher.UIThread.RunJobs();

            Assert.Same(orientation, Focused(window));
        }
    }

    /// <summary>The mini bar's fixed parts: two sample tabs, the first active, then "+" leading the actions (#528).</summary>
    [AvaloniaFact]
    public void TheMiniBar_ShowsTwoSampleTabs_ThenNewTabFirstInTheActions()
    {
        var (root, _, page) = Open();
        using (root)
        {
            var grid = (Grid)page.FindControl<Border>("TitleBarPreview")!.Child!;
            var tabs = (StackPanel)grid.Children[0];
            StackPanel actions = grid.Children.OfType<StackPanel>().Single(panel => (string?)panel.Tag == "actions");

            Assert.Equal(new[] { "~/projects", "pwsh" }, tabs.Children.OfType<Border>().Select(tab => ((TextBlock)tab.Child!).Text));
            Assert.Equal(TitleBarCatalog.NewTabId, actions.Children[0].Tag);
        }
    }

    private static Point CenterOf(Control control, Window window)
        => control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    private static void ClickWithThePointer(Window window, Point at)
    {
        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static double TopOf(Border row, Window window) => row.TranslatePoint(default, window)!.Value.Y;

    private static string? HeaderDetail(TabsAndTitleBarPage page, string group)
        => Rows(page).Children.OfType<Grid>().Single(g => (string?)g.Tag == group).Children.OfType<TextBlock>().Last().Text;

    /// <summary>Scrolls the row on screen: at the default size the menu rows start below the page's viewport.</summary>
    private static void ScrollToRow(TabsAndTitleBarPage page, string id)
    {
        Row(page, id).BringIntoView();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Pins Record Session, a menu row, by clicking its Bar segment with the pointer.</summary>
    private static void PinRecordingWithThePointer(Ntilde.SettingsWindow window, TabsAndTitleBarPage page)
    {
        ScrollToRow(page, "toggle_recording");
        ClickWithThePointer(window, CenterOf(Segment(Row(page, "toggle_recording"), TitleBarItemState.Pinned), window));
        Assert.Equal(TitleBarItemState.Pinned, window.Session.TitleBarDraft.GetState("toggle_recording")); // the click landed
    }

    /// <summary>
    /// A placement chosen with the pointer leaves the row where it is, so the pointer stays over the row it
    /// clicked. The switch, the header count and the preview follow the draft at once.
    /// </summary>
    [AvaloniaFact]
    public void ChoosingAPlacementWithThePointer_LeavesTheRowUnderIt()
    {
        var (root, window, page) = Open();
        using (root)
        {
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                ScrollToRow(page, "toggle_recording");
                double top = TopOf(Row(page, "toggle_recording"), window);

                ClickWithThePointer(window, CenterOf(Segment(Row(page, "toggle_recording"), TitleBarItemState.Pinned), window));

                Assert.Equal(TitleBarItemState.Pinned, window.Session.TitleBarDraft.GetState("toggle_recording"));
                Assert.True(Segment(Row(page, "toggle_recording"), TitleBarItemState.Pinned).IsChecked);
                Assert.Equal(top, TopOf(Row(page, "toggle_recording"), window), 1);
                Assert.Equal("IN THE … MENU", GroupOf(page, "toggle_recording"));
                Assert.Equal("5 of 8", HeaderDetail(page, "IN THE BAR"));
            }
            finally
            {
                window.Close();
            }
        }
    }

    /// <summary>
    /// The slip this guards against: Connections moved to Hidden used to drop to the bottom, Settings slid up
    /// into its place, and a second click at the same spot hid Settings.
    /// </summary>
    [AvaloniaFact]
    public void ASecondClickAtTheSameSpot_HitsTheSameRow()
    {
        var (root, window, page) = Open();
        using (root)
        {
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                Point hide = CenterOf(Segment(Row(page, "connections"), TitleBarItemState.Hidden), window);

                ClickWithThePointer(window, hide);
                ClickWithThePointer(window, hide);

                Assert.Equal(TitleBarItemState.Hidden, window.Session.TitleBarDraft.GetState("connections"));
                Assert.Equal(TitleBarItemState.Pinned, window.Session.TitleBarDraft.GetState("settings"));
            }
            finally
            {
                window.Close();
            }
        }
    }

    /// <summary>
    /// Hide, then Bar again, on a row that never moved on screen puts the action back where it was in the bar
    /// order, not at its end: the arrows act on that order, so they must match what is shown.
    /// </summary>
    [AvaloniaFact]
    public void PinningAHeldBarRowAgain_PutsItBackInItsPlace()
    {
        var (root, window, page) = Open();
        using (root)
        {
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                List<string> order = window.Session.TitleBarDraft.BuildSaveOrder();
                int at = order.IndexOf("connections");
                Assert.True(at >= 2 && at < order.Count - 1, "precondition: Connections has a row above it to swap with, and one below");

                ClickWithThePointer(window, CenterOf(Segment(Row(page, "connections"), TitleBarItemState.Hidden), window));
                ClickWithThePointer(window, CenterOf(Segment(Row(page, "connections"), TitleBarItemState.Pinned), window));

                Assert.Equal(order, window.Session.TitleBarDraft.BuildSaveOrder());

                // ▲ swaps it with the row shown above it.
                Click(Row(page, "connections").GetLogicalDescendants().OfType<Button>().Single(b => b.Tag as string == "up"));
                (order[at - 1], order[at]) = (order[at], order[at - 1]);
                Assert.Equal(order, window.Session.TitleBarDraft.BuildSaveOrder());
            }
            finally
            {
                window.Close();
            }
        }
    }

    [AvaloniaFact]
    public void MovingThePointerOffTheList_MovesTheRowToItsGroup()
    {
        var (root, window, page) = Open();
        using (root)
        {
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                PinRecordingWithThePointer(window, page);
                Assert.Equal("IN THE … MENU", GroupOf(page, "toggle_recording"));

                window.MouseMove(new Point(5, 5)); // the window's top-left corner, well away from the rows
                Dispatcher.UIThread.RunJobs();

                Assert.Equal("IN THE BAR", GroupOf(page, "toggle_recording"));
            }
            finally
            {
                window.Close();
            }
        }
    }

    /// <summary>Restore and import reload the page, which rebuilds the rows from the draft.</summary>
    [AvaloniaFact]
    public void AReload_MovesAHeldRowToItsGroup()
    {
        var (root, window, page) = Open();
        using (root)
        {
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                PinRecordingWithThePointer(window, page);
                Assert.Equal("IN THE … MENU", GroupOf(page, "toggle_recording"));

                page.Load(window.Session);

                Assert.Equal("IN THE BAR", GroupOf(page, "toggle_recording"));
            }
            finally
            {
                window.Close();
            }
        }
    }

    /// <summary>A reorder shows the bar's new order, so it lets a held row go to its group first.</summary>
    [AvaloniaFact]
    public void AReorder_MovesAHeldRowToItsGroup()
    {
        var (root, window, page) = Open();
        using (root)
        {
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                PinRecordingWithThePointer(window, page);
                Assert.Equal("IN THE … MENU", GroupOf(page, "toggle_recording"));

                Click(Row(page, "settings").GetLogicalDescendants().OfType<Button>().Single(b => b.Tag as string == "up"));

                Assert.Equal("IN THE BAR", GroupOf(page, "toggle_recording"));
            }
            finally
            {
                window.Close();
            }
        }
    }

    [AvaloniaFact]
    public void MoveUpByKeyboard_KeepsFocusOnTheSameAction()
    {
        var (root, window, page) = Open();
        using (root)
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            static Button Up(Border row) => row.GetLogicalDescendants().OfType<Button>().Single(b => b.Tag as string == "up");
            // Settings is the last pinned action; press up twice and the same action must move twice.
            List<string> order = window.Session.TitleBarDraft.BuildSaveOrder();
            int at = order.IndexOf("settings");
            Assert.True(at >= 3, "precondition: settings has room to move up twice");

            Up(Row(page, "settings")).Focus(NavigationMethod.Tab);
            Click(Up(Row(page, "settings")));
            Dispatcher.UIThread.RunJobs();
            Assert.Same(Up(Row(page, "settings")), Focused(window));

            Click(Up(Row(page, "settings")));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(at - 2, window.Session.TitleBarDraft.BuildSaveOrder().IndexOf("settings"));
        }
    }

    [AvaloniaFact]
    public void EveryRowsShortcut_StartsAtTheSameX()
    {
        var (root, window, page) = Open();
        using (root)
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            List<double> xs = Rows(page).Children.OfType<Border>()
                .Select(row => row.GetLogicalDescendants().OfType<TextBlock>().Single(t => (string?)t.Tag == "shortcut"))
                .Select(shortcut => shortcut.TranslatePoint(new Point(0, 0), Rows(page))!.Value.X)
                .ToList();

            Assert.True(xs.Count > 5);
            Assert.All(xs, x => Assert.Equal(xs[0], x, 0.5));
        }
    }

    [AvaloniaFact]
    public void ActionTitles_TrimAndCarryTheFullTitleAsTooltip()
    {
        var (root, _, page) = Open();
        using (root)
        {
            TextBlock title = Row(page, "settings").GetLogicalDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("RowLabel"));

            Assert.Equal(Avalonia.Media.TextTrimming.CharacterEllipsis, title.TextTrimming);
            Assert.Equal(title.Text, ToolTip.GetTip(title));
        }
    }

    [AvaloniaFact]
    public void ArrowsOnlyInTheBarGroup_AndNewTabStaysLocked()
    {
        var (root, _, page) = Open();
        using (root)
        {
            Assert.Contains(Row(page, "settings").GetLogicalDescendants().OfType<Button>(), b => b.Tag as string == "up");
            Assert.DoesNotContain(Row(page, "find").GetLogicalDescendants().OfType<Button>(), b => b.Tag as string is "up" or "down");

            Border newTab = Row(page, TitleBarCatalog.NewTabId);
            Assert.Empty(newTab.GetLogicalDescendants().OfType<ToggleButton>());
            Assert.Contains(newTab.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "Always in the bar");
        }
    }

    [AvaloniaFact]
    public void MovingUp_ReordersThePinnedActions()
    {
        var (root, window, page) = Open();
        using (root)
        {
            List<string> before = window.Session.TitleBarDraft.BuildSaveOrder();
            int settingsAt = before.IndexOf("settings");

            Click(Row(page, "settings").GetLogicalDescendants().OfType<Button>().Single(b => b.Tag as string == "up"));

            List<string> after = window.Session.TitleBarDraft.BuildSaveOrder();
            Assert.Equal(settingsAt - 1, after.IndexOf("settings"));
        }
    }

    private static List<string> MiniBarActions(TabsAndTitleBarPage page)
        => ((Grid)page.FindControl<Border>("TitleBarPreview")!.Child!).Children
            .OfType<StackPanel>().Single(panel => (string?)panel.Tag == "actions")
            .Children.Where(child => (string?)child.Tag != "new_tab").Select(child => (string)child.Tag!).ToList();

    [AvaloniaFact]
    public void TheMiniBar_ShowsThePinnedActionsInOrder_ThenTheMenuButton()
    {
        var (root, window, page) = Open();
        using (root)
        {
            List<string> expected = window.Session.TitleBarDraft.BuildSaveOrder()
                .Where(id => id != TitleBarCatalog.NewTabId)
                .Append("overflow")
                .ToList();

            Assert.Equal(expected, MiniBarActions(page));
        }
    }

    /// <summary>
    /// An untagged control cannot be found again by its Tag - a null Tag would match any untagged control - so focus falls
    /// through to the other choices; a tagged one is still found first (#528).
    /// </summary>
    [AvaloniaFact]
    public void FocusRestore_ByANullTag_FallsThroughToThePlacement()
    {
        var untagged = new Button();
        var down = new Button { Tag = "down" };
        var placement = new ToggleButton { Tag = TitleBarItemState.Overflow, IsChecked = true };

        Assert.Same(placement, TabsAndTitleBarPage.FocusTargetIn([untagged, placement], capturedTag: null));
        Assert.Same(down, TabsAndTitleBarPage.FocusTargetIn([untagged, down, placement], capturedTag: "down"));
    }

    [AvaloniaFact]
    public void TheMiniBar_FollowsEveryDraftChange()
    {
        var (root, _, page) = Open();
        using (root)
        {
            Click(Segment(Row(page, "find"), TitleBarItemState.Pinned));
            Assert.Equal("find", MiniBarActions(page)[^2]); // last pinned, before "…"

            foreach (TitleBarCatalogEntry entry in TitleBarCatalog.GetEntries().Where(e => !e.IsLocked).ToList())
            {
                if (GroupOf(page, entry.Id) == "IN THE … MENU")
                {
                    Click(Segment(Row(page, entry.Id), TitleBarItemState.Hidden));
                }
            }

            Assert.DoesNotContain("overflow", MiniBarActions(page));
        }
    }

    [AvaloniaFact]
    public void TheMiniBar_ReordersWhenAnActionMovesUp()
    {
        var (root, _, page) = Open();
        using (root)
        {
            Assert.Equal(["open_tab_list", "connections", "settings", "overflow"], MiniBarActions(page));

            Click(Row(page, "settings").GetLogicalDescendants().OfType<Button>().Single(b => b.Tag as string == "up"));

            Assert.Equal(["open_tab_list", "settings", "connections", "overflow"], MiniBarActions(page));
        }
    }

    /// <summary>
    /// What a screen reader is given is the preview's automation peer, not the properties set on it: a bare Border's
    /// peer has no role of its own, so the preview was read with a role of "none".
    /// </summary>
    [AvaloniaFact]
    public void TheMiniBar_IsOneNamedImageToScreenReaders()
    {
        var (root, _, page) = Open();
        using (root)
        {
            AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(page.FindControl<Border>("TitleBarPreview")!);

            Assert.Equal("Title bar preview", peer.GetName());
            Assert.Equal(AutomationControlType.Image, peer.GetAutomationControlType());
            Assert.True(peer.IsControlElement(), "a screen reader would not reach the preview");
            Assert.Empty(ControlElementsUnder(peer)); // its glyphs stay out of what a screen reader navigates
        }
    }

    private static List<string> ControlElementsUnder(AutomationPeer peer)
    {
        var found = new List<string>();
        foreach (AutomationPeer child in peer.GetChildren())
        {
            if (child.IsControlElement()) found.Add($"{child.GetAutomationControlType()} '{child.GetName()}'");
            found.AddRange(ControlElementsUnder(child));
        }

        return found;
    }

    [AvaloniaFact]
    public void AnUnboundAction_SaysNoShortcut()
    {
        var (root, _, page) = Open();
        using (root)
        {
            TextBlock shortcut = Row(page, "agent_activity").GetLogicalDescendants().OfType<TextBlock>()
                .Single(text => (string?)text.Tag == "shortcut");

            Assert.Equal("No shortcut", shortcut.Text);
        }
    }
}
