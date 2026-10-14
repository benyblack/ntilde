using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Ntilde.Shell;
using Ntilde.Platform;
using Ntilde.Pty;
using Ntilde.VT;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using Avalonia.Threading;
using Avalonia.Media;
using Avalonia.Controls.Shapes;
using Avalonia.Styling;
using Ntilde.CommandAssist.Application;
using Ntilde.CommandAssist.Domain;
using Ntilde.CommandAssist.Models;
using Ntilde.CommandAssist.ShellIntegration.Remote;
using Ntilde.Backup;
using Ntilde.Services.Ssh;
using Ntilde.Shell.Shortcuts;
using Ntilde.Shell.TitleBar;
using Ntilde.Settings;

namespace Ntilde.Settings.Pages;

public partial class ShortcutsPage : UserControl, ISettingsPage
{
    /// <summary>
    /// Class on every binding editor. The frame's Ctrl/Cmd+S skips a focused editor so the chord
    /// can be recorded as a binding.
    /// </summary>
    internal const string CaptureClass = "ShortcutCapture";

    private const string RowDescStyleClass = "RowDesc";

    private SettingsSession? _session;

    public ShortcutsPage()
    {
        InitializeComponent();
        var search = this.FindControl<TextBox>("ShortcutSearchInput")!;
        search.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty && _session != null)
            {
                PopulateShortcutBindingsPanel(search.Text ?? "");
            }
        };
    }

    public SettingsPage Page => SettingsPage.Shortcuts;

    public Control View => this;

    private SettingsSession Session => _session ?? throw new InvalidOperationException("ShortcutsPage used before Load.");

    public void Load(SettingsSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        PopulateShortcutBindingsPanel(this.FindControl<TextBox>("ShortcutSearchInput")!.Text ?? "");
    }

    public SettingsValidationError? Validate()
    {
        ShortcutBindingResolution resolution = ShortcutBindingResolver.Resolve(ShortcutCatalog.GetDefinitions(), Session.ShortcutDraft);
        if (resolution.IsValid)
        {
            return null;
        }

        const string message = "Resolve duplicate shortcuts before saving.";
        ShowShortcutValidationMessage(message);
        return new SettingsValidationError(SettingsPage.Shortcuts, null, message);
    }

    public void Collect(TerminalSettings target)
    {
        // Nothing to collect: keybindings are committed from the session's draft (SettingsSession.CommitDrafts).
    }

    internal static IReadOnlyList<ShortcutCatalogEntry> FilterShortcutCatalogEntries(string query, bool sessionPersistence = true)
    {
        IEnumerable<ShortcutCatalogEntry> entries = ShortcutCatalog.GetEntries();
        if (!sessionPersistence)
        {
            // With persistence off nothing mux-related exists: no command, no dispatch, no row.
            entries = entries.Where(entry => !entry.RequiresSessionPersistence);
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            string trimmedQuery = query.Trim();
            entries = entries.Where(entry =>
                entry.Title.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase) ||
                entry.Category.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase) ||
                FormatScopeLabel(entry.Scope).Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase) ||
                entry.CommandId.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase));
        }

        return entries
            .OrderBy(entry => entry.Scope)
            .ThenBy(entry => entry.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static string DescribeDefaultBinding(string binding) =>
        string.IsNullOrWhiteSpace(binding) ? "Default none" : $"Default {binding}";

    private void PopulateShortcutBindingsPanel(string query)
    {
        var panel = this.FindControl<StackPanel>("ShortcutBindingsPanel");
        if (panel == null)
        {
            return;
        }

        panel.Children.Clear();
        string? activeScope = null;
        IReadOnlyList<ShortcutCatalogEntry> entries = FilterShortcutCatalogEntries(
            query,
            Ntilde.Shell.Mux.SessionPersistenceMode.IsKeepOnClose(Session.Settings.SessionPersistence));
        foreach (ShortcutCatalogEntry entry in entries)
        {
            string scopeLabel = FormatScopeLabel(entry.Scope);
            if (!string.Equals(activeScope, scopeLabel, StringComparison.Ordinal))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = scopeLabel.ToUpperInvariant(),
                    Classes = { "SectionHeader" },
                    Margin = new Thickness(0, activeScope == null ? 0 : 12, 0, 8),
                });
                activeScope = scopeLabel;
            }

            panel.Children.Add(CreateShortcutBindingRow(entry));
        }

        if (panel.Children.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "No shortcuts match the current filter.",
                Classes = { RowDescStyleClass },
            });
        }
    }

    private Control CreateShortcutBindingRow(ShortcutCatalogEntry entry)
    {
        string effectiveBinding = GetEffectiveShortcutBinding(entry);
        var row = new Border
        {
            Background = this.FindPaletteBrush("NtPanel"),
            BorderBrush = this.FindPaletteBrush("NtHairline"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 8),
        };

        var errorText = new TextBlock
        {
            Foreground = Brushes.IndianRed,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };

        var bindingEditor = new TextBox
        {
            Text = effectiveBinding,
            IsReadOnly = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            PlaceholderText = "Press shortcut",
            MinWidth = 180,
        };
        bindingEditor.Classes.Add(CaptureClass);

        bindingEditor.KeyDown += (s, e) => HandleShortcutEditorKeyDown(entry, bindingEditor, errorText, e);

        var resetButton = new Button
        {
            Content = "Reset",
            Classes = { "Pill" },
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        resetButton.Click += (s, e) =>
        {
            Session.ShortcutDraft.Remove(entry.CommandId);
            bindingEditor.Text = entry.DefaultBinding;
            errorText.IsVisible = false;
            ClearShortcutValidationMessage();
            // Pages that print shortcuts (title bar rows, Command Assist descriptions) listen for this;
            // without it they keep showing the old binding until Settings reopens.
            Session.RaiseShortcutsChanged();
        };

        row.Child = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,220,Auto"),
                    ColumnSpacing = 12,
                    Children =
                    {
                        new StackPanel
                        {
                            Spacing = 2,
                            Children =
                            {
                                new TextBlock
                                {
                                    Text = entry.Title,
                                    Classes = { "RowLabel" },
                                },
                                new TextBlock
                                {
                                    Text = $"{entry.Category} · {FormatScopeLabel(entry.Scope)} · {DescribeDefaultBinding(entry.DefaultBinding)}",
                                    Classes = { RowDescStyleClass },
                                },
                            },
                        },
                        bindingEditor,
                        resetButton,
                    }
                },
                errorText,
            }
        };

        Grid.SetColumn(bindingEditor, 1);
        Grid.SetColumn(resetButton, 2);
        return row;
    }

    private void HandleShortcutEditorKeyDown(
        ShortcutCatalogEntry entry,
        TextBox bindingEditor,
        TextBlock errorText,
        KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            bindingEditor.Text = GetEffectiveShortcutBinding(entry);
            errorText.IsVisible = false;
            ClearShortcutValidationMessage();
            e.Handled = true;
            return;
        }

        if (IsModifierKey(e.Key))
        {
            e.Handled = true;
            return;
        }

        string binding = ShortcutMatcher.Normalize(e);
        Dictionary<string, string> candidateBindings = new(Session.ShortcutDraft, StringComparer.OrdinalIgnoreCase);
        if (string.Equals(binding, entry.DefaultBinding, StringComparison.Ordinal))
        {
            candidateBindings.Remove(entry.CommandId);
        }
        else
        {
            candidateBindings[entry.CommandId] = binding;
        }

        ShortcutBindingResolution resolution = ShortcutBindingResolver.Resolve(ShortcutCatalog.GetDefinitions(), candidateBindings);
        ShortcutBindingConflict? conflict = resolution.Conflicts.FirstOrDefault(item =>
            item.Bindings.Any(bindingRecord => string.Equals(bindingRecord.CommandId, entry.CommandId, StringComparison.OrdinalIgnoreCase)));

        if (conflict != null)
        {
            string conflictOwner = conflict.Bindings
                .Select(bindingRecord => ShortcutCatalog.GetEntries().First(catalogEntry => catalogEntry.CommandId == bindingRecord.CommandId).Title)
                .First(title => !string.Equals(title, entry.Title, StringComparison.OrdinalIgnoreCase));
            errorText.Text = $"{binding} is already assigned to {conflictOwner}.";
            errorText.IsVisible = true;
            ShowShortcutValidationMessage("Resolve duplicate shortcuts before saving.");
            e.Handled = true;
            return;
        }

        Session.ReplaceShortcutDraft(candidateBindings);
        bindingEditor.Text = binding;
        errorText.IsVisible = false;
        ClearShortcutValidationMessage();
        e.Handled = true;

        // Pages that print shortcuts (title bar rows, Command Assist descriptions) listen for this;
        // without it they keep showing the old binding until Settings reopens.
        Session.RaiseShortcutsChanged();
    }

    private static bool IsModifierKey(Key key)
    {
        return key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;
    }

    private string GetEffectiveShortcutBinding(ShortcutCatalogEntry entry)
    {
        return Session.ShortcutDraft.TryGetValue(entry.CommandId, out string? binding) &&
               !string.IsNullOrWhiteSpace(binding)
            ? binding
            : entry.DefaultBinding;
    }

    private static string FormatScopeLabel(ShortcutScope scope)
    {
        return scope switch
        {
            ShortcutScope.App => "Application",
            ShortcutScope.Pane => "Pane",
            ShortcutScope.CommandAssist => "Command Assist",
            _ => scope.ToString(),
        };
    }

    private void ShowShortcutValidationMessage(string message)
    {
        var validationMessage = this.FindControl<TextBlock>("ShortcutValidationMessage");
        if (validationMessage == null)
        {
            return;
        }

        validationMessage.Text = message;
        validationMessage.IsVisible = true;
    }

    private void ClearShortcutValidationMessage()
    {
        var validationMessage = this.FindControl<TextBlock>("ShortcutValidationMessage");
        if (validationMessage == null)
        {
            return;
        }

        validationMessage.Text = string.Empty;
        validationMessage.IsVisible = false;
    }
}
