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
using Avalonia.Styling;
using Ntilde.CommandAssist.Application;
using Ntilde.CommandAssist.Domain;
using Ntilde.CommandAssist.Models;
using Ntilde.CommandAssist.ShellIntegration.Remote;
using Ntilde.Backup;
using Ntilde.Shell.Shortcuts;
using Ntilde.Shell.TitleBar;
using Ntilde.Settings;

namespace Ntilde.Settings.Pages;

/// <summary>
/// The Profiles page: edits the local terminal profiles. SSH connections are edited in the connection manager; this page shows local profiles only.
/// </summary>
public partial class ProfilesPage : UserControl, ISettingsPage, IDisposable
{
    private SettingsSession? _session;

    private TerminalProfile? _selectedProfile;
    private System.Collections.Generic.List<TerminalProfile> _profilesList = new();

    public ProfilesPage()
    {
        InitializeComponent();

        var profilesListBox = this.FindControl<ListBox>("ProfilesListBox");
        var btnAddProfile = this.FindControl<Button>("BtnAddProfile");
        var btnDeleteProfile = this.FindControl<Button>("BtnDeleteProfile");
        var btnSetDefault = this.FindControl<Button>("BtnSetDefault");
        var nameInput = this.FindControl<TextBox>("ProfileNameInput");
        var commandInput = this.FindControl<TextBox>("ProfileCommandInput");
        var argsInput = this.FindControl<TextBox>("ProfileArgsInput");
        var cwdInput = this.FindControl<TextBox>("ProfileCwdInput");
        var groupInput = this.FindControl<TextBox>("ProfileGroupInput");
        var tagsInput = this.FindControl<TextBox>("ProfileTagsInput");
        var checkFont = this.FindControl<CheckBox>("CheckOverrideFont");
        var checkSize = this.FindControl<CheckBox>("CheckOverrideSize");
        var checkTheme = this.FindControl<CheckBox>("CheckOverrideTheme");
        var overrideFontList = this.FindControl<ComboBox>("OverrideFontList");
        var overrideFontSize = this.FindControl<NumericUpDown>("OverrideFontSizeInput");
        var checkLigatures = this.FindControl<CheckBox>("CheckOverrideLigatures");
        var overrideLigatureToggle = this.FindControl<CheckBox>("OverrideLigatureToggle");
        var importStatus = this.FindControl<TextBlock>("ImportStatusText");
        var btnImportWT = this.FindControl<Button>("BtnImportWT");
        var overrideThemeList = this.FindControl<ComboBox>("OverrideThemeList");

        // Profile Controls
        if (profilesListBox != null)
        {
            profilesListBox.SelectionChanged += (s, e) =>
            {
                if (profilesListBox.SelectedItem is ListBoxItem item && item.Tag is TerminalProfile profile)
                {
                    SwitchSelectedProfile(profile);
                }
            };
        }

        if (btnAddProfile != null)
        {
            btnAddProfile.Click += (s, e) =>
            {
                // Seeded with this platform's shell, not cmd.exe: on Linux and macOS the
                // latter gave every freshly added profile a command that cannot spawn.
                var newProfile = new TerminalProfile { Name = "New Profile", Command = ShellHelper.GetDefaultShell(), Type = ConnectionType.Local };
                _profilesList.Add(newProfile);
                PopulateProfilesList();
                if (profilesListBox != null) profilesListBox.SelectedIndex = _profilesList.Count - 1;
            };
        }

        if (btnDeleteProfile != null)
        {
            btnDeleteProfile.Click += (s, e) =>
            {
                if (_selectedProfile != null && _profilesList.Count > 1)
                {
                    var index = _profilesList.IndexOf(_selectedProfile);
                    _profilesList.Remove(_selectedProfile);
                    PopulateProfilesList();
                    if (profilesListBox != null) profilesListBox.SelectedIndex = Math.Clamp(index, 0, _profilesList.Count - 1);
                }
            };
        }

        if (btnImportWT != null)
        {
            btnImportWT.Click += (s, e) =>
            {
                var imported = ProfileImporter.ImportWindowsTerminalProfiles();
                int added = 0;
                foreach (var p in imported)
                {
                    if (p.Type != ConnectionType.Local)
                    {
                        continue;
                    }

                    if (!_profilesList.Any(x => x.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        _profilesList.Add(p);
                        added++;
                    }
                }
                if (added > 0)
                {
                    PopulateProfilesList();
                    if (profilesListBox != null) profilesListBox.SelectedIndex = _profilesList.Count - 1;
                    if (importStatus != null) importStatus.Text = $"Imported {added} profiles.";
                }
                else
                {
                    if (importStatus != null) importStatus.Text = "No new profiles found.";
                }
            };
        }

        if (btnSetDefault != null)
        {
            btnSetDefault.Click += (s, e) =>
            {
                if (_selectedProfile != null)
                {
                    Session.Settings.DefaultProfileId = _selectedProfile.Id;
                    PopulateProfilesList(); // Refresh labels
                }
            };
        }

        // Profile Editor Inputs
        if (nameInput != null) nameInput.KeyUp += (s, e) => { if (_selectedProfile != null) { _selectedProfile.Name = nameInput.Text ?? ""; RefreshProfileListItem(_selectedProfile); } };
        if (commandInput != null) commandInput.KeyUp += (s, e) => { if (_selectedProfile != null) _selectedProfile.Command = commandInput.Text ?? ""; };
        if (argsInput != null) argsInput.KeyUp += (s, e) => { if (_selectedProfile != null) _selectedProfile.Arguments = argsInput.Text ?? ""; };
        if (cwdInput != null) cwdInput.KeyUp += (s, e) => { if (_selectedProfile != null) _selectedProfile.StartingDirectory = cwdInput.Text ?? ""; };
        if (groupInput != null) groupInput.KeyUp += (s, e) => { if (_selectedProfile != null) _selectedProfile.Group = groupInput.Text ?? "General"; };
        if (tagsInput != null) tagsInput.KeyUp += (s, e) =>
        {
            if (_selectedProfile != null)
                _selectedProfile.Tags = (tagsInput.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        };

        // Overrides Logic
        if (checkFont != null)
        {
            checkFont.IsCheckedChanged += (s, e) =>
            {
                if (_selectedProfile != null)
                {
                    if (checkFont.IsChecked == true)
                    {
                        if (_selectedProfile.FontFamily == null) _selectedProfile.FontFamily = Session.Settings.FontFamily;
                        if (overrideFontList != null)
                            foreach (var item in overrideFontList.Items.OfType<ComboBoxItem>())
                                if (item.Content?.ToString() == _selectedProfile.FontFamily) overrideFontList.SelectedItem = item;
                    }
                    else
                    {
                        _selectedProfile.FontFamily = null;
                    }
                }
            };
        }

        if (overrideFontList != null)
        {
            overrideFontList.SelectionChanged += (s, e) =>
            {
                if (_selectedProfile != null && checkFont?.IsChecked == true && overrideFontList.SelectedItem is ComboBoxItem item)
                {
                    _selectedProfile.FontFamily = item.Content?.ToString();
                }
            };
        }

        if (checkSize != null)
        {
            checkSize.IsCheckedChanged += (s, e) =>
            {
                if (_selectedProfile != null)
                {
                    if (checkSize.IsChecked == true)
                    {
                        if (_selectedProfile.FontSize == null) _selectedProfile.FontSize = Session.Settings.FontSize;
                        if (overrideFontSize != null) overrideFontSize.Value = (decimal)(_selectedProfile.FontSize ?? 14);
                    }
                    else
                    {
                        _selectedProfile.FontSize = null;
                    }
                }
            };
        }

        if (overrideFontSize != null)
        {
            overrideFontSize.ValueChanged += (s, e) =>
            {
                if (_selectedProfile != null && checkSize?.IsChecked == true && overrideFontSize.Value.HasValue)
                {
                    _selectedProfile.FontSize = (double)overrideFontSize.Value.Value;
                }
            };
        }

        if (checkTheme != null)
        {
            checkTheme.IsCheckedChanged += (s, e) =>
            {
                if (_selectedProfile != null)
                {
                    if (checkTheme.IsChecked == true)
                    {
                        if (_selectedProfile.ThemeName == null) _selectedProfile.ThemeName = Session.Settings.ThemeName;
                        if (overrideThemeList != null)
                            foreach (var item in overrideThemeList.Items.OfType<ComboBoxItem>())
                                if (item.Content?.ToString() == _selectedProfile.ThemeName) overrideThemeList.SelectedItem = item;
                    }
                    else
                    {
                        _selectedProfile.ThemeName = null;
                    }
                }
            };
        }

        if (overrideThemeList != null)
        {
            overrideThemeList.SelectionChanged += (s, e) =>
            {
                if (_selectedProfile != null && checkTheme?.IsChecked == true && overrideThemeList.SelectedItem is ComboBoxItem item)
                {
                    _selectedProfile.ThemeName = item.Content?.ToString();
                }
            };
        }

        if (checkLigatures != null)
        {
            checkLigatures.IsCheckedChanged += (s, e) =>
            {
                if (_selectedProfile != null)
                {
                    if (checkLigatures.IsChecked == true)
                    {
                        if (_selectedProfile.EnableLigatures == null) _selectedProfile.EnableLigatures = Session.Settings.EnableLigatures;
                        if (overrideLigatureToggle != null) overrideLigatureToggle.IsChecked = _selectedProfile.EnableLigatures;
                    }
                    else
                    {
                        _selectedProfile.EnableLigatures = null;
                    }
                }
            };
        }

        if (overrideLigatureToggle != null)
        {
            overrideLigatureToggle.IsCheckedChanged += (s, e) =>
            {
                if (_selectedProfile != null && checkLigatures?.IsChecked == true)
                {
                    _selectedProfile.EnableLigatures = overrideLigatureToggle.IsChecked;
                }
            };
        }
    }

    public SettingsPage Page => SettingsPage.Profiles;

    public Control View => this;

    private SettingsSession Session => _session ?? throw new InvalidOperationException("ProfilesPage used before Load.");

    /// <summary>Takes this page's handler off the session: the window disposes its pages when it closes.</summary>
    public void Dispose()
    {
        if (_session != null)
        {
            _session.ThemesChanged -= PopulateOverrideThemes;
        }
        GC.SuppressFinalize(this);
    }

    public void Load(SettingsSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!ReferenceEquals(_session, session))
        {
            if (_session != null)
            {
                _session.ThemesChanged -= PopulateOverrideThemes;
            }

            session.ThemesChanged += PopulateOverrideThemes;
        }

        // Captured before the rebuild: _selectedProfile points into the list about to be replaced,
        // and its Id is all that still identifies "the same" profile afterwards.
        Guid? previousSelectedProfileId = _selectedProfile?.Id;
        _session = session;

        // Settings editor is local-profiles only; SSH connections are managed in Connection Manager.
        _profilesList = BuildLocalProfilesForEditor(session.Settings.Profiles);
        session.Settings.DefaultProfileId = ResolveDefaultLocalProfileId(session.Settings.DefaultProfileId, _profilesList);

        PopulateOverrideFonts();
        PopulateOverrideThemes();
        PopulateProfilesList();
        RepointSelectedProfileAfterReload(previousSelectedProfileId);
    }

    public SettingsValidationError? Validate() => null;

    public void Collect(TerminalSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);

        // Sync local profiles list back to settings (SSH connections are store-backed separately).
        target.Profiles = NormalizeSettingsProfilesForSave(_profilesList);
        target.DefaultProfileId = ResolveDefaultLocalProfileId(target.DefaultProfileId, target.Profiles);
    }

