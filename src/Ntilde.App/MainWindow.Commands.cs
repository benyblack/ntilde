using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Ntilde.Shell;
using Ntilde.VT;
using Ntilde.Pty;

using Ntilde.Controls;
using Ntilde.Shell.Shortcuts;
using Ntilde.Shell.TitleBar;

namespace Ntilde
{
    /// <summary>
    /// The command palette and keyboard shortcuts: building the palette's commands, running them, and
    /// recording their use.
    /// </summary>
    public partial class MainWindow
    {
        private readonly CommandPaletteUsageStore _commandPaletteUsageStore;
        private Dictionary<string, CommandPaletteUsageEntry> _commandPaletteUsage = new(StringComparer.OrdinalIgnoreCase);

        private bool IsShortcut(KeyEventArgs e, string id, string fallback)
        {
            return ShortcutMatcher.Matches(e, GetEffectiveShortcutBinding(id, fallback));
        }

        private string GetEffectiveShortcutBinding(string id, string fallback)
        {
            if (_settings.Keybindings != null &&
                _settings.Keybindings.TryGetValue(id, out var custom) &&
                !string.IsNullOrWhiteSpace(custom))
            {
                return custom;
            }

            // Fallbacks are written with Ctrl; on macOS the default is the Cmd form.
            return ShortcutPlatform.DefaultBinding(id, fallback);
        }

        internal static bool TryOpenCommandAssistHelp(TerminalPane? pane)
        {
            return pane?.OpenCommandAssistHelp() == true;
        }

        private void SetupCommandPalette()
        {
            CommandRegistry.Clear();

            // 1. Register Default Commands
            CommandRegistry.Register("New Tab", "General", () => AddTab(), GetEffectiveShortcutBinding("new_tab", "Ctrl+Shift+T"), "new_tab");

            // "Check for updates" is always registered, even on a portable-zip or dev run: its
            // handler calls EnsureUpdateCoordinator() before checking, and answering "this build
            // cannot update itself" is what makes the entry meaningful there - a dev run should
            // never simply have it missing. That also means it works within the first 10 seconds
            // of the process's life, before the deferred startup timer would otherwise build the
            // coordinator itself. The restart entry is different: it only makes sense once
            // something is actually staged, and SetupCommandPalette()'s laziness (it runs on
            // palette-open and settings-save) is what lets this reflect the live staged state
            // rather than a value latched at startup.
            if (_updateCoordinator is { IsUpdateStaged: true })
            {
                CommandRegistry.Register(
                    $"Update: Restart to apply {_updateCoordinator.StagedVersion}",
                    "Application",
                    () => ApplyStagedUpdate(),
                    "");
            }

            CommandRegistry.Register("Update: Check for updates", "Application", () =>
            {
                _ = RunManualCheckSafeAsync();
            }, "");

            // Dynamic Profile Tabs
            if (_settings.Profiles != null)
            {
                foreach (var profile in _settings.Profiles.Where(p => p.Type == ConnectionType.Local))
                {
                    // Hidden only if the command belongs to another OS. Keyed off existence, this
                    // dropped profiles the terminal can open perfectly well - a quoted path, or a
                    // command with inline arguments - so they were missing from the palette while
                    // still working from a restored session.
                    if (ShellHelper.IsCommandForAnotherPlatform(profile.Command)) continue;
                    CommandRegistry.Register($"New Tab: {profile.Name}", "Shell", () => AddTab(profile), "");
                }
            }

            if (_sshConnectionService != null)
            {
                foreach (var profile in _sshConnectionService.GetConnectionProfiles())
                {
                    var capturedProfile = profile;
                    CommandRegistry.Register($"New Connection (SSH): {profile.Name}", "SSH", () => AddTab(capturedProfile), "");
                }
            }

            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs != null)
            {
                foreach (var tab in tabs.Items.Cast<TabItem>())
                {
                    var capturedTab = tab;
                    string label = GetTabSwitchCommandLabel(capturedTab);
                    CommandRegistry.Register(label, "Tabs", () =>
                    {
                        tabs.SelectedItem = capturedTab;
                    }, "");
                }
            }

