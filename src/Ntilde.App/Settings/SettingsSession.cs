using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Ntilde.CommandAssist.Domain;
using Ntilde.Platform;
using Ntilde.Shell;
using Ntilde.Shell.TitleBar;
using Ntilde.VT;

namespace Ntilde.Settings;

/// <summary>
/// What every page of one open Settings window shares: the working settings, the drafts Save
/// commits, the live stores MainWindow injects, and the preview sink. One per window.
/// </summary>
public sealed class SettingsSession
{
    private readonly Func<IEnumerable<string>> _systemFontFamilies;
    private Func<Task>? _externalReload;

    public SettingsSession(TerminalSettings settings, Func<IEnumerable<string>>? systemFontFamilies = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _systemFontFamilies = systemFontFamilies ?? (() => SkiaSharp.SKFontManager.Default.FontFamilies);
        Settings = settings;
        ResetFromSettings();
    }

    /// <summary>
    /// The working settings; Save collects every page into it and writes it. Pages mostly change it
    /// only in <see cref="ISettingsPage.Collect"/>, with three exceptions: the Profiles page writes
    /// <c>DefaultProfileId</c> in Load and on "Set as default", and the Appearance page calls
    /// <c>RefreshActiveTheme()</c> on it after a theme edit.
    /// </summary>
    public TerminalSettings Settings { get; private set; }

