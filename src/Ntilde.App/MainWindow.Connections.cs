using Avalonia.Controls;
using Avalonia.Threading;
using Ntilde.Shell;
using Ntilde.Shell.Theming;
using Avalonia;
using Avalonia.Media;
using Avalonia.Layout;
using Avalonia.Input.Platform;

using Ntilde.Controls;
using Ntilde.Services.Ssh;
using Ntilde.Platform.Ssh.Launch;
using Ntilde.ViewModels.Ssh;
using Ntilde.Views.Ssh;

namespace Ntilde
{
    /// <summary>
    /// SSH connections: the connection manager, the new, delete and details dialogs, quick open, and opening a
    /// profile in a pane.
    /// </summary>
    public partial class MainWindow
    {
        private ConnectionManagerWindow? _connectionManagerWindow;

        private void ToggleConnections()
        {
            // The modal dialog disables MainWindow, so a second shortcut cannot normally arrive
            // while it is open; the guard just makes a stale reference activate the existing
            // window instead of stacking a second one.
            if (_connectionManagerWindow != null)
            {
                _connectionManagerWindow.Activate();
                return;
            }

            _ = OpenConnectionManagerAsync();
        }

        /// <summary>
        /// Opens Connection Manager as a real modal window, the way <see cref="OpenSettings"/>
        /// opens Settings. The manager surface and its event wiring are recreated per open:
        /// the dialog is now the surface's only lifetime, whereas the old in-window overlay
        /// kept one control alive inside MainWindow and refreshed it in place.
        /// </summary>
        private async Task OpenConnectionManagerAsync()
        {
            var window = new ConnectionManagerWindow();
            var connManager = window.Manager;
            if (connManager == null)
            {
                return;
            }

            connManager.SavedPasswordAccess = Vault;
            connManager.OnQuickOpenRequested += (profile, target, diagnosticsLevel) =>
            {
                HandleSshQuickOpen(profile, target, diagnosticsLevel);
                window.Close();
            };
            connManager.OnCopyLaunchCommandRequested += (profile, diagnosticsLevel) =>
            {
                _ = CopySshLaunchCommandAsync(profile, diagnosticsLevel, window);
            };
            connManager.OnConnectionDetailsRequested += (profile, diagnosticsLevel) =>
            {
                _ = ShowSshConnectionDetailsAsync(profile, diagnosticsLevel, window);
            };
            connManager.OnProfilesChanged += () =>
            {
                _sshConnectionService.SaveConnectionProfiles(connManager.GetAllProfiles());
            };
            connManager.OnSyncRequested += HandleSshSync;
            connManager.OnNewConnectionRequested += async () =>
            {
                await ShowNewSshConnectionDialogAsync(null, window);
            };
            connManager.OnEditProfile += async (profile) =>
            {
                await ShowNewSshConnectionDialogAsync(profile, window);
            };
            connManager.OnDeleteProfileRequested += async (profile) =>
            {
                await DeleteSshProfileAsync(profile, window);
            };

            // Same "focus search on open" the overlay toggle did. Focus set before the window
            // is shown does not stick, so it waits for Opened.
            window.Opened += (_, _) => connManager.FindControl<TextBox>("SearchInput")?.Focus();

            _connectionManagerWindow = window;
            window.Closed += (_, _) => _connectionManagerWindow = null;
            window.LoadProfiles(_sshConnectionService.GetConnectionProfiles());

            await window.ShowDialog(this);

            // Same focus return the overlay's hide branch did.
            _currentPane?.ActiveControl.Focus();
        }