            CommandRegistry.Register("Workspace: Save Current", "Workspace", () => _ = SaveWorkspaceInteractiveAsync(), "");
            CommandRegistry.Register("Workspace: Load...", "Workspace", () => _ = LoadWorkspaceInteractiveAsync(), "");
            CommandRegistry.Register("Workspace Template: Save Current", "Workspace", () => _ = SaveWorkspaceTemplateInteractiveAsync(), "");
            CommandRegistry.Register("Workspace Template: Apply...", "Workspace", () => _ = LoadWorkspaceTemplateInteractiveAsync(), "");
            CommandRegistry.Register("Tab Rule: Set Template for Current Profile...", "Workspace", () => _ = SetTemplateRuleForCurrentPaneProfileAsync(), "");
            CommandRegistry.Register("Tab Rule: Clear Template for Current Profile", "Workspace", () => ClearTemplateRuleForCurrentPaneProfile(), "");
            var workspacePolicy = WorkspacePolicyManager.Current;
            if (workspacePolicy.AllowWorkspaceBundleExport)
            {
                CommandRegistry.Register("Workspace: Export Bundle...", "Workspace", () => _ = ExportWorkspaceBundleInteractiveAsync(), "");
                CommandRegistry.Register("Workspace: Export Current Session Bundle...", "Workspace", () => _ = ExportCurrentSessionBundleInteractiveAsync(), "");
            }
            if (workspacePolicy.AllowWorkspaceBundleImport)
            {
                CommandRegistry.Register("Workspace: Import Bundle...", "Workspace", () => _ = ImportWorkspaceBundleInteractiveAsync(), "");
                CommandRegistry.Register("Workspace: Open Bundle...", "Workspace", () => _ = OpenWorkspaceBundleInteractiveAsync(), "");
            }
            foreach (var workspaceName in WorkspaceManager.ListWorkspaceNames())
            {
                string capturedName = workspaceName;
                CommandRegistry.Register($"Workspace: Load {capturedName}", "Workspace", () => LoadWorkspaceByName(capturedName), "");
            }
            foreach (var templateName in WorkspaceManager.ListWorkspaceTemplateNames())
            {
                string capturedTemplate = templateName;
                CommandRegistry.Register($"Workspace Template: Apply {capturedTemplate}", "Workspace", () => ApplyWorkspaceTemplateByName(capturedTemplate), "");
            }