    private void PopulateOverrideFonts()
    {
        var overrideFontList = this.FindControl<ComboBox>("OverrideFontList")!;
        overrideFontList.Items.Clear();
        foreach (string family in Session.FontChoices)
        {
            overrideFontList.Items.Add(new ComboBoxItem { Content = family });
        }
    }

    private void PopulateOverrideThemes()
    {
        Session.Settings.ThemeManager.LoadThemes();
        var overrideThemeList = this.FindControl<ComboBox>("OverrideThemeList")!;
        overrideThemeList.Items.Clear();
        foreach (string theme in Session.Settings.ThemeManager.GetAvailableThemes().OrderBy(t => t))
        {
            overrideThemeList.Items.Add(new ComboBoxItem { Content = theme });
        }
    }

    /// <summary>
    /// Fix (I1 residual #2): <c>_selectedProfile</c> points at an entry of the PRE-reload
    /// <c>_profilesList</c>. <see cref="SettingsSession.ReloadAfterExternalChangeAsync"/> re-Loads this page, which rebuilds
    /// <c>_profilesList</c> with fresh <see cref="TerminalProfile"/> instances (freshly
    /// deserialized from the reloaded settings.json) but, without this, never re-points
    /// <c>_selectedProfile</c> at the corresponding new instance - it is left dangling,
    /// referencing an object no longer reachable from <c>_profilesList</c>. The profile
    /// editor's KeyUp handlers (bound to <c>_selectedProfile</c> directly) would keep mutating
    /// that detached object, so edits made after an import/restore are silently dropped at
    /// Save; Delete would likewise become a no-op since the object is no longer in the list to
    /// remove.
    /// </summary>
    /// <remarks>
    /// Re-points by <see cref="TerminalProfile.Id"/> - the one thing that still identifies
    /// "the same" profile across the rebuild - by setting the ProfilesListBox's SelectedItem
    /// to the matching new item. That raises the same SelectionChanged handler a user
    /// re-clicking the profile by hand would, which calls <see cref="SwitchSelectedProfile"/>
    /// and re-derives every editor-pane control from the fresh instance - so this reuses the
    /// existing selection machinery rather than duplicating it. When the previously-selected
    /// profile no longer exists post-reload (the import/restore removed or renamed it),
    /// <c>_selectedProfile</c> is cleared and the editor pane's fields are blanked to match,
    /// rather than continuing to display a profile that is no longer in the list.
    /// </remarks>
    private void RepointSelectedProfileAfterReload(Guid? previousSelectedProfileId)
    {
        if (previousSelectedProfileId is not Guid id)
        {
            return; // Nothing was selected before the reload; nothing to re-point.
        }

        var profilesListBox = this.FindControl<ListBox>("ProfilesListBox");
        var matchedItem = profilesListBox?.Items
            .OfType<ListBoxItem>()
            .FirstOrDefault(item => item.Tag is TerminalProfile profile && profile.Id == id);

        if (matchedItem != null)
        {
            if (profilesListBox != null) profilesListBox.SelectedItem = matchedItem;
            return;
        }

        _selectedProfile = null;
        if (profilesListBox != null) profilesListBox.SelectedItem = null;
        ClearProfileEditorFields();
    }

