using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Ntilde.Shell.TitleBar;
using Ntilde.Shell;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Ntilde.Settings.Pages;

public partial class TabsAndTitleBarPage : UserControl, ISettingsPage
{
    private const string RowDescStyleClass = "RowDesc";

    private SettingsSession? _session;

    public TabsAndTitleBarPage()
    {
        InitializeComponent();
    }

    public SettingsPage Page => SettingsPage.TabsAndTitleBar;

    public Control View => this;

    private SettingsSession Session => _session ?? throw new InvalidOperationException("TabsAndTitleBarPage used before Load.");

    public void Load(SettingsSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!ReferenceEquals(_session, session))
        {
            if (_session != null)
            {
                _session.ShortcutsChanged -= RebuildTitleBarEditor;
            }

            session.ShortcutsChanged += RebuildTitleBarEditor;
        }

        _session = session;
        ComboTags.Select(this.FindControl<ComboBox>("TabOrientationList")!, session.Settings.TabStripOrientation, StringComparison.OrdinalIgnoreCase, fallbackIndex: 0);
        RebuildTitleBarEditor();
    }

    public SettingsValidationError? Validate() => null;

    public void Collect(TerminalSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (ComboTags.SelectedValue(this.FindControl<ComboBox>("TabOrientationList")!) is { } orientation)
        {
            target.TabStripOrientation = orientation;
        }

        // The title-bar placement is committed from the session's draft (SettingsSession.CommitDrafts).
    }

    // ── Title bar actions (spec §6) ───────────────────────────────────────────

    /// <summary>Tooltip on a disabled "Bar" button once the bar is full.</summary>
    internal const string BarFullTooltip = "The title bar holds up to 8 actions";

    private static readonly (TitleBarItemState State, string Label)[] Placements =
    [
        (TitleBarItemState.Pinned, "Bar"),
        (TitleBarItemState.Overflow, "Menu"),
        (TitleBarItemState.Hidden, "Hide"),
    ];

    private void RebuildTitleBarEditor()
    {
        if (_session is null)
        {
            return;
        }

        RebuildMiniTitleBar();
        RebuildTitleBarRows();
    }

    private void RebuildTitleBarRows()
    {
        var panel = this.FindControl<StackPanel>("TitleBarItemsPanel")!;
        panel.Children.Clear();

        TitleBarDraftState draft = Session.TitleBarDraft;
        Dictionary<string, TitleBarCatalogEntry> byId = TitleBarCatalog.GetEntries().ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string> order = draft.GetDisplayOrder();
        List<TitleBarCatalogEntry> InGroup(TitleBarItemState state)
            => order.Where(id => draft.GetState(id) == state).Select(id => byId[id]).ToList();

        List<TitleBarCatalogEntry> bar = InGroup(TitleBarItemState.Pinned);
        List<TitleBarCatalogEntry> menu = InGroup(TitleBarItemState.Overflow);
        List<TitleBarCatalogEntry> hidden = InGroup(TitleBarItemState.Hidden);
        bool barFull = bar.Count >= TitleBarCatalog.MaxPinned;

        panel.Children.Add(GroupHeader("IN THE BAR", $"{bar.Count} of {TitleBarCatalog.MaxPinned}"));
        for (int i = 0; i < bar.Count; i++)
        {
            panel.Children.Add(CreateTitleBarRow(bar[i], TitleBarItemState.Pinned, i, bar.Count, barFull));
        }

        panel.Children.Add(GroupHeader("IN THE … MENU", menu.Count.ToString(CultureInfo.InvariantCulture)));
        foreach (TitleBarCatalogEntry entry in menu)
        {
            panel.Children.Add(CreateTitleBarRow(entry, TitleBarItemState.Overflow, -1, 0, barFull));
        }

        panel.Children.Add(GroupHeader("HIDDEN", $"{hidden.Count} · still reachable by shortcut and the command palette"));
        foreach (TitleBarCatalogEntry entry in hidden)
        {
            panel.Children.Add(CreateTitleBarRow(entry, TitleBarItemState.Hidden, -1, 0, barFull));
        }
    }

    private static Grid GroupHeader(string title, string detail)
    {
        var name = new TextBlock { Text = title, Classes = { "SectionHeader" }, VerticalAlignment = VerticalAlignment.Center };
        var count = new TextBlock { Text = detail, Classes = { RowDescStyleClass }, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(count, 1);
        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 14, 0, 4),
            Tag = title,
            Children = { name, count },
        };
    }

    private Control CreateTitleBarRow(TitleBarCatalogEntry entry, TitleBarItemState state, int barIndex, int barCount, bool barFull)
    {
        var icon = new PathIcon
        {
            Data = Geometry.Parse(entry.IconGeometry),
            Width = entry.IconSize,
            Height = entry.IconSize,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var title = new TextBlock { Text = entry.Title, Classes = { "RowLabel" }, VerticalAlignment = VerticalAlignment.Center };
        var shortcut = new TextBlock
        {
            Text = TitleBarShortcuts.Resolve(entry.ShortcutKey, Session.ShortcutDraft),
            Classes = { RowDescStyleClass },
            VerticalAlignment = VerticalAlignment.Center,
            Tag = "shortcut",
        };

        // Locked: New Tab is the primary action and hosts the flyout with "New SSH Connection…" /
        // "Manage Profiles…" / "Agent Activity…". Letting it be hidden would lose that flyout.
        Control placement = entry.IsLocked
            ? new TextBlock { Text = "Always in the bar", Classes = { RowDescStyleClass }, VerticalAlignment = VerticalAlignment.Center }
            : CreatePlacementSwitch(entry, state, barFull);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("28,*,110,Auto,64"),
            ColumnSpacing = 12,
            Children = { icon, title, shortcut, placement },
        };
        Grid.SetColumn(title, 1);
        Grid.SetColumn(shortcut, 2);
        Grid.SetColumn(placement, 3);

        if (state == TitleBarItemState.Pinned && !entry.IsLocked)
        {
            // Index 0 is the locked New Tab, which never moves and can never be displaced.
            var up = new Button { Content = "▲", Classes = { "IconBtn" }, IsEnabled = barIndex > 1, Tag = "up" };
            var down = new Button { Content = "▼", Classes = { "IconBtn" }, IsEnabled = barIndex < barCount - 1, Tag = "down" };
            AutomationProperties.SetName(up, $"Move {entry.Title} left");
            AutomationProperties.SetName(down, $"Move {entry.Title} right");
            up.Click += (_, _) => MoveDraftPinned(entry.Id, -1);
            down.Click += (_, _) => MoveDraftPinned(entry.Id, +1);
            var arrows = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, Children = { up, down } };
            Grid.SetColumn(arrows, 4);
            grid.Children.Add(arrows);
        }

        Border row = SettingsRowChrome.CreateRow(this, grid);
        row.Tag = entry.Id;
        return row;
    }

    private StackPanel CreatePlacementSwitch(TitleBarCatalogEntry entry, TitleBarItemState current, bool barFull)
    {
        var host = new StackPanel { Orientation = Orientation.Horizontal, Classes = { "Segmented" }, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (state, label) in Placements)
        {
            var button = new ToggleButton { Content = label, Tag = state, IsChecked = state == current, Classes = { "Segment" } };
            AutomationProperties.SetName(button, $"{entry.Title}: {label}");
            if (state == TitleBarItemState.Pinned && current != TitleBarItemState.Pinned && barFull)
            {
                button.IsEnabled = false;
                ToolTip.SetTip(button, BarFullTooltip);
                ToolTip.SetShowOnDisabled(button, true);
            }

            button.Click += (_, _) => ChooseTitleBarPlacement(entry.Id, state);
            host.Children.Add(button);
        }

        return host;
    }

    private void ChooseTitleBarPlacement(string id, TitleBarItemState next)
    {
        // Clicking the current choice un-checks its ToggleButton; the rebuild below puts it back.
        if (Session.TitleBarDraft.GetState(id) != next && !Session.TitleBarDraft.TrySetState(id, next))
        {
            // "Bar" is disabled once the bar is full, so this is the draft's own cap firing.
            ShowTitleBarValidationMessage($"At most {TitleBarCatalog.MaxPinned} actions fit in the title bar. Move one to the menu or hide it first.");
        }
        else
        {
            ClearTitleBarValidationMessage();
        }

        RebuildTitleBarEditor();
    }

    private void MoveDraftPinned(string id, int delta)
    {
        Session.TitleBarDraft.MovePinned(id, delta);
        RebuildTitleBarEditor();
    }

    private void ShowTitleBarValidationMessage(string message)
    {
        var label = this.FindControl<TextBlock>("TitleBarValidationMessage")!;
        label.Text = message;
        label.IsVisible = true;
    }

    private void ClearTitleBarValidationMessage() => this.FindControl<TextBlock>("TitleBarValidationMessage")!.IsVisible = false;

    // RebuildMiniTitleBar is Task 4.2's; until then this placeholder keeps the build green:
    private void RebuildMiniTitleBar()
    {
    }
}