        private void HandleSshSync()
        {
            try
            {
                var importedProfiles = Ntilde.Shell.ProfileImporter.ImportSshConfig();
                int changed = _sshConnectionService.MergeImportedProfiles(importedProfiles);
                if (changed > 0)
                {
                    RefreshProfileUIs();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Sync Error: {ex.Message}");
            }
        }

        private void HandleSshQuickOpen(TerminalProfile profile, SshQuickOpenTarget target, SshDiagnosticsLevel diagnosticsLevel)
        {
            switch (target)
            {
                case SshQuickOpenTarget.CurrentPane:
                    OpenProfileInCurrentPane(profile, diagnosticsLevel);
                    return;
                case SshQuickOpenTarget.NewTab:
                    AddTab(profile, diagnosticsLevel);
                    return;
                case SshQuickOpenTarget.SplitHorizontal:
                    OpenProfileInSplitPane(profile, Avalonia.Layout.Orientation.Vertical, diagnosticsLevel);
                    return;
                case SshQuickOpenTarget.SplitVertical:
                    OpenProfileInSplitPane(profile, Avalonia.Layout.Orientation.Horizontal, diagnosticsLevel);
                    return;
                default:
                    AddTab(profile, diagnosticsLevel);
                    return;
            }
        }

        private void OpenProfileInSplitPane(TerminalProfile profile, Avalonia.Layout.Orientation splitOrientation, SshDiagnosticsLevel diagnosticsLevel)
        {
            if (_currentPane == null)
            {
                AddTab(profile, diagnosticsLevel);
                return;
            }

            SplitPane(splitOrientation);
            OpenProfileInCurrentPane(profile, diagnosticsLevel);
        }

        private void OpenProfileInCurrentPane(TerminalProfile profile, SshDiagnosticsLevel diagnosticsLevel)
        {
            if (_currentPane == null)
            {
                AddTab(profile, diagnosticsLevel);
                return;
            }

            TerminalProfile resolvedProfile = profile.Type == ConnectionType.SSH
                ? _sshConnectionService.GetConnectionProfile(profile.Id) ?? profile
                : _settings.Profiles.Find(p => p.Id == profile.Id) ?? profile;
            var paneToReplace = _currentPane;
            var replacementPane = new TerminalPane(resolvedProfile, diagnosticsLevel);
            WirePane(replacementPane);
            replacementPane.ApplySettings(_settings);

            if (!ReplacePaneInVisualTree(paneToReplace, replacementPane))
            {
                DisposeControlTree(replacementPane);
                AddTab(resolvedProfile, diagnosticsLevel);
                return;
            }

            _currentPane = replacementPane;
            if (TryGetSelectedTab(out var selectedTab))
            {
                _activePaneByTab[selectedTab] = replacementPane;
                AgentHost.AgentSessionRegistry.Instance.SetTabAssociation(replacementPane.PaneId, GetPersistentTabId(selectedTab));
                if (FindTabHeaderTextBlock(selectedTab.Header) is TextBlock tabHeader)
                {
                    tabHeader.Text = resolvedProfile.Name;
                }
            }

            DisposeControlTree(paneToReplace);
            Dispatcher.UIThread.Post(() => replacementPane.ActiveControl.Focus(), DispatcherPriority.Loaded);
            UpdateTabVisuals();
            UpdatePaneAutomationLabels();
            UpdateBroadcastIndicator();
            if (TryGetSelectedTab(out selectedTab))
            {
                RefreshLayoutModelForTab(selectedTab);
            }
        }

        // Dialogs raised from the Connection Manager window take that window as their owner
        // (optional `owner`, falling back to MainWindow everywhere else) so they stack on the
        // surface the user actually acted on rather than on the disabled root window.
        private async Task ShowNewSshConnectionDialogAsync(TerminalProfile? existingProfile, Window? owner = null)
        {
            var vm = _sshConnectionService.CreateEditorViewModel(existingProfile);
            // The default backend for a NEW profile follows the global toggle: native where it is
            // enabled, OpenSSH where it is not — so the default can never point at a backend that
            // would refuse to connect. Existing profiles arrive with their stored kind and are
            // never re-defaulted. Model-level defaults stay OpenSsh on purpose (old stores whose
            // JSON predates the field must not silently migrate); this is the creation surface.
            vm.BackendKind ??= _settings.ExperimentalNativeSshEnabled
                ? Ntilde.Platform.Ssh.Models.SshBackendKind.Native
                : Ntilde.Platform.Ssh.Models.SshBackendKind.OpenSsh;
            vm.ExperimentalNativeSshEnabled = _settings.ExperimentalNativeSshEnabled;
            var dialog = new NewSshConnectionView(vm);
            ThemedWindowChrome.Apply(dialog);
            WireRemoteMuxInstall(vm, dialog);
            bool saved = await dialog.ShowDialog<bool>(owner ?? this);

            if (!saved)
            {
                return;
            }

            try
            {
                var savedProfile = _sshConnectionService.SaveProfile(vm);
                TerminalProfile profile = _sshConnectionService.GetConnectionProfile(savedProfile.Id)
                    ?? SshConnectionService.ToRuntimeProfile(savedProfile);
                RefreshProfileUIs();

                if (vm.ConnectAfterSave)
                {
                    if (profile.SshBackendKind == Ntilde.Platform.Ssh.Models.SshBackendKind.Native &&
                        !_settings.ExperimentalNativeSshEnabled)
                    {
                        await ShowSimpleMessageDialogAsync(
                            "Native SSH disabled",
                            "This profile was saved with the Native backend, but native SSH is disabled globally. Turn it on under Settings > SSH or switch the profile back to OpenSSH.",
                            owner);
                        return;
                    }

                    AddTab(profile, SshDiagnosticsLevel.None);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainWindow] Failed to save SSH connection: {ex.Message}");
            }
        }

        private async Task DeleteSshProfileAsync(TerminalProfile profile, Window? owner = null)
        {
            if (profile == null)
            {
                return;
            }

            string trimmedName = string.IsNullOrWhiteSpace(profile.Name) ? string.Empty : profile.Name.Trim();
            const int maxLabelLength = 60;
            string displayName = trimmedName.Length > maxLabelLength
                ? trimmedName[..maxLabelLength] + "\u2026"
                : trimmedName;
            string label = string.IsNullOrWhiteSpace(displayName)
                ? "this connection"
                : $"\"{displayName}\"";

            if (!await ShowDeleteConnectionConfirmationAsync(label, owner))
            {
                return;
            }

            try
            {
                _sshConnectionService.DeleteProfile(profile.Id);

                // Two orderings matter here and they pull in opposite directions, so the
                // purge is sequenced and the refresh is guaranteed:
                //   * the purge runs AFTER the store delete, so if the delete throws the
                //     secret stays put rather than being orphaned from a profile that
                //     still exists;
                //   * the refresh runs in a finally, so a throwing purge cannot leave the
                //     deleted connection sitting in the list.
                // Ordering them without the finally forces a choice between an orphaned
                // secret and a stale row; this way neither failure mode exists.
                try
                {
                    Vault?.ForgetSavedPassword(profile);
                }
                finally
                {
                    RefreshProfileUIs();
                }
            }
            catch (Exception ex)
            {
                await ShowSimpleMessageDialogAsync("Delete connection", ex.Message, owner);
            }
        }

        private async Task<bool> ShowDeleteConnectionConfirmationAsync(string label, Window? owner = null)
        {
            bool confirmed = false;

            var dialog = CreateThemedDialogWindow("Delete connection?", 480, 210, canResize: false);

            var cancelButton = new Button
            {
                Content = "Cancel",
                Width = 92,
                IsCancel = true
            };
            cancelButton.Click += (_, __) =>
            {
                confirmed = false;
                dialog.Close();
            };

            var deleteButton = new Button
            {
                Content = "Delete",
                Width = 92
            };
            deleteButton.Click += (_, __) =>
            {
                confirmed = true;
                dialog.Close();
            };

            // Give Cancel initial focus: combined with IsCancel above, Escape reliably backs
            // out of a dialog the user may have opened by accident. Delete intentionally has
            // no IsDefault, so Enter never triggers the destructive action.
            dialog.Opened += (_, __) => cancelButton.Focus();

            dialog.Content = new Border
            {
                Padding = new Thickness(16),
                Child = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"Delete {label}?",
                            FontWeight = FontWeight.SemiBold,
                            TextWrapping = TextWrapping.Wrap
                        },
                        new TextBlock
                        {
                            Text = "The saved connection and any password stored for it are removed. "
                                 + "Panes already connected keep running until you close them.",
                            Opacity = 0.8,
                            TextWrapping = TextWrapping.Wrap
                        },
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Spacing = 8,
                            Children = { cancelButton, deleteButton }
                        }
                    }
                }
            };

            await dialog.ShowDialog(owner ?? this);
            return confirmed;
        }

        private async Task CopySshLaunchCommandAsync(TerminalProfile profile, SshDiagnosticsLevel diagnosticsLevel, Window? owner = null)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.Clipboard == null)
            {
                await ShowSimpleMessageDialogAsync("Copy launch command", "Clipboard is not available.", owner);
                return;
            }

            try
            {
                string command = _sshConnectionService.BuildLaunchCommand(profile, diagnosticsLevel);
                await topLevel.Clipboard.SetTextAsync(command);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainWindow] Failed to copy SSH command: {ex.Message}");
                await ShowSimpleMessageDialogAsync("Copy launch command", ex.Message, owner);
            }
        }

        private async Task ShowSshConnectionDetailsAsync(TerminalProfile profile, SshDiagnosticsLevel diagnosticsLevel, Window? owner = null)
        {
            try
            {
                var details = _sshConnectionService.BuildLaunchDetails(profile, diagnosticsLevel);
                await ShowConnectionDetailsDialogAsync(details, diagnosticsLevel, owner);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainWindow] Failed to show SSH connection details: {ex.Message}");
                await ShowSimpleMessageDialogAsync("Connection details", ex.Message, owner);
            }
        }

        private async Task ShowConnectionDetailsDialogAsync(SshLaunchDetails details, SshDiagnosticsLevel diagnosticsLevel, Window? owner = null)
        {
            var dialog = CreateThemedDialogWindow("Connection details", 760, 340, canResize: false);

            var sshPathBox = new TextBox { Text = details.SshPath, IsReadOnly = true };
            var configPathBox = new TextBox { Text = details.ConfigPath, IsReadOnly = true };
            var commandBox = new TextBox
            {
                Text = details.CommandLine,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                Height = 72
            };

            var closeButton = new Button { Content = "Close", Width = 92 };
            closeButton.Click += (_, __) => dialog.Close();

            var copyButton = new Button { Content = "Copy command", Width = 120 };
            copyButton.Click += async (_, __) =>
            {
                var topLevel = TopLevel.GetTopLevel(dialog);
                if (topLevel?.Clipboard != null)
                {
                    await topLevel.Clipboard.SetTextAsync(details.CommandLine);
                }
            };

            dialog.Content = new Border
            {
                Padding = new Thickness(16),
                Child = new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock { Text = $"Diagnostics: {diagnosticsLevel}" },
                        new TextBlock { Text = $"Alias: {details.Alias}" },
                        new TextBlock { Text = "Resolved SSH path" },
                        sshPathBox,
                        new TextBlock { Text = "Generated config path" },
                        configPathBox,
                        new TextBlock { Text = "Repro command" },
                        commandBox,
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Spacing = 8,
                            Children = { copyButton, closeButton }
                        }
                    }
                }
            };

            await dialog.ShowDialog(owner ?? this);
        }

        private void RefreshProfileUIs()
        {
            PopulateNewTabMenu();
            SetupCommandPalette();

            // Refresh Connection Manager if open
            _connectionManagerWindow?.LoadProfiles(_sshConnectionService.GetConnectionProfiles());
        }
    }
}