    /// <summary>
    /// Blanks the profile editor pane's controls, mirroring the field set
    /// <see cref="SwitchSelectedProfile"/> populates but with neutral/empty values, so the UI
    /// does not keep showing a profile that is no longer in <c>_profilesList</c>. Does not
    /// touch event wiring - only sets control values, the same way <see cref="SwitchSelectedProfile"/>
    /// does.
    /// </summary>
    private void ClearProfileEditorFields()
    {
        var nameInput = this.FindControl<TextBox>("ProfileNameInput");
        if (nameInput != null) nameInput.Text = string.Empty;
        var commandInput = this.FindControl<TextBox>("ProfileCommandInput");
        if (commandInput != null) commandInput.Text = string.Empty;
        var argsInput = this.FindControl<TextBox>("ProfileArgsInput");
        if (argsInput != null) argsInput.Text = string.Empty;
        var cwdInput = this.FindControl<TextBox>("ProfileCwdInput");
        if (cwdInput != null) cwdInput.Text = string.Empty;
        var groupInput = this.FindControl<TextBox>("ProfileGroupInput");
        if (groupInput != null) groupInput.Text = string.Empty;
        var tagsInput = this.FindControl<TextBox>("ProfileTagsInput");
        if (tagsInput != null) tagsInput.Text = string.Empty;

        var checkOverrideFont = this.FindControl<CheckBox>("CheckOverrideFont");
        if (checkOverrideFont != null) checkOverrideFont.IsChecked = false;
        var checkOverrideSize = this.FindControl<CheckBox>("CheckOverrideSize");
        if (checkOverrideSize != null) checkOverrideSize.IsChecked = false;
        var checkOverrideTheme = this.FindControl<CheckBox>("CheckOverrideTheme");
        if (checkOverrideTheme != null) checkOverrideTheme.IsChecked = false;
        var checkOverrideLigatures = this.FindControl<CheckBox>("CheckOverrideLigatures");
        if (checkOverrideLigatures != null) checkOverrideLigatures.IsChecked = false;
    }

