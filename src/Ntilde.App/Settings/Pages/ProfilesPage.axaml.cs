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

public partial class ProfilesPage : UserControl, ISettingsPage, IDisposable
{
    private SettingsSession? _session;

    private TerminalProfile? _selectedProfile;
    private System.Collections.Generic.List<TerminalProfile> _profilesList = new();
    private DispatcherTimer? _statusTimer;

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
        var typeList = this.FindControl<ComboBox>("ProfileTypeList");
        var sshPanel = this.FindControl<StackPanel>("SshSettingsPanel");
        var sshHostInput = this.FindControl<TextBox>("SshHostInput");
        var sshPortInput = this.FindControl<NumericUpDown>("SshPortInput");
        var sshUserInput = this.FindControl<TextBox>("SshUserInput");
        var sshKeyPathInput = this.FindControl<TextBox>("SshKeyPathInput");
        var btnBrowseSshKey = this.FindControl<Button>("BtnBrowseSshKey");
        var jumpList = this.FindControl<ComboBox>("JumpHostList");
        var radioAgent = this.FindControl<RadioButton>("RadioAuthAgent");
        var radioKey = this.FindControl<RadioButton>("RadioAuthKey");
        var checkFont = this.FindControl<CheckBox>("CheckOverrideFont");
        var checkSize = this.FindControl<CheckBox>("CheckOverrideSize");
        var checkTheme = this.FindControl<CheckBox>("CheckOverrideTheme");
        var overrideFontList = this.FindControl<ComboBox>("OverrideFontList");
        var overrideFontSize = this.FindControl<NumericUpDown>("OverrideFontSizeInput");
        var ligatureToggle = this.FindControl<CheckBox>("LigatureToggle");
        var checkLigatures = this.FindControl<CheckBox>("CheckOverrideLigatures");
        var overrideLigatureToggle = this.FindControl<CheckBox>("OverrideLigatureToggle");
        var importStatus = this.FindControl<TextBlock>("ImportStatusText");
        var btnImportWT = this.FindControl<Button>("BtnImportWT");
        var btnAddRule = this.FindControl<Button>("BtnAddRule");
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

        // Connection Type and SSH Inputs
        if (typeList != null)
        {
            typeList.SelectedIndex = 0;
            typeList.SelectionChanged += (s, e) =>
            {
                if (_selectedProfile != null)
                {
                    _selectedProfile.Type = ConnectionType.Local;
                    if (sshPanel != null) sshPanel.IsVisible = false;
                }
            };
        }

        if (sshHostInput != null) sshHostInput.KeyUp += (s, e) => { if (_selectedProfile != null) _selectedProfile.SshHost = sshHostInput.Text ?? ""; };
        if (sshPortInput != null) sshPortInput.ValueChanged += (s, e) => { if (_selectedProfile != null && sshPortInput.Value.HasValue) _selectedProfile.SshPort = (int)sshPortInput.Value.Value; };
        if (sshUserInput != null) sshUserInput.KeyUp += (s, e) => { if (_selectedProfile != null) _selectedProfile.SshUser = sshUserInput.Text ?? ""; };

        // Advanced SSH Controls
        if (jumpList != null)
        {
            jumpList.SelectionChanged += (s, e) =>
            {
                if (_selectedProfile != null && jumpList.SelectedItem is ComboBoxItem item)
                {
                    if (item.Tag is Guid gid) _selectedProfile.JumpHostProfileId = gid;
                    else _selectedProfile.JumpHostProfileId = null;
                }
            };
        }

        if (radioAgent != null)
        {
            radioAgent.IsCheckedChanged += (s, e) =>
            {
                if (_selectedProfile == null) return;
                _selectedProfile.UseSshAgent = (radioAgent.IsChecked == true);

                if (sshKeyPathInput != null) sshKeyPathInput.IsEnabled = !_selectedProfile.UseSshAgent;
                if (btnBrowseSshKey != null) btnBrowseSshKey.IsEnabled = !_selectedProfile.UseSshAgent;
            };
        }

        if (btnBrowseSshKey != null)
        {
            btnBrowseSshKey.Click += async (s, e) =>
            {
                var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
                if (storage == null) return;

                var files = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = "Select Private Key",
                    AllowMultiple = false
                });

