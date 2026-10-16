using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.VisualTree;
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

    // The rows as they stood when a placement was chosen with the pointer over them, or null when the rows
    // follow the draft. Regrouping at once moves the clicked row away and slides another under the pointer,
    // so a second click lands on the wrong action. Each row's controls still follow the draft.
    private List<(string Id, TitleBarItemState Group)>? _heldRows;

    // Whether the pointer was over the rows at its last move, press or release, hit-tested at the window (see
    // TrackPointer), and the window whose pointer events feed it.
    private bool _pointerOverRows;
    private TopLevel? _topLevel;

    public TabsAndTitleBarPage()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        _topLevel?.AddHandler(PointerMovedEvent, TrackPointer, RoutingStrategies.Tunnel, handledEventsToo: true);
        _topLevel?.AddHandler(PointerPressedEvent, TrackPointer, RoutingStrategies.Tunnel, handledEventsToo: true);
        _topLevel?.AddHandler(PointerReleasedEvent, TrackPointer, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _topLevel?.RemoveHandler(PointerMovedEvent, TrackPointer);
        _topLevel?.RemoveHandler(PointerPressedEvent, TrackPointer);
        _topLevel?.RemoveHandler(PointerReleasedEvent, TrackPointer);
        _topLevel = null;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// Notes whether the pointer is over the rows, and lets held rows regroup once it is not: then nothing can
    /// land on the wrong one. Judged by hit-testing at the window, not by the rows' own PointerExited: every
    /// placement change rebuilds the control under the pointer, and after that the rows can miss their exit.
    /// </summary>
    private void TrackPointer(object? sender, PointerEventArgs e)
    {
        if (_topLevel is null || !IsVisible)
        {
            return;
        }

        var rows = RowsPanel;
        _pointerOverRows = _topLevel.InputHitTest(e.GetPosition(_topLevel)) is Visual hit
            && (ReferenceEquals(hit, rows) || rows.IsVisualAncestorOf(hit));
        if (!_pointerOverRows)
        {
            ReleaseHeldRows();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // The window hides a page it navigates away from; it comes back with the rows in their groups.
        if (change.Property == IsVisibleProperty && !IsVisible)
        {
            _pointerOverRows = false; // TrackPointer skips a hidden page, so this would go stale
            ReleaseHeldRows();
        }
    }

    public SettingsPage Page => SettingsPage.TabsAndTitleBar;

    public Control View => this;

    private SettingsSession Session => _session ?? throw new InvalidOperationException("TabsAndTitleBarPage used before Load.");

    private StackPanel RowsPanel => this.FindControl<StackPanel>("TitleBarItemsPanel")!;

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
        _heldRows = null; // a reload (restore, import) rebuilds every row from the new draft
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

        (string RowId, object? Tag, bool Keyboard)? focus = CaptureRowFocus();
        RebuildMiniTitleBar();
        RebuildTitleBarRows();
        UpdateOverLimitMessage();
        RestoreRowFocus(focus);
    }

    /// <summary>
    /// A hand-edited settings.json can pin more than MaxPinned. The layout keeps them all, so no saved icon is
    /// lost, and Save keeps them too; but the header alone just reads "10 of 8". While it lasts, say what that
    /// means and how many to move. Recomputed on every rebuild, so a placement change, a reorder or a reload
    /// leaves it describing the draft.
    /// </summary>
    private void UpdateOverLimitMessage()
    {
        var label = this.FindControl<TextBlock>("TitleBarValidationMessage")!;
        int pinned = Session.TitleBarDraft.CountPinned();
        int excess = pinned - TitleBarCatalog.MaxPinned;
        label.IsVisible = excess > 0;
        if (excess > 0)
        {
            label.Text = $"The title bar holds up to {TitleBarCatalog.MaxPinned} actions, and {pinned} are in it. " +
                         $"Move {excess} to the menu or hide {(excess == 1 ? "it" : "them")}.";
        }
    }

    /// <summary>
    /// The rebuild destroys the pressed control, and Avalonia then clears focus, so Tab would restart
    /// from the top of the window. Note which row and control had focus so it can be put back.
    /// </summary>
    private (string RowId, object? Tag, bool Keyboard)? CaptureRowFocus()
    {
        var panel = RowsPanel;
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not Control focused || !panel.IsLogicalAncestorOf(focused))
        {
            return null;
        }

        for (Control? current = focused; current is not null && !ReferenceEquals(current, panel); current = current.GetLogicalParent() as Control)
        {
            if (current is Border { Tag: string rowId } && ReferenceEquals(current.GetLogicalParent(), panel))
            {
                return (rowId, focused.Tag, focused.Classes.Contains(":focus-visible"));
            }
        }

        return null;
    }

    private void RestoreRowFocus((string RowId, object? Tag, bool Keyboard)? focus)
    {
        if (focus is not { } target)
        {
            return;
        }

        var panel = RowsPanel;
        Border? row = panel.Children.OfType<Border>().FirstOrDefault(r => (string?)r.Tag == target.RowId);
        if (row is null)
        {
            return;
        }

        Control? next = FocusTargetIn(row.GetLogicalDescendants().OfType<Control>(), target.Tag);
        next?.Focus(target.Keyboard ? NavigationMethod.Tab : NavigationMethod.Unspecified);
    }

    /// <summary>
    /// Which of a rebuilt row's controls takes focus back: the one with the captured Tag (the row may have changed group,
    /// so it is matched by Tag, not by position), else the other arrow, else whichever placement is now checked. An
    /// untagged control cannot be found again that way - a null Tag matches every untagged control - so it goes straight
    /// to the other choices.
    /// </summary>
    internal static Control? FocusTargetIn(IEnumerable<Control> candidates, object? capturedTag)
    {
        List<Control> focusable = candidates.Where(c => c.Focusable && c.IsEnabled).ToList();
        return (capturedTag is null ? null : focusable.FirstOrDefault(c => Equals(c.Tag, capturedTag)))
            ?? focusable.FirstOrDefault(c => c is Button && c.Tag is "up" or "down")
            ?? focusable.FirstOrDefault(c => c is ToggleButton { IsChecked: true });
    }

    private void RebuildTitleBarRows()
    {
        var panel = RowsPanel;
        panel.Children.Clear();

        TitleBarDraftState draft = Session.TitleBarDraft;
        Dictionary<string, TitleBarCatalogEntry> byId = TitleBarCatalog.GetEntries().ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        List<(string Id, TitleBarItemState Group)> layout = _heldRows ?? DraftRowLayout();
        List<TitleBarCatalogEntry> InGroup(TitleBarItemState group)
            => layout.Where(row => row.Group == group).Select(row => byId[row.Id]).ToList();

        List<TitleBarCatalogEntry> bar = InGroup(TitleBarItemState.Pinned);
        List<TitleBarCatalogEntry> menu = InGroup(TitleBarItemState.Overflow);
        List<TitleBarCatalogEntry> hidden = InGroup(TitleBarItemState.Hidden);

        // The counts, the cap and each row's controls follow the draft, also while the rows are held.
        int CountInDraft(TitleBarItemState state) => byId.Keys.Count(id => draft.GetState(id) == state);
        int pinned = CountInDraft(TitleBarItemState.Pinned);
        bool barFull = pinned >= TitleBarCatalog.MaxPinned;

        // Reordered among themselves: the rows in the bar group that the draft still pins.
        List<string> reorderable = bar.Where(e => draft.GetState(e.Id) == TitleBarItemState.Pinned).Select(e => e.Id).ToList();

        panel.Children.Add(GroupHeader("IN THE BAR", $"{pinned} of {TitleBarCatalog.MaxPinned}"));
        foreach (TitleBarCatalogEntry entry in bar)
        {
            panel.Children.Add(CreateTitleBarRow(entry, draft.GetState(entry.Id), reorderable.IndexOf(entry.Id), reorderable.Count, barFull));
        }

        panel.Children.Add(GroupHeader("IN THE … MENU", CountInDraft(TitleBarItemState.Overflow).ToString(CultureInfo.InvariantCulture)));
        foreach (TitleBarCatalogEntry entry in menu)
        {
            panel.Children.Add(CreateTitleBarRow(entry, draft.GetState(entry.Id), -1, 0, barFull));
        }

        panel.Children.Add(GroupHeader("HIDDEN", $"{CountInDraft(TitleBarItemState.Hidden)} · still reachable by shortcut and the command palette"));
        foreach (TitleBarCatalogEntry entry in hidden)
        {
            panel.Children.Add(CreateTitleBarRow(entry, draft.GetState(entry.Id), -1, 0, barFull));
        }
    }

    /// <summary>Each row in the group the draft gives it: the bar in its order, then the rest in catalog order.</summary>
    private List<(string Id, TitleBarItemState Group)> DraftRowLayout()
    {
        TitleBarDraftState draft = Session.TitleBarDraft;
        return draft.GetDisplayOrder().Select(id => (id, draft.GetState(id))).ToList();
    }

    private void ReleaseHeldRows()
    {
        if (_heldRows is null)
        {
            return;
        }

        _heldRows = null;
        RebuildTitleBarEditor();
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
        var title = new TextBlock
        {
            Text = entry.Title,
            Classes = { "RowLabel" },
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        ToolTip.SetTip(title, entry.Title);
        var shortcut = new TextBlock
        {
            Text = ShortcutText(TitleBarShortcuts.Resolve(entry.ShortcutKey, Session.ShortcutDraft)),
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
            ColumnDefinitions =
            {
                new ColumnDefinition(28, GridUnitType.Pixel),
                new ColumnDefinition(1, GridUnitType.Star),
                new ColumnDefinition(110, GridUnitType.Pixel),
                // Shared across rows (IsSharedSizeScope on the panel) so "Always in the bar" on New Tab
                // and the switch elsewhere give every shortcut the same X.
                new ColumnDefinition(GridLength.Auto) { SharedSizeGroup = "Placement" },
                new ColumnDefinition(64, GridUnitType.Pixel),
            },
            ColumnSpacing = 12,
            Children = { icon, title, shortcut, placement },
        };
        Grid.SetColumn(title, 1);
        Grid.SetColumn(shortcut, 2);
        Grid.SetColumn(placement, 3);

        // barIndex is -1 for a row held outside the bar group: it gets its arrows once it moves there.
        if (state == TitleBarItemState.Pinned && !entry.IsLocked && barIndex >= 0)
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

    private static string ShortcutText(string? resolved) => string.IsNullOrWhiteSpace(resolved) ? "No shortcut" : resolved;

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
                // The tooltip is pointer-only; this is how a screen reader learns why it is disabled.
                AutomationProperties.SetHelpText(button, BarFullTooltip);
            }

            button.Click += (_, _) => ChooseTitleBarPlacement(entry.Id, state);
            host.Children.Add(button);
        }

        return host;
    }

    private void ChooseTitleBarPlacement(string id, TitleBarItemState next)
    {
        // With the pointer over the rows, hold them where they are now (see _heldRows); they regroup once it
        // leaves. With the pointer elsewhere (a keyboard choice, usually) they regroup at once, and focus
        // follows the row (RestoreRowFocus).
        if (_pointerOverRows)
        {
            _heldRows ??= DraftRowLayout();
        }

        // Clicking the current choice un-checks its ToggleButton; the rebuild below puts it back.
        // The draft refuses a pin into a full bar, but "Bar" is disabled there, and its tooltip says why.
        if (Session.TitleBarDraft.GetState(id) != next
            && Session.TitleBarDraft.TrySetState(id, next)
            && next == TitleBarItemState.Pinned
            && _heldRows is { } held)
        {
            PutBackInHeldPlace(id, held);
        }

        RebuildTitleBarEditor();
    }

    /// <summary>
    /// The draft pins a row at the end of the bar. A held row in the bar group never moved on screen (Hide,
    /// then Bar again), so move it back up to its place there: the arrows act on the draft's order, which
    /// must match the order shown.
    /// </summary>
    private void PutBackInHeldPlace(string id, List<(string Id, TitleBarItemState Group)> held)
    {
        TitleBarDraftState draft = Session.TitleBarDraft;
        List<string> heldBar = held.Where(row => row.Group == TitleBarItemState.Pinned).Select(row => row.Id).ToList();
        int at = heldBar.IndexOf(id);
        if (at < 0)
        {
            return; // held outside the bar group: it has no place among the bar's rows yet
        }

        // Just ahead of the next row shown below it that is still pinned; with none, the end is its place.
        string? below = heldBar.Skip(at + 1).FirstOrDefault(other => draft.GetState(other) == TitleBarItemState.Pinned);
        if (below is null)
        {
            return;
        }

        List<string> order = draft.BuildSaveOrder();
        for (int steps = order.IndexOf(id) - order.IndexOf(below); steps > 0; steps--)
        {
            draft.MovePinned(id, -1);
        }
    }

    private void MoveDraftPinned(string id, int delta)
    {
        Session.TitleBarDraft.MovePinned(id, delta);
        _heldRows = null; // the bar's new order has to show, so held rows go to their groups first
        RebuildTitleBarEditor();
    }

    /// <summary>
    /// A lightweight replica of the title bar, not a real one: two sample tabs, then the right-aligned group: New Tab's "+", the
    /// pinned actions in order, "…" when the menu has anything in it, and dim caption buttons.
    /// </summary>
    private void RebuildMiniTitleBar()
    {
        var host = this.FindControl<Border>("TitleBarPreview")!;
        TitleBarDraftState draft = Session.TitleBarDraft;
        Dictionary<string, TitleBarCatalogEntry> byId = TitleBarCatalog.GetEntries().ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);

        var tabs = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { SampleTab("~/projects", active: true), SampleTab("pwsh", active: false) },
        };

        // As in the real title bar, "+" leads the right-aligned actions group.
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Tag = "actions" };
        actions.Children.Add(new TextBlock { Text = "+", Tag = "new_tab", Classes = { "MiniBarGlyph" }, VerticalAlignment = VerticalAlignment.Center });
        foreach (string id in draft.BuildSaveOrder().Where(id => !byId[id].IsLocked))
        {
            actions.Children.Add(new PathIcon { Data = Geometry.Parse(byId[id].IconGeometry), Width = 12, Height = 12, Tag = id, Classes = { "MiniBarIcon" } });
        }

        if (TitleBarCatalog.GetEntries().Any(entry => draft.GetState(entry.Id) == TitleBarItemState.Overflow))
        {
            actions.Children.Add(new TextBlock { Text = "…", Tag = "overflow", Classes = { "MiniBarGlyph" }, VerticalAlignment = VerticalAlignment.Center });
        }

        var captions = new TextBlock { Text = "—   □   ✕", Classes = { "MiniBarCaptions" }, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), Children = { tabs, actions, captions } };
        Grid.SetColumn(actions, 2);
        Grid.SetColumn(captions, 3);
        // The preview is decorative: the host is the one control element, read as an image named in the XAML (a bare
        // Border's peer is no control element, so its name would never be announced), and the glyph pile stays out of UIA.
        AutomationProperties.SetAccessibilityView(host, AccessibilityView.Control);
        HideFromAutomation(grid);
        host.Child = grid;
    }

    private static void HideFromAutomation(Control control)
    {
        AutomationProperties.SetAccessibilityView(control, AccessibilityView.Raw);
        foreach (Control child in control.GetLogicalChildren().OfType<Control>())
        {
            HideFromAutomation(child);
        }
    }

    private Border SampleTab(string title, bool active) => new()
    {
        Background = this.FindPaletteBrush(active ? "NtPanel" : "NtPanelAlt"),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(8, 2),
        Child = new TextBlock { Text = title, FontSize = 11, Foreground = this.FindPaletteBrush(active ? "NtFg" : "NtFg3") },
    };
}
