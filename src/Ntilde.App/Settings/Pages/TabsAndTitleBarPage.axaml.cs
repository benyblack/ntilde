using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Ntilde.Shell.TitleBar;
using Ntilde.Shell;
using System;
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
                _session.ShortcutsChanged -= RebuildTitleBarRows;
            }

            session.ShortcutsChanged += RebuildTitleBarRows;
        }

        _session = session;
        ComboTags.Select(this.FindControl<ComboBox>("TabOrientationList")!, session.Settings.TabStripOrientation, StringComparison.OrdinalIgnoreCase, fallbackIndex: 0);
        RebuildTitleBarRows();
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

    private void RebuildTitleBarRows()
    {

        var panel = this.FindControl<StackPanel>("TitleBarItemsPanel");
        if (panel == null)
        {
            return;
        }

        panel.Children.Clear();

        // Pinned entries first, in their configured order, so the ▲/▼ buttons act on a list
        // that reads top-to-bottom the way the bar reads left-to-right. Then the rest in
        // catalog order.
        var ordered = Session.TitleBarDraft.GetDisplayOrder();

        var byId = TitleBarCatalog.GetEntries()
            .ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < ordered.Count; i++)
        {
            panel.Children.Add(CreateTitleBarRow(byId[ordered[i]], i));
        }
    }

    private Control CreateTitleBarRow(
        TitleBarCatalogEntry entry,
        int index)
    {
        var state = Session.TitleBarDraft.GetState(entry.Id);
        bool isPinned = state == TitleBarItemState.Pinned;

        var row = new Border
        {
            Background = this.FindPaletteBrush("NtPanel"),
            BorderBrush = this.FindPaletteBrush("NtHairline"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
        };

        var icon = new PathIcon
        {
            Data = Geometry.Parse(entry.IconGeometry),
            Width = entry.IconSize,
            Height = entry.IconSize,
            VerticalAlignment = VerticalAlignment.Center,
        };

        string shortcut = TitleBarShortcuts.Resolve(entry.ShortcutKey, Session.ShortcutDraft);

        var labels = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = entry.Title },
                new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(shortcut) ? "No shortcut" : shortcut,
                    Classes = { RowDescStyleClass },
                },
            },
        };

        Control placement;
        if (entry.IsLocked)
        {
            // Locked: New Tab is the primary action and hosts the flyout with
            // "New SSH Connection…" / "Manage Profiles…" / "Agent Activity…". Letting it be
            // hidden would lose that flyout entirely.
            placement = new TextBlock
            {
                Text = "Always pinned",
                Classes = { RowDescStyleClass },
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        else
        {
            var combo = new ComboBox
            {
                MinWidth = 140,
                VerticalAlignment = VerticalAlignment.Center,
                ItemsSource = new[] { "Pinned", "Overflow", "Hidden" },
                SelectedItem = state.ToString(),
            };

            // Assigning combo.SelectedItem below (to snap a rejected pick back) re-raises
            // SelectionChanged synchronously. Without this guard, the re-entrant call would see
            // the reverted (old) value as "picked", write it into draft state again, and
            // immediately clear the validation message this same handler just showed.
            bool suppressSelectionChanged = false;

            combo.SelectionChanged += (s, e) =>
            {
                if (suppressSelectionChanged)
                {
                    return;
                }

                if (combo.SelectedItem is not string picked ||
                    !Enum.TryParse(picked, out TitleBarItemState next))
                {
                    return;
                }

                if (!Session.TitleBarDraft.TrySetState(entry.Id, next))
                {
                    // Explicit placement with no width-driven spill means nothing else stops a
                    // pinned set from running into the tab strip.
                    ShowTitleBarValidationMessage(
                        $"At most {TitleBarCatalog.MaxPinned} actions can be pinned. Move one to Overflow or Hidden first.");

                    suppressSelectionChanged = true;
                    combo.SelectedItem = Session.TitleBarDraft.GetState(entry.Id).ToString();
                    suppressSelectionChanged = false;
                    return;
                }

                ClearTitleBarValidationMessage();
                RebuildTitleBarRows();
            };

            placement = combo;
        }

        var up = new Button
        {
            Content = "▲",
            Classes = { "Pill" },
            IsEnabled = isPinned && !entry.IsLocked && index > 1,
            VerticalAlignment = VerticalAlignment.Center,
        };
        up.Click += (s, e) => MoveDraftPinned(entry.Id, -1);

        var down = new Button
        {
            Content = "▼",
            Classes = { "Pill" },
            IsEnabled = isPinned && !entry.IsLocked && index < Session.TitleBarDraft.CountPinned() - 1,
            VerticalAlignment = VerticalAlignment.Center,
        };
        down.Click += (s, e) => MoveDraftPinned(entry.Id, +1);

        row.Child = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto"),
            ColumnSpacing = 12,
            Children = { icon, labels, placement, up, down },
        };

        Grid.SetColumn(icon, 0);
        Grid.SetColumn(labels, 1);
        Grid.SetColumn(placement, 2);
        Grid.SetColumn(up, 3);
        Grid.SetColumn(down, 4);

        return row;
    }

    private void MoveDraftPinned(string id, int delta)
    {
        Session.TitleBarDraft.MovePinned(id, delta);
        RebuildTitleBarRows();
    }

    private void ShowTitleBarValidationMessage(string message)
    {
        var label = this.FindControl<TextBlock>("TitleBarValidationMessage");
        if (label == null) return;
        label.Text = message;
        label.IsVisible = true;
    }

    private void ClearTitleBarValidationMessage()
    {
        var label = this.FindControl<TextBlock>("TitleBarValidationMessage");
        if (label == null) return;
        label.IsVisible = false;
    }
}