                if (files.Count > 0 && sshKeyPathInput != null)
                {
                    var path = files[0].Path.LocalPath;
                    sshKeyPathInput.Text = path;
                    if (_selectedProfile != null)
                    {
                        _selectedProfile.IdentityFilePath = path;
                        _selectedProfile.SshKeyPath = path; // Backward compat sync
                    }
                }
            };
        }
        if (sshKeyPathInput != null) sshKeyPathInput.KeyUp += (s, e) =>
        {
            if (_selectedProfile != null)
            {
                _selectedProfile.IdentityFilePath = sshKeyPathInput.Text ?? "";
                _selectedProfile.SshKeyPath = sshKeyPathInput.Text ?? ""; // Sync
            }
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

        if (btnAddRule != null) btnAddRule.Click += BtnAddForward_Click;

        _statusTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _statusTimer.Tick += (s, e) => RefreshForwardsList();
        _statusTimer.Start();
    }

    public SettingsPage Page => SettingsPage.Profiles;

    public Control View => this;

    private SettingsSession Session => _session ?? throw new InvalidOperationException("ProfilesPage used before Load.");

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

    public void Dispose()
    {
        _statusTimer?.Stop();
        _statusTimer = null;
        GC.SuppressFinalize(this);
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
    /// <c>_profilesList</c>. <see cref="ReloadSettingsAfterExternalChangeAsync"/> rebuilds
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

        var sshHostInput = this.FindControl<TextBox>("SshHostInput");
        if (sshHostInput != null) sshHostInput.Text = string.Empty;
        var sshPortInput = this.FindControl<NumericUpDown>("SshPortInput");
        if (sshPortInput != null) sshPortInput.Value = null;
        var sshUserInput = this.FindControl<TextBox>("SshUserInput");
        if (sshUserInput != null) sshUserInput.Text = string.Empty;
        var sshKeyPathInput = this.FindControl<TextBox>("SshKeyPathInput");
        if (sshKeyPathInput != null) sshKeyPathInput.Text = string.Empty;
        var sshPasswordInput = this.FindControl<TextBox>("SshPasswordInput");
        if (sshPasswordInput != null) sshPasswordInput.Text = string.Empty;

        var checkOverrideFont = this.FindControl<CheckBox>("CheckOverrideFont");
        if (checkOverrideFont != null) checkOverrideFont.IsChecked = false;
        var checkOverrideSize = this.FindControl<CheckBox>("CheckOverrideSize");
        if (checkOverrideSize != null) checkOverrideSize.IsChecked = false;
        var checkOverrideTheme = this.FindControl<CheckBox>("CheckOverrideTheme");
        if (checkOverrideTheme != null) checkOverrideTheme.IsChecked = false;
        var checkOverrideLigatures = this.FindControl<CheckBox>("CheckOverrideLigatures");
        if (checkOverrideLigatures != null) checkOverrideLigatures.IsChecked = false;
    }

    internal static System.Collections.Generic.List<TerminalProfile> BuildLocalProfilesForEditor(System.Collections.Generic.IEnumerable<TerminalProfile> profiles)
    {
        var source = profiles ?? System.Array.Empty<TerminalProfile>();

        return source
            .Where(profile => profile.Type == ConnectionType.Local)
            .Select(profile => new TerminalProfile
            {
                Id = profile.Id,
                Name = profile.Name,
                Command = profile.Command,
                Arguments = profile.Arguments,
                StartingDirectory = profile.StartingDirectory,
                Type = ConnectionType.Local,
                FontFamily = profile.FontFamily,
                FontSize = profile.FontSize,
                ThemeName = profile.ThemeName,
                EnableLigatures = profile.EnableLigatures,
                Group = profile.Group,
                Notes = profile.Notes,
                AccentColor = profile.AccentColor,
                Tags = profile.Tags?.ToList() ?? new System.Collections.Generic.List<string>()
            })
            .ToList();
    }

    internal static System.Collections.Generic.List<TerminalProfile> NormalizeSettingsProfilesForSave(System.Collections.Generic.IEnumerable<TerminalProfile> profiles)
    {
        var source = profiles ?? System.Array.Empty<TerminalProfile>();

        return source
            .Where(profile => profile.Type == ConnectionType.Local)
            .Select(profile => new TerminalProfile
            {
                Id = profile.Id,
                Name = profile.Name,
                Command = profile.Command,
                Arguments = profile.Arguments,
                StartingDirectory = profile.StartingDirectory,
                Type = ConnectionType.Local,
                FontFamily = profile.FontFamily,
                FontSize = profile.FontSize,
                ThemeName = profile.ThemeName,
                EnableLigatures = profile.EnableLigatures,
                Group = profile.Group,
                Notes = profile.Notes,
                AccentColor = profile.AccentColor,
                Tags = profile.Tags?.ToList() ?? new System.Collections.Generic.List<string>()
            })
            .ToList();
    }

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

        var typeList = this.FindControl<ComboBox>("ProfileTypeList");
        if (typeList != null) typeList.SelectedIndex = 0;

        var sshPanel = this.FindControl<StackPanel>("SshSettingsPanel");
        if (sshPanel != null) sshPanel.IsVisible = false;

        this.FindControl<TextBox>("SshHostInput")!.Text = profile.SshHost ?? "";
        this.FindControl<NumericUpDown>("SshPortInput")!.Value = profile.SshPort;
        this.FindControl<TextBox>("SshUserInput")!.Text = profile.SshUser ?? "";
        this.FindControl<TextBox>("SshKeyPathInput")!.Text = profile.SshKeyPath ?? "";
        this.FindControl<TextBox>("SshPasswordInput")!.Text = string.Empty;

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

        // SSH profile editing moved to Connection Manager.
    }

    private void PopulateJumpHostList(TerminalProfile current)
    {
        var combo = this.FindControl<ComboBox>("JumpHostList");
        if (combo == null) return;

        combo.Items.Clear();
        var noneItem = new ComboBoxItem { Content = "Direct Connection (None)" };
        combo.Items.Add(noneItem);
        combo.SelectedItem = noneItem;

        foreach (var p in _profilesList.Where(x => x.Type == ConnectionType.SSH && x.Id != current.Id))
        {
            var item = new ComboBoxItem { Content = p.Name, Tag = p.Id };
            combo.Items.Add(item);
            if (current.JumpHostProfileId == p.Id)
            {
                combo.SelectedItem = item;
            }
        }
    }

    private void RefreshForwardsList()
    {
        var panel = this.FindControl<StackPanel>("ForwardsList");
        if (panel == null || _selectedProfile == null) return;
        panel.Children.Clear();
        foreach (var f in _selectedProfile.Forwards)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto, *, Auto"), Margin = new Thickness(0, 2) };

            // Status Indicator
            bool isListening = CheckIfPortIsListening(f);
            var dot = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = isListening ? Brushes.LimeGreen : Brushes.Gray,
                Margin = new Thickness(5, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(dot, isListening ? "Active / Listening" : "Inactive");

            var txt = new TextBlock { Text = f.ToString(), VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            var btn = new Button
            {
                Content = "×",
                Classes = { "Danger" },
                Width = 24,
                Height = 24,
                Padding = new Thickness(0),
                Tag = f
            };
            btn.Click += BtnRemoveForward_Click;

            Grid.SetColumn(dot, 0);
            Grid.SetColumn(txt, 1);
            Grid.SetColumn(btn, 2);

            grid.Children.Add(dot);
            grid.Children.Add(txt);
            grid.Children.Add(btn);
            panel.Children.Add(grid);
        }
    }

    private static bool CheckIfPortIsListening(ForwardingRule rule)
    {
        try
        {
            // Dynamic (-D) or Local (-L) both listen on a local port
            if (rule.Type == ForwardingType.Remote) return false; // Remote forwards listen on the SERVER side

            string portStr = rule.LocalAddress;
            if (portStr.Contains(':')) portStr = portStr.Split(':').Last();
            if (!int.TryParse(portStr, out int port)) return false;

            var properties = IPGlobalProperties.GetIPGlobalProperties();
            var listeners = properties.GetActiveTcpListeners();
            return listeners.Any(l => l.Port == port);
        }
        catch { return false; }
    }

    private void BtnAddForward_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selectedProfile == null) return;

        var typeBox = this.FindControl<ComboBox>("RuleInputType");
        var localBox = this.FindControl<TextBox>("RuleInputLocal");
        var remoteBox = this.FindControl<TextBox>("RuleInputRemote");

        if (string.IsNullOrWhiteSpace(localBox?.Text)) return;

        var rule = new ForwardingRule
        {
            Type = (ForwardingType)(typeBox?.SelectedIndex ?? 0),
            LocalAddress = localBox.Text.Trim(),
            RemoteAddress = remoteBox?.Text?.Trim() ?? ""
        };

        _selectedProfile.Forwards.Add(rule);
        RefreshForwardsList();

        // Clear inputs
        localBox.Text = "";
        if (remoteBox != null) remoteBox.Text = "";
    }

    private void BtnRemoveForward_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selectedProfile == null || sender is not Button btn || btn.Tag is not ForwardingRule rule) return;
        _selectedProfile.Forwards.Remove(rule);
        RefreshForwardsList();
    }
}
