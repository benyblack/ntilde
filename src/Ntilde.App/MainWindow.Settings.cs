using Avalonia.Controls;
using Ntilde.Shell;

using Ntilde.Controls;
using Ntilde.Settings;

namespace Ntilde
{
    /// <summary>
    /// Opening the settings window and applying what it saves: to every pane, to session persistence, and to
    /// the window.
    /// </summary>
    public partial class MainWindow
    {
        public void ApplySettingsRecursive(Control? control, TerminalSettings settings)
        {
            if (control == null) return;
            if (control is TerminalPane pane)
            {
                // Refresh the profile object if possible to pick up overrides
                if (pane.Profile != null)
                {
                    TerminalProfile? updatedProfile = pane.Profile.Type == ConnectionType.SSH
                        ? _sshConnectionService.GetConnectionProfile(pane.Profile.Id)
                        : settings.Profiles.Find(p => p.Id == pane.Profile.Id);
                    if (updatedProfile != null) pane.UpdateProfile(updatedProfile);
                }
                pane.ApplySettings(settings);
            }
            else if (control is Panel panel)
            {
                foreach (var child in panel.Children)
                {
                    if (child is Control c)
                    {
                        ApplySettingsRecursive(c, settings);
                    }
                }
            }
            else if (control is ContentControl cc)
            {
                ApplySettingsRecursive(cc.Content as Control, settings);
            }
            else if (control is Decorator decorator)
            {
                // Border/ScrollViewer-style single-child wrappers: without this, a pane nested
                // behind one was silently skipped and never received theme or font updates.
                ApplySettingsRecursive(decorator.Child as Control, settings);
            }
        }