    /// <summary>Pending keybindings, committed on Save. Case-insensitive, like <see cref="TerminalSettings.Keybindings"/>.</summary>
    public Dictionary<string, string> ShortcutDraft { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Pending title-bar placement, committed on Save as deltas.</summary>
    public TitleBarDraftState TitleBarDraft { get; } = new();

    /// <summary>
    /// Whether the General page's "Keep shells running" choice is on right now. The General page
    /// still writes the setting itself on Save; this is the live value other pages follow (the
    /// Shortcuts page lists the mux commands only while it is on).
    /// </summary>
    public bool KeepShellsRunning { get; private set; }

    /// <summary>
    /// Font families to offer: installed, bundled, the global font and every local profile's
    /// override, even when not installed here, so a configured font is always selectable.
    /// </summary>
    public IReadOnlyList<string> FontChoices { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// Theme names to offer, sorted. The theme manager reads the themes folder once; a theme saved,
    /// imported or deleted through it afterwards is already in its list.
    /// </summary>
    public IReadOnlyList<string> LoadThemeNames() => Settings.ThemeManager.GetAvailableThemes().OrderBy(name => name).ToList();

    public SettingsPreviewSink Preview { get; } = new();

    /// <summary>
    /// The live history store MainWindow injects, so "Clear history" acts on the instance panes
    /// append to. Null for a window opened elsewhere (tests, design time).
    /// </summary>
    public IHistoryStore? HistoryStore { get; set; }

    /// <summary>The live snippet store MainWindow injects. Null for a window opened elsewhere.</summary>
    public ISnippetStore? SnippetStore { get; set; }

    /// <summary>Raised after a shortcut draft edit or reset; pages that print chords refresh.</summary>
    public event Action? ShortcutsChanged;

    /// <summary>Raised after a theme was saved, deleted or imported; pages that list themes refresh.</summary>
    public event Action? ThemesChanged;

    /// <summary>Raised after command history was actually cleared.</summary>
    public event Action? HistoryCleared;

    /// <summary>Raised after a snippet was added, edited or deleted, or the snippet file was replaced.</summary>
    public event Action? SnippetsChanged;

    /// <summary>Raised when <see cref="KeepShellsRunning"/> changes; not on a reload, which reloads every page anyway.</summary>
    public event Action? KeepShellsRunningChanged;

    public void SetKeepShellsRunning(bool value)
    {
        if (KeepShellsRunning == value)
        {
            return;
        }

        KeepShellsRunning = value;
        KeepShellsRunningChanged?.Invoke();
    }

    public void ReplaceShortcutDraft(Dictionary<string, string> draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ShortcutDraft = draft;
    }

    public void RaiseShortcutsChanged() => ShortcutsChanged?.Invoke();

    public void RaiseThemesChanged() => ThemesChanged?.Invoke();

    public void RaiseHistoryCleared() => HistoryCleared?.Invoke();

    public void RaiseSnippetsChanged() => SnippetsChanged?.Invoke();

    /// <summary>The Backup page calls this after a successful import or restore; the frame reloads every page.</summary>
    public Task ReloadAfterExternalChangeAsync() => _externalReload?.Invoke() ?? Task.CompletedTask;

    internal void SetExternalReloadHandler(Func<Task> handler) => _externalReload = handler;

    /// <summary>Swaps in freshly loaded settings and rebuilds both drafts and the font choices, discarding pending draft edits.</summary>
    public void Reload(TerminalSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Settings = settings;
        ResetFromSettings();
    }

    /// <summary>
    /// Writes the drafts into <paramref name="target"/>. Title-bar values are deltas only: an id at
    /// its catalog default is omitted, so a future catalog change reaches existing users.
    /// </summary>
    public void CommitDrafts(TerminalSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.Keybindings = new Dictionary<string, string>(ShortcutDraft, StringComparer.OrdinalIgnoreCase);
        target.TitleBarItems = TitleBarDraft.BuildSaveDelta();
        target.TitleBarOrder = TitleBarDraft.BuildSaveOrder();
    }

    private void ResetFromSettings()
    {
        ShortcutDraft = new Dictionary<string, string>(Settings.Keybindings, StringComparer.OrdinalIgnoreCase);
        KeepShellsRunning = Ntilde.Shell.Mux.SessionPersistenceMode.IsKeepOnClose(Settings.SessionPersistence);

        // Seeded from the resolver's output: it alone normalizes hand-edited ids ("Find" vs
        // "find"), and a second reader here would drift from it.
        TitleBarDraft.SeedFrom(TitleBarLayoutResolver.Resolve(Settings.TitleBarItems, Settings.TitleBarOrder, null));

        FontChoices = CollectFontFamilyChoices(
            _systemFontFamilies(),
            Settings.FontFamily,
            Settings.Profiles.Where(profile => profile.Type == ConnectionType.Local).Select(profile => profile.FontFamily));
    }

    /// <param name="additionalConfiguredFontFamily">
    /// A second font name to guarantee is present, independent of
    /// <paramref name="configuredFontFamily"/> (Codex review round 3) — e.g. a selected
    /// profile's own font override, seeded alongside the global font rather than instead of
    /// it. Both are seeded unconditionally (each ignored only when null/blank); which one
    /// ultimately gets selected is <c>LoadCurrentSettings</c>'/the caller's decision, not
    /// this method's — it only guarantees visibility in the choice list.
    /// </param>
    internal static System.Collections.Generic.List<string> BuildFontFamilyChoices(
        System.Collections.Generic.IEnumerable<string> systemFonts,
        string? configuredFontFamily,
        string? additionalConfiguredFontFamily = null)
    {
        var names = new System.Collections.Generic.SortedSet<string>(
            systemFonts?.Where(f => !string.IsNullOrWhiteSpace(f)) ?? System.Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        // Every bundled family, not just the default: the others ship in the
        // binary and work, but are typically not installed system-wide, so
        // without this they would be unpickable - which is how Cascadia Mono PL
        // would have disappeared from this list when it stopped being the
        // default. The symbols-only font is excluded by SelectableFamilies.
        foreach (string bundled in BundledFontCatalog.SelectableFamilies)
        {
            names.Add(bundled);
        }

        if (!string.IsNullOrWhiteSpace(configuredFontFamily))
        {
            names.Add(configuredFontFamily);
        }

        if (!string.IsNullOrWhiteSpace(additionalConfiguredFontFamily))
        {
            names.Add(additionalConfiguredFontFamily);
        }

        return names.ToList();
    }

    /// <summary>
    /// <see cref="BuildFontFamilyChoices"/>, guaranteeing every name in
    /// <paramref name="additionalFontFamilies"/> as well (blank names are ignored).
    /// </summary>
    internal static List<string> CollectFontFamilyChoices(
        IEnumerable<string> systemFonts,
        string? configuredFontFamily,
        IEnumerable<string?> additionalFontFamilies)
    {
        var names = new SortedSet<string>(BuildFontFamilyChoices(systemFonts, configuredFontFamily), StringComparer.OrdinalIgnoreCase);
        foreach (string? family in additionalFontFamilies)
        {
            if (!string.IsNullOrWhiteSpace(family))
            {
                names.Add(family);
            }
        }

        return names.ToList();
    }
}