            CommandRegistry.Register("Close Tab", "General", () => CloseActiveTab(), GetEffectiveShortcutBinding("close_tab", "Ctrl+W"), "close_tab");
            CommandRegistry.Register("Close Pane", "General", () => CloseActivePane(), GetEffectiveShortcutBinding("close_pane", "Ctrl+Shift+W"), "close_pane");
            CommandRegistry.Register("Tab: Next (MRU)", "General", () => SwitchTabByMru(reverse: false), GetEffectiveShortcutBinding("next_tab", "Ctrl+Tab"), "next_tab");
            CommandRegistry.Register("Tab: Previous (MRU)", "General", () => SwitchTabByMru(reverse: true), GetEffectiveShortcutBinding("prev_tab", "Ctrl+Shift+Tab"), "prev_tab");
            CommandRegistry.Register("Tab: Move Previous", "General", () => MoveSelectedTab(-1), GetEffectiveShortcutBinding(MoveTabPrevCommandId, "Ctrl+Shift+PageUp"), MoveTabPrevCommandId);
            CommandRegistry.Register("Tab: Move Next", "General", () => MoveSelectedTab(1), GetEffectiveShortcutBinding(MoveTabNextCommandId, "Ctrl+Shift+PageDown"), MoveTabNextCommandId);
            CommandRegistry.Register("Tab: Open Tab List", "General", () => PopulateTabListMenu(showFlyout: true), GetEffectiveShortcutBinding(TitleBarCatalog.OpenTabListId, "Ctrl+Shift+O"), TitleBarCatalog.OpenTabListId);
            CommandRegistry.Register("Tabs: Toggle Vertical Tab Sidebar", "General", () => ToggleTabOrientation(), GetEffectiveShortcutBinding("toggle_tab_orientation", "Ctrl+Shift+L"), "toggle_tab_orientation");
            CommandRegistry.Register("Tab: Rename Current", "General", () => _ = RenameSelectedTabAsync(), "");
            CommandRegistry.Register("Tab: Copy Current Title", "General", () => _ = CopySelectedTabTitleAsync(), "");
            CommandRegistry.Register("Tab: Close Others", "General", () => _ = CloseOtherTabsAsync(), "");
            CommandRegistry.Register("Tab: Toggle Pin", "General", () => TogglePinSelectedTab(), "");
            CommandRegistry.Register("Tab: Toggle Protect", "General", () => ToggleProtectSelectedTab(), "");
            // Keep command naming aligned with common terminal UX:
            // Vertical split => vertical divider => side-by-side panes.
            CommandRegistry.Register("Split Vertical", "View", () => SplitPane(Avalonia.Layout.Orientation.Horizontal), GetEffectiveShortcutBinding("split_vertical", "Ctrl+Shift+D"), "split_vertical");
            // Horizontal split => horizontal divider => stacked panes.
            CommandRegistry.Register("Split Horizontal", "View", () => SplitPane(Avalonia.Layout.Orientation.Vertical), GetEffectiveShortcutBinding("split_horizontal", "Ctrl+Shift+E"), "split_horizontal");
            CommandRegistry.Register("Equalize Panes", "View", () => EqualizeCurrentSplit(), GetEffectiveShortcutBinding("equalize_panes", "Ctrl+Shift+G"), "equalize_panes");
            CommandRegistry.Register("Pane: Toggle Zoom", "View", () => TogglePaneZoomForCurrentTab(), GetEffectiveShortcutBinding("toggle_pane_zoom", "Ctrl+Shift+Z"), "toggle_pane_zoom");
            CommandRegistry.Register("Pane: Toggle Broadcast Input (Tab)", "View", () => ToggleBroadcastForCurrentTab(), GetEffectiveShortcutBinding("toggle_broadcast_input", "Ctrl+Shift+B"), "toggle_broadcast_input");
            CommandRegistry.Register("Pane: Reconnect", "View", () => _currentPane?.Reconnect(), "");
            if (IsMuxPersistenceActive)
            {
                CommandRegistry.Register("Session: Attach to Session\u2026", "General", () => _ = AttachToMuxSessionAsync(), GetEffectiveShortcutBinding(ShortcutCatalog.AttachSessionId, ""), ShortcutCatalog.AttachSessionId);
                CommandRegistry.Register("Pane: Detach", "View", () => DetachActivePane(), GetEffectiveShortcutBinding(ShortcutCatalog.DetachPaneId, ""), ShortcutCatalog.DetachPaneId);
                CommandRegistry.Register("Session: Quit and Close All Shells", "General", () => _ = QuitAndCloseAllShellsAsync(), GetEffectiveShortcutBinding(ShortcutCatalog.QuitAndCloseAllShellsId, ""), ShortcutCatalog.QuitAndCloseAllShellsId);
            }
            CommandRegistry.Register("Focus Pane Left", "View", () => NavigatePane(MoveDirection.Left), "Alt+Left");
            CommandRegistry.Register("Focus Pane Right", "View", () => NavigatePane(MoveDirection.Right), "Alt+Right");
            CommandRegistry.Register("Focus Pane Up", "View", () => NavigatePane(MoveDirection.Up), "Alt+Up");
            CommandRegistry.Register("Focus Pane Down", "View", () => NavigatePane(MoveDirection.Down), "Alt+Down");
            CommandRegistry.Register("Find in Terminal", "Edit", () => _currentPane?.ToggleSearch(), GetEffectiveShortcutBinding("find", "Ctrl+F"), "find");
            CommandRegistry.Register("Pane: Export Snapshot (Plain Text)", "View", () => _currentPane?.ExportSnapshotAsync("txt"), "");
            CommandRegistry.Register("Pane: Export Snapshot (ANSI)", "View", () => _currentPane?.ExportSnapshotAsync("ansi"), "");
            CommandRegistry.Register("Pane: Export Snapshot (PNG)", "View", () => _currentPane?.ExportSnapshotAsync("png"), "");
            CommandRegistry.Register("Pane: Toggle Render HUD", "View", () => _currentPane?.ToggleRenderHud(), "");
            CommandRegistry.Register("Paste", "Edit", () => _ = PasteFromClipboardAsync(), GetEffectiveShortcutBinding("paste", "Ctrl+V"), "paste");
            CommandRegistry.Register("Font: Increase", "View", () => { _settings.FontSize++; ApplySettingsToAllTabs(); _settings.Save(); }, GetEffectiveShortcutBinding("font_increase", "Ctrl++"), "font_increase");
            CommandRegistry.Register("Font: Decrease", "View", () => { _settings.FontSize = Math.Max(6, _settings.FontSize - 1); ApplySettingsToAllTabs(); _settings.Save(); }, GetEffectiveShortcutBinding("font_decrease", "Ctrl+-"), "font_decrease");
            CommandRegistry.Register("Settings", "General", async () =>
            {
                await OpenSettings(DefaultSettingsTarget());
            }, GetEffectiveShortcutBinding("settings", "Ctrl+,"), "settings");
            CommandRegistry.Register("Connections", "General", () => ToggleConnections(), GetEffectiveShortcutBinding("connections", "Ctrl+Shift+K"), "connections");
            CommandRegistry.Register("Toggle Recording", "General", () => _currentPane?.ToggleRecording(), GetEffectiveShortcutBinding("toggle_recording", "Ctrl+Shift+R"), "toggle_recording");
            CommandRegistry.Register(
                "Command Assist: Pin/Unpin Selection",
                "General",
                () => _currentPane?.TryToggleCommandAssistPinShortcut(),
                GetEffectiveShortcutBinding("command_assist_pin", "Ctrl+Shift+S"),
                "command_assist_pin");
            CommandRegistry.Register("Open Recording...", "General", () => _ = ExecuteUiCommandAsync(ExecuteOpenRecordingCommandAsync, "Open Recording..."), "");
            CommandRegistry.Register("Open Recordings Folder", "General", () => OpenRecordingsFolder(), "");

