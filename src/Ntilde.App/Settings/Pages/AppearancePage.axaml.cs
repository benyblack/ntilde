using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Ntilde.Shell;
using Ntilde.VT;

namespace Ntilde.Settings.Pages;

/// <summary>Appearance: the theme (with its editor as a sub-page), the window, and the background image.</summary>
public partial class AppearancePage : UserControl, ISettingsPage, IHasSubPage
{
    private const string UnsavedThemeEditsMessage = "Save the theme or go back first.";

    private SettingsSession? _session;
    private TerminalTheme? _editingTheme;

    // The draft as last opened or saved. Unsaved edits are whatever differs from it.
    private TerminalTheme? _baselineTheme;
    private bool _draftIsSavedTheme;

    public AppearancePage()
    {
        InitializeComponent();
        WireThemeRow();
        WireThemeEditor();
        WireWindowRows();
        WireBackgroundImageRows();
    }

    private enum PreviewSurface
    {
        Page,
        Editor,
    }

    public SettingsPage Page => SettingsPage.Appearance;

    public Control View => this;

    /// <summary>True while the theme editor sub-page is showing.</summary>
    internal bool IsThemeEditorOpen => this.FindControl<ScrollViewer>("ThemeEditorView")!.IsVisible;

    private SettingsSession Session => _session ?? throw new InvalidOperationException("AppearancePage used before Load.");

    public void Load(SettingsSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;

        // Load also runs after a backup restore or import. The draft was cut from the old settings; drop it
        // (the restored ones may not hold the theme). Both confirmations warn that unsaved edits go.
        ShowMainView(restoreFocus: false);
        PopulateThemes();
        LoadControls();

        // The theme selection usually paints the preview via SelectionChanged, but only when an
        // item matches ThemeName - a name the manager normalizes (legacy "Default (Dark)") or a
        // since-deleted theme leaves the combo empty. Paint it from the active theme directly.
        UpdateThemePreview(session.Settings.ActiveTheme, PreviewSurface.Page);
    }

    /// <summary>
    /// The editor's draft lives only in this page until Save theme, so a window Save (Ctrl+S or the
    /// footer) while it holds unsaved edits would drop them. Refuse and say why.
    /// </summary>
    public SettingsValidationError? Validate()
        => RefuseToDropUnsavedThemeEdits()
            ? new SettingsValidationError(SettingsPage.Appearance, null, UnsavedThemeEditsMessage)
            : null;

    public void Collect(TerminalSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.WindowOpacity = Slider("WindowOpacitySlider").Value;
        target.UiScale = UiScale.Clamp(Slider("UiScaleSlider").Value);
        if (Combo("ThemeList").SelectedItem is ComboBoxItem themeItem)
        {
            target.ThemeName = themeItem.Content?.ToString() ?? "Default";
        }

        if (ComboTags.SelectedValue(Combo("BlurList")) is { } blur)
        {
            target.BlurEffect = blur;
        }

        target.BackgroundImagePath = this.FindControl<TextBox>("BgImagePathInput")!.Text ?? "";
        target.BackgroundImageOpacity = Slider("BgImageOpacitySlider").Value;
        if (ComboTags.SelectedValue(Combo("BgImageStretchList")) is { } stretch)
        {
            target.BackgroundImageStretch = stretch;
        }
    }

    public bool IsSubPageOpen => IsThemeEditorOpen;

    /// <summary>
    /// A search jump or deep link closes the editor only when that drops nothing, as Save does. A refused
    /// search jump moves focus from the emptied search box to Save theme, the safe way to act on the reason
    /// (Back would discard the draft).
    /// </summary>
    public void CloseSubPage(NavigationMethod? focusWith)
    {
        if (!RefuseToDropUnsavedThemeEdits())
        {
            ShowMainView(restoreFocus: true);
        }
        else if (focusWith is { } method)
        {
            this.FindControl<Button>("BtnSaveTheme")!.Focus(method);
        }
    }

    /// <summary>True, with the reason shown in the editor, while the open editor holds unsaved edits.</summary>
    private bool RefuseToDropUnsavedThemeEdits()
    {
        if (!IsThemeEditorOpen || !HasUnsavedThemeEdits())
        {
            return false;
        }

        // The status is a live region, read out when its text changes: a repeated refusal is set afresh.
        TextBlock status = Text("ThemeEditorStatus");
        status.Text = "";
        status.Text = UnsavedThemeEditsMessage;
        return true;
    }