    /// <summary>The profiles the editor works on: a full copy of each local profile.</summary>
    internal static System.Collections.Generic.List<TerminalProfile> BuildLocalProfilesForEditor(System.Collections.Generic.IEnumerable<TerminalProfile> profiles)
        => CopyLocalProfiles(profiles);

    /// <summary>The profiles Save writes back: a full copy of each local profile the editor holds.</summary>
    internal static System.Collections.Generic.List<TerminalProfile> NormalizeSettingsProfilesForSave(System.Collections.Generic.IEnumerable<TerminalProfile> profiles)
        => CopyLocalProfiles(profiles);

    /// <summary>
    /// Local profiles only (SSH connections live in the connection store), each copied whole with
    /// <see cref="TerminalProfile.CopyForEditing"/>. The editor shows and changes only some fields;
    /// copying every field means a Save writes the others back as they were, instead of resetting
    /// them to their defaults.
    /// </summary>
    private static System.Collections.Generic.List<TerminalProfile> CopyLocalProfiles(System.Collections.Generic.IEnumerable<TerminalProfile> profiles)
        => (profiles ?? System.Array.Empty<TerminalProfile>())
            .Where(profile => profile.Type == ConnectionType.Local)
            .Select(profile => profile.CopyForEditing())
            .ToList();