            // All three route to the same Backup & Restore settings page rather than acting
            // directly from the palette. Export and import both need a file picker plus a
            // merge/replace mode prompt, and Restore needs the "this replaces your current
            // config" confirmation - all of which already live on that page. Duplicating any of
            // that into the palette would create a second copy of the destructive-action
            // confirmation, which is exactly the part that must not drift between two call sites.
            // Method-group, not a lambda wrapper: keeps the delegate's Method identity as
            // OpenSettingsToBackupPage itself, so a test can assert what these route to via
            // reflection without ever invoking through to the real (headlessly-hanging) ShowDialog.
            CommandRegistry.Register(
                "Export configuration\u2026",
                "Backup",
                OpenSettingsToBackupPage,
                id: "backup.export");

            CommandRegistry.Register(
                "Import configuration\u2026",
                "Backup",
                OpenSettingsToBackupPage,
                id: "backup.import");

            CommandRegistry.Register(
                "Restore from snapshot\u2026",
                "Backup",
                OpenSettingsToBackupPage,
                id: "backup.restore");

            // SFTP Actions
            CommandRegistry.Register("SFTP: Toggle Remote Files", "Remote", () => _currentPane?.ToggleRemoteFilesSidebar(), "");
            CommandRegistry.Register("SFTP: Upload File...", "Remote", () => _ = InitiateSftpTransfer(null, TransferDirection.Upload, TransferKind.File), "");
            CommandRegistry.Register("SFTP: Upload Folder...", "Remote", () => _ = InitiateSftpTransfer(null, TransferDirection.Upload, TransferKind.Folder), "");
            CommandRegistry.Register("SFTP: Download File...", "Remote", () => _ = InitiateSftpTransfer(null, TransferDirection.Download, TransferKind.File), "");
            CommandRegistry.Register("SFTP: Download Folder...", "Remote", () => _ = InitiateSftpTransfer(null, TransferDirection.Download, TransferKind.Folder), "");
            CommandRegistry.Register("SFTP: Show Transfers", "Remote", () => ToggleTransferCenter(), "");

            // Themes: one entry per installed theme (built-in + imported), so the
            // palette never drifts from what the Settings theme list offers.
            foreach (var themeName in _settings.ThemeManager.GetAvailableThemes()
                         .OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
            {
                CommandRegistry.Register(
                    $"Theme: {themeName}",
                    "Theme",
                    () =>
                    {
                        _settings.ThemeName = themeName;
                        _settings.RefreshActiveTheme();
                        ApplyThemeToUI();
                        ApplySettingsToAllTabs();
                        _settings.Save();
                    },
                    "",
                    $"theme:{themeName}");
            }

            // Cursor UX
            CommandRegistry.Register("Cursor: Block", "View", () => { _settings.CursorStyle = "Block"; ApplySettingsToAllTabs(); _settings.Save(); }, "");
            CommandRegistry.Register("Cursor: Beam", "View", () => { _settings.CursorStyle = "Beam"; ApplySettingsToAllTabs(); _settings.Save(); }, "");
            CommandRegistry.Register("Cursor: Underline", "View", () => { _settings.CursorStyle = "Underline"; ApplySettingsToAllTabs(); _settings.Save(); }, "");
            CommandRegistry.Register("Cursor: Toggle Blink", "View", () => { _settings.CursorBlink = !_settings.CursorBlink; ApplySettingsToAllTabs(); _settings.Save(); }, "");

            // Bell UX
            CommandRegistry.Register("Bell: Toggle Audio", "View", () => { _settings.BellAudioEnabled = !_settings.BellAudioEnabled; ApplySettingsToAllTabs(); _settings.Save(); }, "");
            CommandRegistry.Register("Bell: Toggle Visual Flash", "View", () => { _settings.BellVisualEnabled = !_settings.BellVisualEnabled; ApplySettingsToAllTabs(); _settings.Save(); }, "");

            // Scrolling UX
            CommandRegistry.Register("Scroll: Toggle Smooth", "View", () => { _settings.SmoothScrolling = !_settings.SmoothScrolling; ApplySettingsToAllTabs(); _settings.Save(); }, "");
            CommandRegistry.Register("Debug: Box Drawing Test Screen", "Debug", () => ShowBoxDrawingTestScreen(), "");

            // Keep toolbar tooltips in sync with the (possibly rebound) shortcuts.
            UpdateShortcutTooltips();
        }