    private void LoadControls()
    {
        TerminalSettings settings = Session.Settings;
        Slider("WindowOpacitySlider").Value = settings.WindowOpacity;
        Text("OpacityValueDisplay").Text = FormatPercent(settings.WindowOpacity);
        Slider("UiScaleSlider").Value = UiScale.Clamp(settings.UiScale);
        Text("UiScaleValueDisplay").Text = FormatUiScale(Slider("UiScaleSlider").Value);
        this.FindControl<TextBox>("BgImagePathInput")!.Text = settings.BackgroundImagePath;
        Slider("BgImageOpacitySlider").Value = settings.BackgroundImageOpacity;
        Text("BgImageOpacityDisplay").Text = FormatPercent(settings.BackgroundImageOpacity);

        // Legacy settings store "Default (Dark)" while the combo lists the manager's canonical
        // "Default". Normalize that one alias rather than resolving through GetTheme: for a name
        // GetTheme cannot find it falls back to the Default theme OBJECT, whose .Name would match
        // the "Default" item and silently rewrite the stored name on Save. With no match the
        // selection stays empty and Collect leaves ThemeName alone.
        string themeName = settings.ThemeName == "Default (Dark)" ? "Default" : settings.ThemeName;
        SelectTheme(themeName);

        ComboTags.Select(Combo("BlurList"), settings.BlurEffect, StringComparison.Ordinal, fallbackIndex: 0);
        ComboTags.Select(Combo("BgImageStretchList"), settings.BackgroundImageStretch, StringComparison.Ordinal, fallbackIndex: 3); // Fill (crop)
    }

    // ── Theme ─────────────────────────────────────────────────────────────────

