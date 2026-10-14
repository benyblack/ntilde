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

public partial class AppearancePage : UserControl, ISettingsPage
{
    // Shared style-class name (see NtStyles' "TextBlock.RowDesc" selector), used by the row
    // builders below; a const keeps the literal from drifting away from the selector.
    private const string RowDescStyleClass = "RowDesc";

    private SettingsSession? _session;
    private TerminalTheme? _editingTheme;

    public AppearancePage()
    {
        InitializeComponent();
        WireThemeSection();
        WireFontRows();
        WireWindowRows();
    }

    public SettingsPage Page => SettingsPage.Appearance;

    public Control View => this;

    private SettingsSession Session => _session ?? throw new InvalidOperationException("AppearancePage used before Load.");

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
        PopulateFonts();
        PopulateThemes();
        LoadControls();
        RebuildTitleBarRows();

        // LoadControls' theme selection usually paints the preview via SelectionChanged, but only
        // when an item matches ThemeName - a name the manager normalizes (legacy "Default (Dark)")
        // or a since-deleted theme leaves the combo empty and the preview stuck on its hardcoded
        // dark background. Paint it from the active theme directly so it is always current.
        UpdateThemePreview(session.Settings.ActiveTheme, "Main");
    }

    public SettingsValidationError? Validate() => null;

    public void Collect(TerminalSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);

        target.FontSize = (double)(this.FindControl<NumericUpDown>("FontSizeInput")!.Value ?? 14);
        target.MaxHistory = (int)(this.FindControl<NumericUpDown>("ScrollbackInput")!.Value ?? 10000);
        target.WindowOpacity = this.FindControl<Slider>("WindowOpacitySlider")!.Value;
        target.UiScale = UiScale.Clamp(this.FindControl<Slider>("UiScaleSlider")!.Value);
        target.EnableLigatures = Toggle("LigatureToggle").IsChecked == true;
        target.EnableComplexShaping = Toggle("ComplexShapingToggle").IsChecked == true;
        target.LongCommandNotificationsEnabled = Toggle("LongCommandNotificationsToggle").IsChecked == true;
        target.Osc9NotificationsEnabled = Toggle("Osc9NotificationsToggle").IsChecked == true;
        target.Osc9ProgressReportingEnabled = Toggle("Osc9ProgressReportingToggle").IsChecked == true;
        target.AutomaticUpdateChecks = Toggle("AutomaticUpdateChecksToggle").IsChecked == true;

        if (SelectedItem("FontList") is { } fontItem)
        {
            target.FontFamily = fontItem.Content?.ToString() ?? BundledFontCatalog.DefaultTerminalFontFamily;
        }

        if (SelectedItem("ThemeList") is { } themeItem)
        {
            target.ThemeName = themeItem.Content?.ToString() ?? "Default";
        }

        if (SelectedItem("BlurList") is { } blurItem)
        {
            target.BlurEffect = blurItem.Content?.ToString() ?? "Acrylic";
        }

        if (SelectedItem("TabOrientationList") is { } orientationItem)
        {
            target.TabStripOrientation = orientationItem.Content?.ToString() ?? "Horizontal";
        }

        target.BackgroundImagePath = this.FindControl<TextBox>("BgImagePathInput")!.Text ?? "";
        target.BackgroundImageOpacity = this.FindControl<Slider>("BgImageOpacitySlider")!.Value;
        if (SelectedItem("BgImageStretchList") is { } stretchItem)
        {
            target.BackgroundImageStretch = stretchItem.Content?.ToString() ?? "UniformToFill";
        }
    }

    private void PopulateFonts()
    {
        var fontList = this.FindControl<ComboBox>("FontList")!;
        fontList.Items.Clear();
        foreach (string family in Session.FontChoices)
        {
            fontList.Items.Add(new ComboBoxItem { Content = family });
        }
    }

    private void PopulateThemes()
    {
        Session.Settings.ThemeManager.LoadThemes();
        var themeList = this.FindControl<ComboBox>("ThemeList")!;
        themeList.Items.Clear();
        foreach (string theme in Session.Settings.ThemeManager.GetAvailableThemes().OrderBy(t => t))
        {
            themeList.Items.Add(new ComboBoxItem { Content = theme });
        }
    }

    /// <summary>The Appearance half of the old LoadCurrentSettings, value for value.</summary>
    private void LoadControls()
    {
        TerminalSettings settings = Session.Settings;

        Toggle("LongCommandNotificationsToggle").IsChecked = settings.LongCommandNotificationsEnabled;
        Toggle("Osc9NotificationsToggle").IsChecked = settings.Osc9NotificationsEnabled;
        Toggle("Osc9ProgressReportingToggle").IsChecked = settings.Osc9ProgressReportingEnabled;
        Toggle("AutomaticUpdateChecksToggle").IsChecked = settings.AutomaticUpdateChecks;
        this.FindControl<NumericUpDown>("FontSizeInput")!.Value = (decimal)settings.FontSize;
        this.FindControl<NumericUpDown>("ScrollbackInput")!.Value = settings.MaxHistory;

        this.FindControl<Slider>("WindowOpacitySlider")!.Value = settings.WindowOpacity;
        this.FindControl<TextBlock>("OpacityValueDisplay")!.Text = $"{(int)(settings.WindowOpacity * 100)}%";

        var uiScaleSlider = this.FindControl<Slider>("UiScaleSlider")!;
        uiScaleSlider.Value = UiScale.Clamp(settings.UiScale);
        this.FindControl<TextBlock>("UiScaleValueDisplay")!.Text = FormatUiScale(uiScaleSlider.Value);

        Toggle("LigatureToggle").IsChecked = settings.EnableLigatures;
        Toggle("ComplexShapingToggle").IsChecked = settings.EnableComplexShaping;

        this.FindControl<TextBox>("BgImagePathInput")!.Text = settings.BackgroundImagePath;
        this.FindControl<Slider>("BgImageOpacitySlider")!.Value = settings.BackgroundImageOpacity;
        this.FindControl<TextBlock>("BgImageOpacityDisplay")!.Text = $"{(int)(settings.BackgroundImageOpacity * 100)}%";

        SelectByContent("FontList", settings.FontFamily, StringComparison.Ordinal, fallbackIndex: null);

        // Legacy settings store "Default (Dark)" while the combo lists the manager's canonical
        // "Default" (ThemeManager.GetTheme maps between them). Normalize that one alias rather than
        // resolving through GetTheme: for a name GetTheme cannot find it falls back to the Default
        // theme OBJECT, whose .Name would match the "Default" item and silently rewrite the stored
        // name on Save. When nothing matches, the selection stays empty and Collect skips the theme
        // field, preserving the stored name.
        string themeName = settings.ThemeName == "Default (Dark)" ? "Default" : settings.ThemeName;
        SelectByContent("ThemeList", themeName, StringComparison.Ordinal, fallbackIndex: null);

        SelectByContent("BlurList", settings.BlurEffect, StringComparison.Ordinal, fallbackIndex: 0);
        SelectByContent("TabOrientationList", settings.TabStripOrientation, StringComparison.OrdinalIgnoreCase, fallbackIndex: 0);
        SelectByContent("BgImageStretchList", settings.BackgroundImageStretch, StringComparison.Ordinal, fallbackIndex: 3); // UniformToFill
    }

    /// <summary>
    /// Selects the item whose text equals <paramref name="value"/>. With no match, falls back to
    /// <paramref name="fallbackIndex"/> only when nothing is selected yet, exactly as the old loops did.
    /// </summary>
    private void SelectByContent(string comboName, string? value, StringComparison comparison, int? fallbackIndex)
    {
        var combo = this.FindControl<ComboBox>(comboName)!;
        ComboBoxItem? match = combo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Content?.ToString(), value, comparison));
        if (match != null)
        {
            combo.SelectedItem = match;
        }
        else if (fallbackIndex is int index && combo.SelectedItem == null && combo.ItemCount > 0)
        {
            combo.SelectedIndex = index;
        }
    }

    private ComboBoxItem? SelectedItem(string comboName) => this.FindControl<ComboBox>(comboName)!.SelectedItem as ComboBoxItem;

    private CheckBox Toggle(string name) => this.FindControl<CheckBox>(name)!;

    private void WireThemeSection()
    {
        var themeList = this.FindControl<ComboBox>("ThemeList");
        var btnEditTheme = this.FindControl<Button>("BtnEditTheme");
        var btnNewTheme = this.FindControl<Button>("BtnNewTheme");
        var btnCloseEditor = this.FindControl<Button>("BtnCloseEditor");
        var btnSaveTheme = this.FindControl<Button>("BtnSaveTheme");
        var btnDeleteTheme = this.FindControl<Button>("BtnDeleteTheme");
        var btnImportTheme = this.FindControl<Button>("BtnImportTheme");
        var themeEditorPanel = this.FindControl<Border>("ThemeEditorPanel");
        var editThemeNameInput = this.FindControl<TextBox>("EditThemeNameInput");
        var editThemeFgInput = this.FindControl<TextBox>("EditThemeFgInput");
        var editThemeBgInput = this.FindControl<TextBox>("EditThemeBgInput");
        var editThemeCursorInput = this.FindControl<TextBox>("EditThemeCursorInput");
        var themeEditorStatus = this.FindControl<TextBlock>("ThemeEditorStatus");
        if (btnImportTheme != null)
        {
            btnImportTheme.Click += async (s, e) =>
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel != null)
                {
                    var files = await topLevel.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
                    {
                        Title = "Import Theme",
                        AllowMultiple = false,
                        FileTypeFilter = new[]
                        {
                            new Avalonia.Platform.Storage.FilePickerFileType("Theme Files") { Patterns = new[] { "*.json", "*.itermcolors" } },
                            new Avalonia.Platform.Storage.FilePickerFileType("Alacritty Theme") { Patterns = new[] { "*.toml" } }
                        }
                    });

                    if (files.Count > 0)
                    {
                        string path = files[0].Path.LocalPath;
                        string importedThemeName = Session.Settings.ThemeManager.ImportTheme(path);
                        if (!string.IsNullOrEmpty(importedThemeName))
                        {
                            PopulateThemes();
                            Session.RaiseThemesChanged();
                            // Select the imported theme
                            if (themeList != null)
                            {
                                foreach (ComboBoxItem it in themeList.Items.Cast<ComboBoxItem>())
                                {
                                    if (it.Content?.ToString() == importedThemeName)
                                    {
                                        themeList.SelectedItem = it;
                                        Session.Preview.Theme(importedThemeName);
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
            };
        }

        if (btnEditTheme != null)
        {
            btnEditTheme.Click += (s, e) =>
            {
                if (themeList?.SelectedItem is ComboBoxItem item)
                {
                    var themeName = item.Content?.ToString() ?? "Default";
                    var theme = Session.Settings.ThemeManager.GetTheme(themeName);
                    OpenThemeEditor(theme.Clone());
                }
            };
        }

        if (btnNewTheme != null)
        {
            btnNewTheme.Click += (s, e) =>
            {
                OpenThemeEditor(new TerminalTheme { Name = "New Theme" });
            };
        }

        if (btnCloseEditor != null)
        {
            btnCloseEditor.Click += (s, e) =>
            {
                if (themeEditorPanel != null) themeEditorPanel.IsVisible = false;
            };
        }

        if (btnSaveTheme != null)
        {
            btnSaveTheme.Click += (s, e) =>
            {
                if (_editingTheme != null)
                {
                    if (string.IsNullOrWhiteSpace(editThemeNameInput?.Text))
                    {
                        if (themeEditorStatus != null) themeEditorStatus.Text = "Name required";
                        return;
                    }

                    _editingTheme.Name = editThemeNameInput.Text;
                    Session.Settings.ThemeManager.SaveTheme(_editingTheme);
                    PopulateThemes();
                    Session.RaiseThemesChanged();

                    // Select the saved theme in the list
                    if (themeList != null)
                    {
                        foreach (ComboBoxItem it in themeList.Items.Cast<ComboBoxItem>())
                        {
                            if (it.Content?.ToString() == _editingTheme.Name)
                            {
                                themeList.SelectedItem = it;
                                Session.Settings.RefreshActiveTheme();
                                Session.Preview.Theme(_editingTheme.Name);
                                break;
                            }
                        }
                    }

                    // Session.Preview.Theme refreshes the host window, but nothing above re-applies
                    // the palette to THIS window when the saved theme was already the selected
                    // one (editing colors of the active theme) - the selection change is a
                    // no-op, so SelectionChanged never fires and the editor's new background,
                    // sidebar, and card colors only showed up after closing and reopening
                    // Settings (or restarting). Re-apply here so the edit lands live.
                    Session.Preview.RepaintWindow(null);

                    if (themeEditorStatus != null) themeEditorStatus.Text = "Saved!";
                    DispatcherTimer.RunOnce(() => { if (themeEditorStatus != null) themeEditorStatus.Text = ""; }, TimeSpan.FromSeconds(2));
                }
            };
        }

        if (btnDeleteTheme != null)
        {
            btnDeleteTheme.Click += (s, e) =>
            {
                if (_editingTheme != null && _editingTheme.Name != "Default")
                {
                    Session.Settings.ThemeManager.DeleteTheme(_editingTheme.Name);
                    PopulateThemes();
                    Session.RaiseThemesChanged();
                    if (themeList != null) themeList.SelectedIndex = 0;
                    if (themeEditorPanel != null) themeEditorPanel.IsVisible = false;
                }
            };
        }

        // Wire up ANSI swatch inputs
        for (int i = 0; i < 16; i++)
        {
            int index = i;
            var swatchBtn = this.FindControl<Button>($"EditSwatch{index}");
            if (swatchBtn != null)
            {
                swatchBtn.Click += (s, e) => OpenSwatchFlyout(swatchBtn, index);
            }
        }

        // Real-time updates for global color inputs
        if (editThemeFgInput != null) editThemeFgInput.TextChanged += (s, e) =>
        {
            if (Color.TryParse(editThemeFgInput.Text, out var color) && _editingTheme != null)
            {
                _editingTheme.Foreground = TermColorHelper.FromAvaloniaColor(color);
                UpdateThemePreview(_editingTheme, "Editor");
            }
        };
        if (editThemeBgInput != null) editThemeBgInput.TextChanged += (s, e) =>
        {
            if (Color.TryParse(editThemeBgInput.Text, out var color) && _editingTheme != null)
            {
                _editingTheme.Background = TermColorHelper.FromAvaloniaColor(color);
                UpdateThemePreview(_editingTheme, "Editor");
            }
        };
        if (editThemeCursorInput != null) editThemeCursorInput.TextChanged += (s, e) =>
        {
            if (Color.TryParse(editThemeCursorInput.Text, out var color) && _editingTheme != null)
            {
                _editingTheme.CursorColor = TermColorHelper.FromAvaloniaColor(color);
                UpdateThemePreview(_editingTheme, "Editor");
            }
        };

        if (themeList != null)
        {
            themeList.SelectionChanged += (s, e) =>
            {
                if (themeList.SelectedItem is ComboBoxItem item)
                {
                    var theme = Session.Settings.ThemeManager.GetTheme(item.Content?.ToString() ?? "Default");
                    UpdateThemePreview(theme, "Main");
                }
            };
        }

        if (themeList != null)
        {
            themeList.SelectionChanged += (s, e) =>
            {
                if (themeList.SelectedItem is ComboBoxItem item && item.Content != null)
                {
                    var themeName = item.Content.ToString() ?? "Default";
                    Session.Preview.Theme(themeName);
                    var theme = Session.Settings.ThemeManager.GetTheme(themeName);
                    Session.Preview.RepaintWindow(theme);
                }
            };
        }
    }

    private void WireFontRows()
    {
        var fontList = this.FindControl<ComboBox>("FontList");
        var fontSizeInput = this.FindControl<NumericUpDown>("FontSizeInput");

        // Core Settings
        if (fontList != null)
        {
            fontList.SelectionChanged += (s, e) =>
            {
                if (fontList.SelectedItem is ComboBoxItem item && item.Content != null)
                {
                    Session.Preview.Font(item.Content.ToString() ?? BundledFontCatalog.DefaultTerminalFontFamily);
                }
            };
        }

        if (fontSizeInput != null)
        {
            fontSizeInput.ValueChanged += (s, e) =>
            {
                if (fontSizeInput.Value.HasValue)
                {
                    Session.Preview.FontSize((double)fontSizeInput.Value.Value);
                }
            };
        }
    }

    private void TriggerBgUpdate()
    {
        var bgPathInput = this.FindControl<TextBox>("BgImagePathInput");
        var bgOpacitySlider = this.FindControl<Slider>("BgImageOpacitySlider");
        var bgStretchList = this.FindControl<ComboBox>("BgImageStretchList");
        var path = bgPathInput?.Text ?? "";
        var opacity = bgOpacitySlider?.Value ?? 0.5;
        var stretch = (bgStretchList?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "UniformToFill";
        Session.Preview.BackgroundImage(path, opacity, stretch);
    }

    private void WireWindowRows()
    {
        var opacitySlider = this.FindControl<Slider>("WindowOpacitySlider");
        var opacityDisplay = this.FindControl<TextBlock>("OpacityValueDisplay");
        var blurList = this.FindControl<ComboBox>("BlurList");
        var bgPathInput = this.FindControl<TextBox>("BgImagePathInput");
        var bgOpacitySlider = this.FindControl<Slider>("BgImageOpacitySlider");
        var bgStretchList = this.FindControl<ComboBox>("BgImageStretchList");
        var bgOpacityDisplay = this.FindControl<TextBlock>("BgImageOpacityDisplay");

        // Bg Image
        if (bgPathInput != null)
        {
            bgPathInput.PropertyChanged += (s, e) =>
            {
                if (e.Property == TextBox.TextProperty) TriggerBgUpdate();
            };
        }

        if (bgOpacitySlider != null && bgOpacityDisplay != null)
        {
            bgOpacityDisplay.Text = $"{(int)(bgOpacitySlider.Value * 100)}%";
            bgOpacitySlider.PropertyChanged += (s, e) =>
            {
                if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty)
                {
                    bgOpacityDisplay.Text = $"{(int)(bgOpacitySlider.Value * 100)}%";
                    TriggerBgUpdate();
                }
            };
        }

        if (bgStretchList != null)
        {
            bgStretchList.SelectionChanged += (s, e) => TriggerBgUpdate();
        }

        if (opacitySlider != null && opacityDisplay != null)
        {
            opacityDisplay.Text = $"{(int)(opacitySlider.Value * 100)}%";
            opacitySlider.PropertyChanged += (s, e) =>
            {
                if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty)
                {
                    opacityDisplay.Text = $"{(int)(opacitySlider.Value * 100)}%";
                    Session.Preview.Opacity(opacitySlider.Value);
                }
            };
        }

        var uiScaleSlider = this.FindControl<Slider>("UiScaleSlider");
        var uiScaleDisplay = this.FindControl<TextBlock>("UiScaleValueDisplay");
        if (uiScaleSlider != null && uiScaleDisplay != null)
        {
            uiScaleDisplay.Text = FormatUiScale(uiScaleSlider.Value);
            uiScaleSlider.PropertyChanged += (s, e) =>
            {
                if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty)
                {
                    double scale = UiScale.Clamp(uiScaleSlider.Value);
                    uiScaleDisplay.Text = FormatUiScale(scale);
                    Session.Preview.UiScale(scale);
                }
            };
        }

        if (blurList != null)
        {
            blurList.SelectionChanged += (s, e) =>
            {
                if (blurList.SelectedItem is ComboBoxItem item && item.Content != null)
                {
                    Session.Preview.Blur(item.Content.ToString() ?? "Acrylic");
                }
            };
        }

        var btnBrowse = this.FindControl<Button>("BtnBrowseImage");
        if (btnBrowse != null)
        {
            btnBrowse.Click += async (s, e) =>
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel != null)
                {
                    var files = await topLevel.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
                    {
                        Title = "Select Background Image",
                        AllowMultiple = false,
                        FileTypeFilter = new[]
                        {
                            new Avalonia.Platform.Storage.FilePickerFileType("Images") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp" } }
                        }
                    });

                    if (files.Count > 0)
                    {
                        if (bgPathInput != null)
                        {
                            bgPathInput.Text = files[0].Path.LocalPath;
                            TriggerBgUpdate();
                        }
                    }
                }
            };
        }

        var btnClearBg = this.FindControl<Button>("BtnClearImage");
        if (btnClearBg != null)
        {
            btnClearBg.Click += (s, e) =>
            {
                if (bgPathInput != null)
                {
                    bgPathInput.Text = "";
                    TriggerBgUpdate();
                }
            };
        }
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

    private void UpdateThemePreview(TerminalTheme theme, string context)
    {
        // Update the single preview area we have regardless of whether it's the global theme
        // or a profile override being touched.
        var sampleBorder = this.FindControl<Border>("SampleTextBorder");
        var sampleText = this.FindControl<TextBlock>("SampleTextBlock");
        var previewArea = this.FindControl<Border>("ThemePreviewArea");

        if (sampleBorder != null) sampleBorder.Background = new SolidColorBrush(theme.Background.ToAvaloniaColor());
        if (sampleText != null) sampleText.Foreground = new SolidColorBrush(theme.Foreground.ToAvaloniaColor());

        // Also update the container background to match the theme (so it doesn't look like a black box in a light theme)
        // But lets make it slightly different so we can distinguish the "terminal area"
        if (previewArea != null)
        {
            previewArea.Background = new SolidColorBrush(theme.Background.ToAvaloniaColor());
            // Ensure the preview label is visible against this background
            var children = (previewArea.Child as StackPanel)?.Children;
            if (children != null && children.Count > 0 && children[0] is TextBlock label)
            {
                // Calculate contrast for the label
                double lum = (0.299 * theme.Background.R + 0.587 * theme.Background.G + 0.114 * theme.Background.B) / 255.0;
                label.Foreground = lum > 0.5 ? Brushes.Black : Brushes.White;
            }
        }

        for (int i = 0; i < 16; i++)
        {
            var swatch = this.FindControl<Border>($"Swatch{i}");
            if (swatch != null)
            {
                swatch.Background = new SolidColorBrush(theme.GetAnsiColor(i % 8, i >= 8).ToAvaloniaColor());
            }
        }
    }

    private void OpenThemeEditor(TerminalTheme theme)
    {
        _editingTheme = theme;
        var panel = this.FindControl<Border>("ThemeEditorPanel");
        var nameInput = this.FindControl<TextBox>("EditThemeNameInput");
        var fgInput = this.FindControl<TextBox>("EditThemeFgInput");
        var bgInput = this.FindControl<TextBox>("EditThemeBgInput");
        var cursorInput = this.FindControl<TextBox>("EditThemeCursorInput");
        var btnDelete = this.FindControl<Button>("BtnDeleteTheme");

        if (panel != null) panel.IsVisible = true;
        if (nameInput != null) nameInput.Text = theme.Name;
        if (fgInput != null) fgInput.Text = theme.Foreground.ToString();
        if (bgInput != null) bgInput.Text = theme.Background.ToString();
        if (cursorInput != null) cursorInput.Text = theme.CursorColor.ToString();

        if (btnDelete != null) btnDelete.IsEnabled = theme.Name != "Default";

        UpdateEditorSwatches();
        UpdateThemePreview(theme, "Editor");
    }

    private void UpdateEditorSwatches()
    {
        if (_editingTheme == null) return;
        for (int i = 0; i < 16; i++)
        {
            var btn = this.FindControl<Button>($"EditSwatch{i}");
            if (btn != null)
            {
                btn.Background = new SolidColorBrush(_editingTheme.GetAnsiColor(i % 8, i >= 8).ToAvaloniaColor());
            }
        }
    }

    private void OpenSwatchFlyout(Button target, int index)
    {
        if (_editingTheme == null) return;

        var current = _editingTheme.GetAnsiColor(index % 8, index >= 8);
        var hexInput = new TextBox { Text = current.ToString(), Width = 100 };
        var preview = new Border { Width = 30, Height = 30, Background = new SolidColorBrush(current.ToAvaloniaColor()), CornerRadius = new CornerRadius(4), Margin = new Thickness(5, 0, 0, 0) };

        hexInput.TextChanged += (s, e) =>
        {
            if (Color.TryParse(hexInput.Text, out var color))
            {
                preview.Background = new SolidColorBrush(color);
                _editingTheme.SetAnsiColor(index % 8, index >= 8, TermColorHelper.FromAvaloniaColor(color));
                target.Background = new SolidColorBrush(color);
                UpdateThemePreview(_editingTheme, "Editor");
            }
        };

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(10),
            Children = { hexInput, preview }
        };

        var flyout = new Flyout { Content = content };
        flyout.ShowAt(target);
    }

    private static string FormatUiScale(double scale) => $"{(int)Math.Round(scale * 100)}%";
}