        private void ApplySettingsToAllTabs()
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs != null)
            {
                foreach (TabItem ti in tabs.Items.Cast<TabItem>())
                {
                    ApplySettingsRecursive(ti.Content as Control, _settings);
                }
            }
        }

        /// <summary>
        /// Takes every pane's assist surface down. Called after Settings clears command history.
        /// </summary>
        /// <remarks>
        /// <strong>PR #293 review, non-blocking 7.</strong> The rows on screen are a snapshot of a ranking
        /// pass, and nothing invalidates them when the store underneath is emptied - so a user who cleared
        /// their history while a bubble or popup was up went on looking at the commands they had just
        /// deleted, and could still accept one. Dismissing is the right verb rather than re-ranking: the
        /// honest answer for a just-emptied store is "nothing", and the next keystroke rebuilds the
        /// surface from the empty store anyway.
        /// </remarks>
        private void DismissCommandAssistSurfaces()
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null)
            {
                return;
            }

            foreach (TabItem ti in tabs.Items.Cast<TabItem>())
            {
                foreach (var pane in EnumeratePanes(ti.Content as Control))
                {
                    pane.DismissCommandAssistSurface();
                }
            }
        }

        /// <summary>The Settings page on screen when Settings last closed, this run only. Ctrl+, reopens there.</summary>
        private SettingsPage? _lastSettingsPage;

        /// <summary>Customize Title Bar… opens the page the title-bar actions live on.</summary>
        internal static SettingsTarget CustomizeTitleBarSettingsTarget()
            => new(SettingsPage.TabsAndTitleBar);

        /// <summary>Where "Manage Profiles…" opens Settings.</summary>
        internal static SettingsTarget ManageProfilesSettingsTarget() => new(SettingsPage.Profiles);

        /// <summary>Where the three Backup palette entries open Settings.</summary>
        internal static SettingsTarget BackupSettingsTarget() => new(SettingsPage.Backup);

        /// <summary>Where Ctrl+, the palette's "Settings" and the title-bar button open Settings: the page last shown in this run, else the default.</summary>
        internal SettingsTarget DefaultSettingsTarget() => new(_lastSettingsPage ?? SettingsPageRegistry.DefaultPage);

        /// <summary>
        /// Opens Settings on the Backup &amp; Restore page. Routes through <see cref="OpenSettings"/> -
        /// this window's one construction site - rather than constructing a <see cref="SettingsWindow"/>
        /// directly, so Backup gets the same save/legacy-migration handling as every other entry
        /// point. The page is named by BackupSettingsTarget, not an index.
        /// </summary>
        private void OpenSettingsToBackupPage()
        {
            _ = OpenSettings(BackupSettingsTarget());
        }

        private async Task OpenSettings(SettingsTarget target)
        {
            var sw = new SettingsWindow(target);

            // The one live history store, so Settings' "Clear history" acts on the same instance the
            // panes append to (V2 Phase 3b task 5). Reading the property constructs it lazily, which is
            // acceptable here: the user is opening a settings dialog, not on the startup path.
            //
            // Assigned after construction rather than passed to the constructor, and that is safe for a
            // specific reason worth writing down (PR #293 review, non-blocking 8): the window's clear
            // handler reads this property when the button is *clicked*, not when the row is wired. Wiring
            // happens during construction, above this line; the read cannot happen before the dialog is
            // shown, which is after it. A null store is still handled - the row reports "not available in
            // this window" - so a future caller that constructs a SettingsWindow without injecting one
            // degrades visibly instead of throwing.
            sw.CommandAssistHistoryStore = _commandAssistServices.HistoryStore;

            // N7: clearing history deletes the rows any open assist surface is currently showing, and
            // those surfaces do not watch the store. Refreshed from here because MainWindow is what can
            // see the panes.
            sw.OnCommandAssistHistoryCleared += DismissCommandAssistSurfaces;

            // The same injection, for the same reason, for the snippet manager (V2 Phase 4b): one
            // live JsonSnippetStore, so Settings and the panes' pin action write through the same
            // instance rather than two views of one file. Editing or deleting a snippet also invalidates
            // whatever an open assist surface is showing, so it takes the same refresh.
            sw.CommandAssistSnippetStore = _commandAssistServices.SnippetStore;
            sw.OnCommandAssistSnippetsChanged += DismissCommandAssistSurfaces;

            // Snapshot the live-previewed values so Cancel can restore them (#167).
            // The preview handlers below mutate _settings directly; without this,
            // closing the dialog without saving left the preview values live, and any
            // later unrelated Save() persisted them to disk.
            var previewSnapshot = new PreviewSnapshot(
                _settings.WindowOpacity,
                _settings.BlurEffect,
                _settings.BackgroundImagePath,
                _settings.BackgroundImageOpacity,
                _settings.BackgroundImageStretch,
                _settings.FontFamily,
                _settings.FontSize,
                _settings.ThemeName,
                _settings.UiScale);

            // Wire up live preview events
            sw.OnOpacityChanged += (val) => { _settings.WindowOpacity = val; ApplyThemeToUI(); ApplySettingsToAllTabs(); };
            sw.OnBlurChanged += (val) => { _settings.BlurEffect = val; UpdateTransparencyHints(); };
            sw.OnBgImageChanged += (path, opacity, stretch) =>
            {
                _settings.BackgroundImagePath = path;
                _settings.BackgroundImageOpacity = opacity;
                _settings.BackgroundImageStretch = stretch;
                ApplyThemeToUI();
                ApplySettingsToAllTabs();
            };
            sw.OnFontChanged += (font) => { _settings.FontFamily = font; ApplySettingsToAllTabs(); };
            sw.OnFontSizeChanged += (size) => { _settings.FontSize = size; ApplySettingsToAllTabs(); };
            sw.OnUiScaleChanged += (scale) => { _settings.UiScale = scale; UiScale.Apply(scale); };
            sw.OnThemeChanged += (theme) =>
            {
                _settings.ThemeName = theme;
                // Force reload themes to pick up any changes from settings window.
                _settings.ThemeManager.ReloadThemes();
                _settings.RefreshActiveTheme();
                ApplyThemeToUI();
                ApplySettingsToAllTabs();
                UpdateTabVisuals();
            };

            // "Quit and close all shells…" (Task 17): Settings closes without saving, then the quit runs.
            sw.SessionPersistenceActive = IsMuxPersistenceActive;

            bool saved = await sw.ShowDialog<bool>(this);

            ApplySettingsWindowResult(sw, saved, previewSnapshot);

            if (sw.QuitAndCloseAllRequested) await QuitAndCloseAllShellsAsync();
        }

        /// <summary>
        /// F1: a successful Import or Restore run from the Backup page changes settings.json (and
        /// possibly more) on disk while this dialog is still open (see
        /// <c>SettingsWindow.ReloadSettingsAfterExternalChangeAsync</c>). Before this fix, only the
        /// <paramref name="saved"/> == true branch below adopted that change; closing the dialog any
        /// other way - Cancel, or the window's X, both of which surface here as
        /// <paramref name="saved"/> == false - left <c>_settings</c> pointing at the stale
        /// PRE-import object. <c>_settings.Save()</c> runs from roughly ten ordinary places in this
        /// class (e.g. the "Font: Increase" / "Font: Decrease" palette commands), so the very next
        /// one of those silently overwrote the just-imported configuration on disk.
        ///
        /// <see cref="SettingsWindow.ConfigurationReplacedExternally"/> is the signal: when it is
        /// set, this method takes the <paramref name="saved"/> == true path's handling regardless of
        /// what <paramref name="saved"/> actually is, so both branches leave <c>MainWindow</c> in
        /// equivalent state. The plain-Cancel path (no import/restore happened) is unchanged: it
        /// still reverts the live-previewed fields captured in <paramref name="previewSnapshot"/>.
        ///
        /// Pulled out of <see cref="OpenSettings"/> as its own method purely for testability -
        /// <c>OpenSettings</c> itself reaches a real <c>Window.ShowDialog</c>, which this repo's
        /// headless test host cannot return from. A test can build a <see cref="SettingsWindow"/>,
        /// await its internal <c>ReloadSettingsAfterExternalChangeAsync</c> (as
        /// <c>SettingsWindowBackupSectionTests</c> does), then call this method directly with
        /// <paramref name="saved"/> = false to simulate Cancel/X after a successful import.
        /// </summary>
        internal void ApplySettingsWindowResult(SettingsWindow sw, bool saved, PreviewSnapshot previewSnapshot)
        {
            // Ctrl+, reopens where the user left off, whichever way the dialog closed.
            _lastSettingsPage = sw.CurrentPage;

            if (saved || sw.ConfigurationReplacedExternally)
            {
                // Use the settings object directly from the dialog to avoid disk I/O race conditions
                if (sw.Settings != null)
                {
                    _settings = sw.Settings;
                }
                else
                {
                    _settings = TerminalSettings.Load();
                }

                if (_sshLegacyMigrationService.MigrateLegacyProfiles(_settings))
                {
                    _settings.Save();
                }

                ApplySessionPersistenceSetting();
                RefreshProfileUIs();
                ApplyThemeToUI();
                ApplySettingsToAllTabs();
                UiScale.Apply(_settings.UiScale);
                RebuildTitleBar();
                ApplyTabLayout();
                UpdateTransparencyHints();
                ApplyAgentHostSettingsLive();
                // A toggled-off Osc9ProgressReportingEnabled must clear a taskbar state
                // set before the change (and vice versa).
                RefreshTaskbarProgressFromSelection();

                // Refresh Connection Manager if open
                _connectionManagerWindow?.LoadProfiles(_sshConnectionService.GetConnectionProfiles());
            }
            else
            {
                // Cancel: revert the live preview (#167).
                _settings.WindowOpacity = previewSnapshot.WindowOpacity;
                _settings.BlurEffect = previewSnapshot.BlurEffect;
                _settings.BackgroundImagePath = previewSnapshot.BackgroundImagePath;
                _settings.BackgroundImageOpacity = previewSnapshot.BackgroundImageOpacity;
                _settings.BackgroundImageStretch = previewSnapshot.BackgroundImageStretch;
                _settings.FontFamily = previewSnapshot.FontFamily;
                _settings.FontSize = previewSnapshot.FontSize;
                _settings.ThemeName = previewSnapshot.ThemeName;
                _settings.UiScale = previewSnapshot.UiScale;
                _settings.RefreshActiveTheme();
                UiScale.Apply(_settings.UiScale);

                ApplyThemeToUI();
                ApplySettingsToAllTabs();
                UpdateTransparencyHints();
                UpdateTabVisuals();
                ApplyTabLayout();
            }
        }

        /// <summary>
        /// The factory follows a saved SessionPersistence flip (spec §9). Compared against the
        /// factory in use, not the previous settings object, which the dialog may have edited in
        /// place. Off→on builds (or reuses) the host and warms it; on→off only swaps the factory:
        /// the host stays alive for the mux panes already open. Either way every pane is re-wired,
        /// which only changes what its Reconnect creates.
        /// </summary>
        private void ApplySessionPersistenceSetting()
        {
            bool wantPersistent = Ntilde.Shell.Mux.SessionPersistenceMode.IsKeepOnClose(_settings.SessionPersistence);
            bool isPersistent = _sessionFactory is Ntilde.Shell.Mux.MuxTerminalSessionFactory;
            if (wantPersistent == isPersistent) return;

            if (wantPersistent)
            {
                _sessionFactory = CreatePersistentSessionFactory();
                if (_sessionFactory is not Ntilde.Shell.Mux.MuxTerminalSessionFactory) return; // creation failed and was logged: nothing changed
                _muxHosts!.Local.WarmUp();
            }
            else
            {
                _sessionFactory = Ntilde.Shell.DefaultTerminalSessionFactory.Instance;
            }

            foreach (var pane in _paneOwnerTab.Keys.ToList())
            {
                WirePane(pane);
            }
        }

        /// <summary>
        /// The live-previewed fields <see cref="OpenSettings"/> snapshots before showing the dialog
        /// (#167), so a plain Cancel can revert them. A named type rather than the anonymous type
        /// this used to be, so <see cref="ApplySettingsWindowResult"/> can take it as a parameter and
        /// be called from a test without going through <c>OpenSettings</c> itself.
        /// </summary>
        internal readonly record struct PreviewSnapshot(
            double WindowOpacity,
            string BlurEffect,
            string BackgroundImagePath,
            double BackgroundImageOpacity,
            string BackgroundImageStretch,
            string FontFamily,
            double FontSize,
            string ThemeName,
            double UiScale);
    }
}