        private void UpdateShortcutTooltips()
        {
            var btnConnections = FindTitleBarButton("connections");
            if (btnConnections != null)
            {
                ToolTip.SetTip(btnConnections, $"Connections ({GetEffectiveShortcutBinding("connections", "Ctrl+Shift+K")})");
            }

            // The record button's tooltip embeds the recording shortcut and is
            // produced by UpdateRecordButtonUi; re-sync it to pick up rebindings.
            SyncRecordingButtonState();
        }

        private void ShowBoxDrawingTestScreen()
        {
            var pane = _currentPane;
            var buffer = pane?.Buffer;
            if (buffer == null) return;

            int cols = Math.Max(20, buffer.Cols);
            int inner = Math.Max(2, cols - 2);
            string horizontal = new string('\u2500', inner);
            string middle = "\u2502" + new string('.', inner) + "\u2502";

            var ruler = new System.Text.StringBuilder(cols);
            for (int i = 1; i <= cols; i++)
            {
                ruler.Append((char)('0' + (i % 10)));
            }

            var screen = new System.Text.StringBuilder();
            screen.AppendLine("[Ntilde] Box Drawing Repro");
            screen.AppendLine(ruler.ToString());
            screen.AppendLine("\u250C" + horizontal + "\u2510");
            screen.AppendLine(middle);
            screen.AppendLine("\u2514" + horizontal + "\u2518");
            screen.AppendLine("\u253C\u253C\u253C\u253C\u253C  \u2502\u2502\u2502\u2502\u2502  \u2500\u2500\u2500\u2500\u2500");

            buffer.Clear(resetCursor: true);
            buffer.SetCursorPosition(0, 0);
            buffer.WriteContent(screen.ToString(), false);
        }

        private void InitializeCommandPaletteUI()
        {
            // 2. Wire UI (Wires only ONCE)
            var overlay = this.FindControl<Grid>("CommandPaletteOverlay");
            var box = this.FindControl<TextBox>("CommandSearchBox");
            var list = this.FindControl<ListBox>("CommandList");

            if (overlay != null)
            {
                overlay.PointerPressed += (s, e) =>
                {
                    // If we clicked the background overlay itself (not the border dialog), close it
                    if (e.Source == overlay) ToggleCommandPalette();
                };
            }

            if (box != null && list != null)
            {
                box.KeyUp += (s, e) =>
                {
                    if (e.Key == Key.Down)
                    {
                        list.SelectedIndex = Math.Min(list.ItemCount - 1, list.SelectedIndex + 1);
                        list.ScrollIntoView(list.SelectedIndex);
                    }
                    else if (e.Key == Key.Up)
                    {
                        list.SelectedIndex = Math.Max(0, list.SelectedIndex - 1);
                        list.ScrollIntoView(list.SelectedIndex);
                    }
                    else if (e.Key == Key.Enter)
                    {
                        if (list.SelectedItem is TerminalCommand cmd)
                        {
                            ExecuteCommand(cmd);
                        }
                    }
                    else if (e.Key == Key.Escape)
                    {
                        ToggleCommandPalette();
                    }
                    else
                    {
                        var results = GetPaletteCommands(box.Text ?? "");
                        list.ItemsSource = results;
                        if (results.Count > 0) list.SelectedIndex = 0;
                    }
                };

                // Filter on text changed too for smoother feel
                box.PropertyChanged += (s, e) =>
                {
                    if (e.Property.Name == "Text")
                    {
                        var results = GetPaletteCommands(box.Text ?? "");
                        list.ItemsSource = results;
                        if (results.Count > 0) list.SelectedIndex = 0;
                    }
                };
            }

            if (list != null)
            {
                list.DoubleTapped += (s, e) =>
                {
                    if (list.SelectedItem is TerminalCommand cmd) ExecuteCommand(cmd);
                };
            }
        }

