using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
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
            Assert.True(ToolTip.GetShowOnDisabled(bar));
            Assert.True(Segment(Row(page, "find"), TitleBarItemState.Pinned).IsEnabled); // already pinned
            Assert.Contains(
                Rows(page).Children.OfType<Grid>().Single(g => (string?)g.Tag == "IN THE BAR").Children.OfType<TextBlock>(),
                text => text.Text == "8 of 8");
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
}
