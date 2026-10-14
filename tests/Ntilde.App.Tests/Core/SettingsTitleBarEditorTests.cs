using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia;
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

    [AvaloniaFact]
    public void TheMiniBar_IsDecorative_HostKeepsTheNameAndContentIsHidden()
    {
        var (root, _, page) = Open();
        using (root)
        {
            var host = page.FindControl<Border>("TitleBarPreview")!;
            Assert.Equal("Title bar preview", AutomationProperties.GetName(host));
            Assert.Equal(AccessibilityView.Control, AutomationProperties.GetAccessibilityView(host));

            var grid = (Grid)host.Child!;
            Assert.Equal(AccessibilityView.Raw, AutomationProperties.GetAccessibilityView(grid));
            Assert.All(
                grid.GetLogicalDescendants().OfType<Control>(),
                control => Assert.Equal(AccessibilityView.Raw, AutomationProperties.GetAccessibilityView(control)));
        }
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