        private void ToggleCommandPalette()
        {
            var overlay = this.FindControl<Grid>("CommandPaletteOverlay");
            var box = this.FindControl<TextBox>("CommandSearchBox");
            var list = this.FindControl<ListBox>("CommandList");

            if (overlay == null) return;

            bool isVisible = overlay.IsVisible;
            overlay.IsVisible = !isVisible;

            if (!isVisible)
            {
                // Opening
                if (box != null && list != null)
                {
                    SetupCommandPalette();
                    box.Text = "";
                    list.ItemsSource = GetPaletteCommands("");
                    list.SelectedIndex = 0;
                    box.Focus();
                }
            }
            else
            {
                // Closing - return focus to terminal
                _currentPane?.ActiveControl.Focus();
            }
        }

        private async Task ExecuteUiCommandAsync(Func<Task> action, string commandTitle)
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                TerminalLogger.Error($"Error executing command {commandTitle}: {ex.Message}");
                await ShowSimpleMessageDialogAsync(commandTitle, ex.Message);
            }
        }

        private void ExecuteCommand(TerminalCommand cmd)
        {
            ToggleCommandPalette(); // Close first
            RecordCommandUsage(cmd.Id);
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    cmd.Action?.Invoke();
                }
                catch (Exception ex)
                {
                    TerminalLogger.Error($"Error executing command {cmd.Title}: {ex.Message}");
                }
            }, DispatcherPriority.Background);
        }

        private List<TerminalCommand> GetPaletteCommands(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return CommandPaletteOrdering.OrderForEmptyQuery(CommandRegistry.GetCommands(), _commandPaletteUsage).ToList();
            }

            return CommandPaletteOrdering.OrderSearchResults(CommandRegistry.Search(query), _commandPaletteUsage).ToList();
        }

        private void RecordCommandUsage(string commandId)
        {
            if (string.IsNullOrWhiteSpace(commandId))
            {
                return;
            }

            try
            {
                _commandPaletteUsageStore.RecordUse(commandId, DateTimeOffset.UtcNow);
                _commandPaletteUsageStore.Save();
                _commandPaletteUsage = new Dictionary<string, CommandPaletteUsageEntry>(_commandPaletteUsageStore.Load(), StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                // Keep command execution resilient if usage persistence is unavailable.
            }
        }

        /// <summary>
        /// The window's keyboard shortcuts, ahead of the focused pane (registered to tunnel). A key no shortcut takes
        /// is broadcast to the tab's other panes when broadcast input is on.
        /// </summary>
        private void OnWindowKeyDown(object? sender, KeyEventArgs e)
        {
            // An in-flight tab reorder drag owns the pointer; Escape aborts it before
            // anything else - in particular before the broadcast-to-panes fallback at
            // the bottom of this handler, which would otherwise also feed the raw ESC
            // byte to the terminals while the user is only canceling a drag. That
            // priority holds only because this handler is registered with
            // RoutingStrategies.Tunnel (its AddHandler call in the constructor): the
            // tunneling window handler runs before the focused TerminalView's
            // bubble-phase Escape handling (which sends "\x1b" to the
            // PTY), so the drag is canceled and the event marked handled before the
            // key could reach the pane.
            if (_isTabReorderDragging && e.Key == Key.Escape)
            {
                CancelTabReorderDrag();
                e.Handled = true;
                return;
            }

            var modifiers = e.KeyModifiers;
            bool isCtrl = (modifiers & KeyModifiers.Control) != 0;
            bool isShift = (modifiers & KeyModifiers.Shift) != 0;

            // The command palette owns Ctrl+Shift+P outright since V2 Phase 3b. It used to try the
            // Command Assist pin first and fall through when the pin declined, which meant whether
            // this chord opened the palette depended on whether an assist row happened to be
            // selected - a shortcut that works most of the time is worse than one that does not.
            // Pin has its own catalogued binding below.
            if (IsShortcut(e, "command_palette", "Ctrl+Shift+P"))
            {
                ToggleCommandPalette();
                e.Handled = true;
                return;
            }

            var overlay = this.FindControl<Grid>("CommandPaletteOverlay");
            if (overlay != null && overlay.IsVisible)
            {
                // Trap keys when palette is open
                if (e.Key == Key.Escape) ToggleCommandPalette();
                // Let TextBox handle the rest, but prevent bubbling to terminal
                // Actually, if we are focused on the TextBox, we don't need to do much.
                // But if focus somehow escaped, we want to ensure we don't type in terminal.
                return;
            }

            if (IsShortcut(e, "command_assist_toggle", "Ctrl+Space"))
            {
                RecordCommandUsage("command_assist_toggle");
                _currentPane?.ToggleCommandAssist();
                e.Handled = true;
                return;
            }

            if (IsShortcut(e, "command_assist_help", "Ctrl+Shift+H"))
            {
                if (TryOpenCommandAssistHelp(_currentPane))
                {
                    RecordCommandUsage("command_assist_help");
                    e.Handled = true;
                    return;
                }
            }

            if (IsShortcut(e, "command_assist_history", "Ctrl+R"))
            {
                if (_currentPane?.OpenCommandAssistHistorySearch() == true)
                {
                    RecordCommandUsage("command_assist_history");
                    e.Handled = true;
                    return;
                }
            }

            // V2 Phase 3b: pin/unpin on its own chord. Falls through when there is no row to pin,
            // so the key is not dead - it reaches the terminal like any unbound chord would.
            if (IsShortcut(e, "command_assist_pin", "Ctrl+Shift+S"))
            {
                if (_currentPane?.TryToggleCommandAssistPinShortcut() == true)
                {
                    RecordCommandUsage("command_assist_pin");
                    e.Handled = true;
                    return;
                }
            }

            if (IsShortcut(e, "settings", "Ctrl+,"))
            {
                RecordCommandUsage("settings");
                _ = OpenSettings(DefaultSettingsTarget());
                e.Handled = true;
                return;
            }

            if (IsShortcut(e, "connections", "Ctrl+Shift+K"))
            {
                RecordCommandUsage("connections");
                ToggleConnections();
                e.Handled = true;
                return;
            }

            if (IsShortcut(e, "toggle_recording", "Ctrl+Shift+R"))
            {
                RecordCommandUsage("toggle_recording");
                _currentPane?.ToggleRecording();
                e.Handled = true;
                return;
            }

            if (IsShortcut(e, "font_increase", "Ctrl+OemPlus") || IsShortcut(e, "font_increase_alt", "Ctrl+Add"))
            {
                RecordCommandUsage("font_increase");
                _settings.FontSize++;
                ApplySettingsToAllTabs();
                _settings.Save();
                e.Handled = true;
                return;
            }
            if (IsShortcut(e, "font_decrease", "Ctrl+OemMinus") || IsShortcut(e, "font_decrease_alt", "Ctrl+Subtract"))
            {
                RecordCommandUsage("font_decrease");
                _settings.FontSize = Math.Max(6, _settings.FontSize - 1);
                ApplySettingsToAllTabs();
                _settings.Save();
                e.Handled = true;
                return;
            }
            if (IsShortcut(e, "new_tab", "Ctrl+Shift+T"))
            {
                RecordCommandUsage("new_tab");
                AddTab();
                e.Handled = true;
                return;
            }
            if (IsShortcut(e, "toggle_tab_orientation", "Ctrl+Shift+L"))
            {
                RecordCommandUsage("toggle_tab_orientation");
                ToggleTabOrientation();
                e.Handled = true;
                return;
            }
            if (IsShortcut(e, "close_tab", "Ctrl+W"))
            {
                RecordCommandUsage("close_tab");
                _ = CloseSelectedTabAsync();
                e.Handled = true;
                return;
            }
            if (IsShortcut(e, "close_pane", "Ctrl+Shift+W"))
            {
                RecordCommandUsage("close_pane");
                CloseActivePane();
                e.Handled = true;
                return;
            }
            // Unbound by default: inert until the user assigns a chord.
            if (IsMuxPersistenceActive && IsShortcut(e, ShortcutCatalog.AttachSessionId, ""))
            {
                RecordCommandUsage(ShortcutCatalog.AttachSessionId);
                _ = AttachToMuxSessionAsync();
                e.Handled = true;
                return;
            }
            if (IsMuxPersistenceActive && IsShortcut(e, ShortcutCatalog.DetachPaneId, ""))
            {
                RecordCommandUsage(ShortcutCatalog.DetachPaneId);
                DetachActivePane();
                e.Handled = true;
                return;
            }
            if (IsMuxPersistenceActive && IsShortcut(e, ShortcutCatalog.QuitAndCloseAllShellsId, ""))
            {
                RecordCommandUsage(ShortcutCatalog.QuitAndCloseAllShellsId);
                _ = QuitAndCloseAllShellsAsync();
                e.Handled = true;
                return;
            }
            if (IsShortcut(e, "find", "Ctrl+F") || IsShortcut(e, "find_alt", "Ctrl+Shift+F"))
            {
                RecordCommandUsage("find");
                _currentPane?.ToggleSearch();
                e.Handled = true;
                return;
            }
            if (IsShortcut(e, "split_vertical", "Ctrl+Shift+D"))
            {
                RecordCommandUsage("split_vertical");
                // "Split Vertical" means a vertical divider (side-by-side panes).
                SplitPane(Avalonia.Layout.Orientation.Horizontal);
                e.Handled = true;
                return;
            }
            if (IsShortcut(e, "split_horizontal", "Ctrl+Shift+E"))
            {
                RecordCommandUsage("split_horizontal");
                // "Split Horizontal" means a horizontal divider (stacked panes).
                SplitPane(Avalonia.Layout.Orientation.Vertical);
                e.Handled = true;
                return;
            }
            if (IsShortcut(e, "equalize_panes", "Ctrl+Shift+G"))
            {
                RecordCommandUsage("equalize_panes");
                EqualizeCurrentSplit();
                e.Handled = true;
                return;
            }
            if (IsShortcut(e, "toggle_pane_zoom", "Ctrl+Shift+Z"))
            {
                RecordCommandUsage("toggle_pane_zoom");
                TogglePaneZoomForCurrentTab();
                e.Handled = true;
                return;
            }
            if (IsShortcut(e, "toggle_broadcast_input", "Ctrl+Shift+B"))
            {
                RecordCommandUsage("toggle_broadcast_input");
                ToggleBroadcastForCurrentTab();
                e.Handled = true;
                return;
            }
            bool nextTabShortcut = IsShortcut(e, "next_tab", "Ctrl+Tab");
            bool prevTabShortcut = IsShortcut(e, "prev_tab", "Ctrl+Shift+Tab");
            if (nextTabShortcut || prevTabShortcut)
            {
                RecordCommandUsage(prevTabShortcut ? "prev_tab" : "next_tab");
                bool switched = SwitchTabByMru(reverse: prevTabShortcut);
                if (switched)
                {
                    e.Handled = true;
                    return;
                }
            }
            bool moveTabPrevShortcut = IsShortcut(e, MoveTabPrevCommandId, "Ctrl+Shift+PageUp");
            bool moveTabNextShortcut = IsShortcut(e, MoveTabNextCommandId, "Ctrl+Shift+PageDown");
            if (moveTabPrevShortcut || moveTabNextShortcut)
            {
                // Keyboard move is meaningless mid-drag; swallowing the shortcut keeps
                // reorder mutations single-threaded through the drag path (cf. the
                // Escape guard above).
                if (_isTabReorderDragging)
                {
                    e.Handled = true;
                    return;
                }
                RecordCommandUsage(moveTabPrevShortcut ? MoveTabPrevCommandId : MoveTabNextCommandId);
                MoveSelectedTab(moveTabPrevShortcut ? -1 : 1);
                e.Handled = true;
                return;
            }
            if (IsShortcut(e, TitleBarCatalog.OpenTabListId, "Ctrl+Shift+O"))
            {
                RecordCommandUsage(TitleBarCatalog.OpenTabListId);
                PopulateTabListMenu(showFlyout: true);
                e.Handled = true;
                return;
            }
            if (IsShortcut(e, "paste", "Ctrl+V"))
            {
                RecordCommandUsage("paste");
                _ = PasteFromClipboardAsync();
                e.Handled = true;
                return;
            }
            if ((modifiers & KeyModifiers.Alt) != 0)
            {
                MoveDirection? dir = e.Key switch
                {
                    Key.Left => MoveDirection.Left,
                    Key.Right => MoveDirection.Right,
                    Key.Up => MoveDirection.Up,
                    Key.Down => MoveDirection.Down,
                    _ => null
                };
                if (dir.HasValue && NavigatePane(dir.Value))
                {
                    e.Handled = true;
                    return;
                }
            }

            BroadcastKeyToSiblingPanes(e);
        }
    }
}