    private void WireThemeRow()
    {
        var themeList = Combo("ThemeList");
        themeList.SelectionChanged += (_, _) =>
        {
            if (_session is null || themeList.SelectedItem is not ComboBoxItem { Content: { } content })
            {
                return;
            }

            string themeName = content.ToString() ?? "Default";
            TerminalTheme theme = Session.Settings.ThemeManager.GetTheme(themeName);
            UpdateThemePreview(theme, PreviewSurface.Page);
            Session.Preview.Theme(themeName);
            Session.Preview.RepaintWindow(theme);
        };

        this.FindControl<Button>("BtnEditTheme")!.Click += (_, _) =>
        {
            if (Combo("ThemeList").SelectedItem is ComboBoxItem item)
            {
                OpenThemeEditor(Session.Settings.ThemeManager.GetTheme(item.Content?.ToString() ?? "Default").Clone(), isSavedTheme: true);
            }
        };

        this.FindControl<Button>("BtnNewTheme")!.Click += (_, _) => OpenThemeEditor(new TerminalTheme { Name = "New Theme" }, isSavedTheme: false);

        this.FindControl<Button>("BtnImportTheme")!.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is not { } topLevel)
            {
                return;
            }

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import Theme",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Theme Files") { Patterns = new[] { "*.json", "*.itermcolors" } },
                    new FilePickerFileType("Alacritty Theme") { Patterns = new[] { "*.toml" } },
                },
            });
            if (files.Count == 0)
            {
                return;
            }

            string importedThemeName = Session.Settings.ThemeManager.ImportTheme(files[0].Path.LocalPath);
            if (string.IsNullOrEmpty(importedThemeName))
            {
                return;
            }

            PopulateThemes();
            Session.RaiseThemesChanged();
            SelectTheme(importedThemeName);
            Session.Preview.Theme(importedThemeName);
        };
    }

    private void PopulateThemes()
    {
        Session.Settings.ThemeManager.LoadThemes();
        var themeList = Combo("ThemeList");
        themeList.Items.Clear();
        foreach (string theme in Session.Settings.ThemeManager.GetAvailableThemes().OrderBy(t => t))
        {
            themeList.Items.Add(new ComboBoxItem { Content = theme });
        }
    }

    private void SelectTheme(string name)
    {
        var themeList = Combo("ThemeList");
        if (themeList.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Content?.ToString() == name) is { } match)
        {
            themeList.SelectedItem = match;
        }
    }

    // ── Theme editor sub-page ─────────────────────────────────────────────────

    private void WireThemeEditor()
    {
        this.FindControl<Button>("BtnThemeEditorBack")!.Click += (_, _) => ShowMainView(restoreFocus: true);

        var nameInput = this.FindControl<TextBox>("EditThemeNameInput")!;
        var status = Text("ThemeEditorStatus");

        this.FindControl<Button>("BtnSaveTheme")!.Click += (_, _) =>
        {
            if (_editingTheme is null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(nameInput.Text))
            {
                status.Text = "Name required";
                return;
            }

            _editingTheme.Name = nameInput.Text;

            // SaveTheme caches the object it is given. Hand it a copy, so edits made after this
            // Save stay in the draft and Back still discards them.
            // A failed write leaves the draft, the baseline and the saved state as they were, so
            // the window's Save still refuses to drop the unsaved edits.
            if (!Session.Settings.ThemeManager.SaveTheme(_editingTheme.Clone()))
            {
                status.Text = "Couldn't save the theme. Check that the themes folder can be written.";
                return;
            }

            PopulateThemes();
            Session.RaiseThemesChanged();
            SelectTheme(_editingTheme.Name);
            Session.Settings.RefreshActiveTheme();
            Session.Preview.Theme(_editingTheme.Name);

            // Repaint with the theme just saved. The old editor re-applied the theme named in
            // settings.json, so saving an edit of any other theme left Settings in the wrong
            // colours until it was reopened.
            Session.Preview.RepaintWindow(Session.Settings.ThemeManager.GetTheme(_editingTheme.Name));

            _baselineTheme = _editingTheme.Clone();
            _draftIsSavedTheme = true;
            this.FindControl<Button>("BtnDeleteTheme")!.IsEnabled = _editingTheme.Name != "Default";
            Text("ThemeEditorTitle").Text = _editingTheme.Name;
            status.Text = "Saved!";
            DispatcherTimer.RunOnce(
                () =>
                {
                    // Clear only our own message; a later error or refusal must stay visible.
                    if (status.Text == "Saved!")
                    {
                        status.Text = "";
                    }
                },
                TimeSpan.FromSeconds(2));
        };

        this.FindControl<Button>("BtnDeleteTheme")!.Click += (_, _) =>
        {
            if (_editingTheme is null || !_draftIsSavedTheme || _editingTheme.Name == "Default")
            {
                return;
            }

            Session.Settings.ThemeManager.DeleteTheme(_editingTheme.Name);
            PopulateThemes();
            Session.RaiseThemesChanged();
            Combo("ThemeList").SelectedIndex = 0;
            ShowMainView(restoreFocus: true);
        };

        for (int i = 0; i < 16; i++)
        {
            int index = i;
            var swatch = this.FindControl<Button>($"EditSwatch{index}")!;
            swatch.Click += (_, _) => OpenSwatchFlyout(swatch, index);
        }

        WireColourInput("EditThemeFgInput", (theme, colour) => theme.Foreground = colour);
        WireColourInput("EditThemeBgInput", (theme, colour) => theme.Background = colour);
        WireColourInput("EditThemeCursorInput", (theme, colour) => theme.CursorColor = colour);
    }

    /// <summary>Real-time updates for the foreground / background / cursor inputs (ORIG 418-442).</summary>
    private void WireColourInput(string name, Action<TerminalTheme, TermColor> apply)
    {
        var input = this.FindControl<TextBox>(name)!;
        input.TextChanged += (_, _) =>
        {
            if (Color.TryParse(input.Text, out var colour) && _editingTheme != null)
            {
                apply(_editingTheme, TermColorHelper.FromAvaloniaColor(colour));
                UpdateThemePreview(_editingTheme, PreviewSurface.Editor);
            }
        };
    }

    private void OpenThemeEditor(TerminalTheme theme, bool isSavedTheme)
    {
        _editingTheme = theme;
        _baselineTheme = theme.Clone();
        _draftIsSavedTheme = isSavedTheme;
        this.FindControl<TextBox>("EditThemeNameInput")!.Text = theme.Name;
        // With the alpha (ToHex, not ToString): the change handlers read this text back on a later pass, and
        // an opaque copy of a translucent colour would read as an edit.
        this.FindControl<TextBox>("EditThemeFgInput")!.Text = theme.Foreground.ToHex();
        this.FindControl<TextBox>("EditThemeBgInput")!.Text = theme.Background.ToHex();
        this.FindControl<TextBox>("EditThemeCursorInput")!.Text = theme.CursorColor.ToHex();
        this.FindControl<Button>("BtnDeleteTheme")!.IsEnabled = isSavedTheme && theme.Name != "Default";
        Text("ThemeEditorTitle").Text = theme.Name;
        Text("ThemeEditorStatus").Text = "";

        UpdateEditorSwatches();
        UpdateThemePreview(theme, PreviewSurface.Editor);

        this.FindControl<ScrollViewer>("AppearanceMainView")!.IsVisible = false;
        this.FindControl<ScrollViewer>("ThemeEditorView")!.IsVisible = true;
        FocusWhenShown("BtnThemeEditorBack");
    }

    /// <summary>
    /// True when the open draft differs from what was last opened or saved. Compared on demand, so
    /// the programmatic text the editor fills in on open (raised later) cannot read as an edit.
    /// </summary>
    private bool HasUnsavedThemeEdits()
    {
        if (_editingTheme is not { } draft || _baselineTheme is not { } baseline)
        {
            return false;
        }

        if (!string.Equals(this.FindControl<TextBox>("EditThemeNameInput")!.Text ?? "", baseline.Name, StringComparison.Ordinal)
            || !draft.Foreground.Equals(baseline.Foreground)
            || !draft.Background.Equals(baseline.Background)
            || !draft.CursorColor.Equals(baseline.CursorColor))
        {
            return true;
        }

        for (int i = 0; i < 16; i++)
        {
            if (!draft.GetAnsiColor(i % 8, i >= 8).Equals(baseline.GetAnsiColor(i % 8, i >= 8)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Moves keyboard focus to a control once layout has made it visible.</summary>
    private void FocusWhenShown(string name)
    {
        var control = this.FindControl<Control>(name)!;
        Dispatcher.UIThread.Post(() => control.Focus(), DispatcherPriority.Input);
    }

    /// <summary>Leaves the editor. Unsaved edits are dropped, as the old editor's ✕ did.</summary>
    private void ShowMainView(bool restoreFocus)
    {
        var editor = this.FindControl<ScrollViewer>("ThemeEditorView")!;

        // Only focus the editor had goes back to Edit (Back, Delete). A search jump that closes the
        // editor started in the search box, and moves focus to the row it lands on.
        bool focusWasInEditor = editor.IsVisible && editor.IsKeyboardFocusWithin;
        _editingTheme = null;
        _baselineTheme = null;
        _draftIsSavedTheme = false;
        editor.IsVisible = false;
        this.FindControl<ScrollViewer>("AppearanceMainView")!.IsVisible = true;
        if (restoreFocus && focusWasInEditor)
        {
            FocusWhenShown("BtnEditTheme");
        }
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
        // A local, not the field: Back clears _editingTheme while this flyout's handler lives on.
        if (_editingTheme is not { } theme) return;

        var current = theme.GetAnsiColor(index % 8, index >= 8);
        var hexInput = new TextBox { Text = current.ToHex(), Width = 100 }; // with the alpha, as OpenThemeEditor fills its fields
        var preview = new Border { Width = 30, Height = 30, Background = new SolidColorBrush(current.ToAvaloniaColor()), CornerRadius = new CornerRadius(4), Margin = new Thickness(5, 0, 0, 0) };

        hexInput.TextChanged += (s, e) =>
        {
            if (Color.TryParse(hexInput.Text, out var color))
            {
                preview.Background = new SolidColorBrush(color);
                theme.SetAnsiColor(index % 8, index >= 8, TermColorHelper.FromAvaloniaColor(color));
                target.Background = new SolidColorBrush(color);
                UpdateThemePreview(theme, PreviewSurface.Editor);
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

    /// <summary>
    /// Paints a preview card: the compact one on the page, or the editor's. They are separate
    /// cards now that the editor is its own sub-page.
    /// </summary>
    private void UpdateThemePreview(TerminalTheme theme, PreviewSurface surface)
    {
        bool editor = surface == PreviewSurface.Editor;
        var background = new SolidColorBrush(theme.Background.ToAvaloniaColor());
        this.FindControl<Border>(editor ? "EditorPreviewArea" : "ThemePreviewArea")!.Background = background;
        this.FindControl<Border>(editor ? "EditorSampleTextBorder" : "SampleTextBorder")!.Background = background;
        this.FindControl<TextBlock>(editor ? "EditorSampleTextBlock" : "SampleTextBlock")!.Foreground =
            new SolidColorBrush(theme.Foreground.ToAvaloniaColor());

        if (editor)
        {
            // The label sits on the theme's own background; keep it readable.
            double luminance = (0.299 * theme.Background.R + 0.587 * theme.Background.G + 0.114 * theme.Background.B) / 255.0;
            Text("EditorPreviewLabel").Foreground = luminance > 0.5 ? Brushes.Black : Brushes.White;
        }

        string swatchPrefix = editor ? "EditorPreviewSwatch" : "Swatch";
        for (int i = 0; i < 16; i++)
        {
            this.FindControl<Border>($"{swatchPrefix}{i}")!.Background =
                new SolidColorBrush(theme.GetAnsiColor(i % 8, i >= 8).ToAvaloniaColor());
        }
    }

    // ── Window ────────────────────────────────────────────────────────────────

    private void WireWindowRows()
    {
        var opacity = Slider("WindowOpacitySlider");
        var opacityDisplay = Text("OpacityValueDisplay");
        opacity.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty || _session is null)
            {
                return;
            }

            opacityDisplay.Text = FormatPercent(opacity.Value);
            Session.Preview.Opacity(opacity.Value);
        };

        var scale = Slider("UiScaleSlider");
        var scaleDisplay = Text("UiScaleValueDisplay");
        scale.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty || _session is null)
            {
                return;
            }

            double clamped = UiScale.Clamp(scale.Value);
            scaleDisplay.Text = FormatUiScale(clamped);
            Session.Preview.UiScale(clamped);
        };

        var blur = Combo("BlurList");
        blur.SelectionChanged += (_, _) =>
        {
            if (_session != null && ComboTags.SelectedValue(blur) is { } effect)
            {
                Session.Preview.Blur(effect);
            }
        };
    }

    // ── Background image ──────────────────────────────────────────────────────

    private void WireBackgroundImageRows()
    {
        var path = this.FindControl<TextBox>("BgImagePathInput")!;
        var opacity = Slider("BgImageOpacitySlider");
        var opacityDisplay = Text("BgImageOpacityDisplay");

        path.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                TriggerBgUpdate();
            }
        };

        opacity.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
            {
                opacityDisplay.Text = FormatPercent(opacity.Value);
                TriggerBgUpdate();
            }
        };

        Combo("BgImageStretchList").SelectionChanged += (_, _) => TriggerBgUpdate();

        this.FindControl<Button>("BtnBrowseImage")!.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is not { } topLevel)
            {
                return;
            }

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Background Image",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Images") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp" } },
                },
            });
            if (files.Count > 0)
            {
                path.Text = files[0].Path.LocalPath;
                TriggerBgUpdate();
            }
        };

        this.FindControl<Button>("BtnClearImage")!.Click += (_, _) =>
        {
            path.Text = "";
            TriggerBgUpdate();
        };
    }

    private void TriggerBgUpdate()
    {
        if (_session is null)
        {
            return;
        }

        string path = this.FindControl<TextBox>("BgImagePathInput")!.Text ?? "";
        double opacity = Slider("BgImageOpacitySlider").Value;
        string stretch = ComboTags.SelectedValue(Combo("BgImageStretchList")) ?? "UniformToFill";
        Session.Preview.BackgroundImage(path, opacity, stretch);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string FormatPercent(double value) => $"{(int)(value * 100)}%";

    private static string FormatUiScale(double scale) => $"{(int)Math.Round(scale * 100)}%";

    private Slider Slider(string name) => this.FindControl<Slider>(name)!;

    private ComboBox Combo(string name) => this.FindControl<ComboBox>(name)!;

    private TextBlock Text(string name) => this.FindControl<TextBlock>(name)!;
}