    internal static Guid ResolveDefaultLocalProfileId(Guid currentDefaultId, System.Collections.Generic.IReadOnlyList<TerminalProfile> localProfiles)
    {
        if (localProfiles == null || localProfiles.Count == 0)
        {
            return Guid.Empty;
        }

        return localProfiles.Any(profile => profile.Id == currentDefaultId)
            ? currentDefaultId
            : localProfiles[0].Id;
    }

    private void PopulateProfilesList()
    {
        var profilesListBox = this.FindControl<ListBox>("ProfilesListBox");
        if (profilesListBox == null) return;

        profilesListBox.Items.Clear();
        foreach (var profile in _profilesList)
        {
            // UI Polish: Show all profiles the user has configured.
            // Previously we hid "invalid" ones, but that hides imported WSL profiles if not found in path.
            // Let the user see and fix them if broken.

            var isDefault = profile.Id == Session.Settings.DefaultProfileId;
            var displayName = profile.Name + (isDefault ? " (Default)" : "");
            var item = new ListBoxItem
            {
                Content = displayName,
                Tag = profile
            };
            profilesListBox.Items.Add(item);
        }
    }

    private void RefreshProfileListItem(TerminalProfile profile)
    {
        var profilesListBox = this.FindControl<ListBox>("ProfilesListBox");
        if (profilesListBox == null) return;

        foreach (ListBoxItem item in profilesListBox.Items.Cast<ListBoxItem>())
        {
            if (item.Tag == profile)
            {
                var isDefault = profile.Id == Session.Settings.DefaultProfileId;
                item.Content = profile.Name + (isDefault ? " (Default)" : "");
                break;
            }
        }
    }

    private void SwitchSelectedProfile(TerminalProfile profile)
    {
        _selectedProfile = profile;

        this.FindControl<TextBox>("ProfileNameInput")!.Text = profile.Name;
        this.FindControl<TextBox>("ProfileCommandInput")!.Text = profile.Command;
        this.FindControl<TextBox>("ProfileArgsInput")!.Text = profile.Arguments ?? "";
        this.FindControl<TextBox>("ProfileCwdInput")!.Text = profile.StartingDirectory ?? "";
        this.FindControl<TextBox>("ProfileGroupInput")!.Text = profile.Group ?? "General";
        this.FindControl<TextBox>("ProfileTagsInput")!.Text = string.Join(", ", profile.Tags ?? new System.Collections.Generic.List<string>());

        this.FindControl<CheckBox>("CheckOverrideFont")!.IsChecked = profile.FontFamily != null;
        this.FindControl<CheckBox>("CheckOverrideSize")!.IsChecked = profile.FontSize.HasValue;
        this.FindControl<CheckBox>("CheckOverrideTheme")!.IsChecked = profile.ThemeName != null;
        this.FindControl<CheckBox>("CheckOverrideLigatures")!.IsChecked = profile.EnableLigatures.HasValue;

        // Sync values to override inputs
        var overrideLigatureToggle = this.FindControl<CheckBox>("OverrideLigatureToggle");
        if (overrideLigatureToggle != null) overrideLigatureToggle.IsChecked = profile.EnableLigatures ?? Session.Settings.EnableLigatures;
        var overrideFontList = this.FindControl<ComboBox>("OverrideFontList");
        if (overrideFontList != null)
        {
            var targetFont = profile.FontFamily ?? Session.Settings.FontFamily;
            foreach (ComboBoxItem item in overrideFontList.Items.Cast<ComboBoxItem>())
                if (item.Content?.ToString() == targetFont) { overrideFontList.SelectedItem = item; break; }
        }

        var overrideFontSize = this.FindControl<NumericUpDown>("OverrideFontSizeInput");
        if (overrideFontSize != null)
        {
            overrideFontSize.Value = (decimal)(profile.FontSize ?? Session.Settings.FontSize);
        }

        var overrideThemeList = this.FindControl<ComboBox>("OverrideThemeList");
        if (overrideThemeList != null)
        {
            var targetTheme = profile.ThemeName ?? Session.Settings.ThemeName;
            foreach (var obj in overrideThemeList.Items)
                if (obj is ComboBoxItem item && item.Content?.ToString() == targetTheme) { overrideThemeList.SelectedItem = item; break; }
        }
    }
}
