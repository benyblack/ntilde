using Avalonia.Controls;
using Ntilde.Shell;
using Ntilde.VT;
using Avalonia;
using Avalonia.Media;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Ntilde.Pty;

using Ntilde.Controls;

namespace Ntilde
{
    /// <summary>
    /// Saving and restoring the window's session: the startup restore, workspaces and workspace templates,
    /// per-profile template rules, and session bundles.
    /// </summary>
    public partial class MainWindow
    {
        private sealed class SessionRestoreAbortedException : Exception
        {
            public SessionRestoreAbortedException(string message) : base(message) { }
        }

        private void ApplySessionSnapshot(NtildeSession session)
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return;

            // A workspace, template or bundle is a layout, not a live session: rebuilt panes start
            // fresh shells. A snapshot saved before its ids were stripped at capture (or a bundle
            // from elsewhere) must not make the rebuilt panes claim daemon sessions.
            session = SessionManager.WithoutMuxIds(session);
            DisposeAllTabs(tabs);
            ResetTabCollections();
            SessionManager.RestoreSession(this, tabs, _settings, session);
            if (tabs.Items.Count > 0)
            {
                InitializeRestoredTabs(tabs);
            }
            SetupCommandPalette();
        }

        /// <param name="sessionsCannotPredateUtc">Read only when there is a session to restore (an OS call).</param>
        private bool TryRestoreStartupSession(TabControl tabs, Func<DateTime?> sessionsCannotPredateUtc, out NtildeSession? loadedSession)
        {
            loadedSession = null;
            if (!SessionManager.TryLoadSavedSession(out NtildeSession? session, out DateTime? savedUtc) ||
                session == null ||
                session.Tabs.Count == 0)
            {
                return false;
            }
            loadedSession = session;
            // Spec R2, decided once per launch: the file predates the boot (or, on Windows, this logon), so a reboot or
            // a logoff ended the local daemon sessions it names. The mark goes on the loaded nodes, before any tab is
            // built from them, so deferred tabs carry it too - and so does the session file until each pane spawns.
            DateTime? boundary = sessionsCannotPredateUtc();
            if (Ntilde.Shell.Mux.MuxRestoreExpectations.SessionsEndedByReboot(savedUtc, boundary))
            {
                TerminalLogger.Log($"TryRestoreStartupSession: the session file ({savedUtc:o}) predates this boot or logon ({boundary:o}); its local shells start fresh, quietly");
                SessionManager.MarkLocalMuxSessionsEndedByReboot(session);
            }

            _startup.Checkpoint("StartupRestore.AfterSessionLoad");

            try
            {
                _startup.BeginSessionRestore(session, immediate =>
                {
                    tabs.Items.Clear();

                    for (int index = 0; index < session.Tabs.Count; index++)
                    {
                        TabSession tabSession = session.Tabs[index];
                        TabItem? tabItem = index == immediate.OriginalIndex
                            ? SessionManager.CreateRestoredTabItem(tabSession, _settings)
                            : CreateStartupPlaceholderTab(tabSession);

                        if (tabItem != null)
                        {
                            tabs.Items.Add(tabItem);
                        }
                    }
                    _startup.Checkpoint("StartupRestore.AfterTabMaterialization");

                    if (tabs.Items.Count == 0)
                    {
                        throw new SessionRestoreAbortedException(
                            "Session restore produced no tab items; aborting restore.");
                    }

                    if (immediate.OriginalIndex >= 0 && immediate.OriginalIndex < tabs.Items.Count)
                    {
                        tabs.SelectedIndex = immediate.OriginalIndex;
                    }

                    InitializeRestoredTabs(tabs);
                    _startup.Checkpoint("StartupRestore.AfterInitializeRestoredTabs");
                });
            }
            catch (SessionRestoreAbortedException ex)
            {
                TerminalLogger.Log($"TryRestoreStartupSession: aborted ({ex.Message})");
                return false;
            }

            return true;
        }

        private TabItem CreateStartupPlaceholderTab(TabSession tabSession)
        {
            return new TabItem
            {
                Header = new TextBlock
                {
                    Text = tabSession.Title,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    Padding = new Thickness(10, 4)
                },
                Content = new Border { Background = Brushes.Transparent },
                Tag = tabSession
            };
        }

        private void HydrateDeferredStartupTab(TabControl tabs, StartupRestoreTab deferredTab)
        {
            // Find the placeholder by identity rather than by OriginalIndex (#326 review). Restore
            // materializes every saved tab up front and hydrates the placeholders on a later
            // background dispatcher pass; anything that removes a tab in between shifts every later
            // index by one, and an index lookup then either writes this tab's content into its
            // neighbour's placeholder or falls out of range and leaves a permanently blank tab whose
            // null root can be persisted over the saved session. That became reachable without any
            // user action when ShellExitPolicy started defaulting to "Graceful": the restored
            // selected tab's shell can exit 0 during startup and close its own pane, and the exit is
            // posted at normal dispatcher priority while this pass is posted at background priority,
            // so the close always gets there first.
            //
            // Placeholders carry their TabSession in Tag (CreateStartupPlaceholderTab) and the
            // deferred entry carries the same instance out of NtildeSession.Tabs, so reference
            // equality names the right tab no matter where it has drifted to. OriginalIndex stays on
            // the record: it is what the deferred plan is built and logged against.
            TabItem? tabItem = null;
            foreach (object? item in tabs.Items)
            {
                if (item is TabItem candidate && ReferenceEquals(candidate.Tag, deferredTab.Tab))
                {
                    tabItem = candidate;
                    break;
                }
            }

            if (tabItem == null)
            {
                return;
            }

            Control? content = SessionManager.CreateRestoredTabContent(deferredTab.Tab, _settings);
            if (content == null)
            {
                return;
            }

            tabItem.Content = content;
            tabItem.Tag = deferredTab.Tab;
            InitializeRestoredTabs(tabs);
        }

        private async Task SaveWorkspaceInteractiveAsync()
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return;

            string suggested = $"workspace-{DateTime.Now:yyyyMMdd-HHmm}";
            var name = await ShowTextPromptAsync("Save Workspace", "Workspace name", suggested);
            if (string.IsNullOrWhiteSpace(name)) return;

            var snapshot = SessionManager.CaptureSession(this, tabs);
            if (WorkspaceManager.SaveWorkspace(name.Trim(), snapshot))
            {
                SetupCommandPalette();
            }
        }

        private async Task LoadWorkspaceInteractiveAsync()
        {
            var names = WorkspaceManager.ListWorkspaceNames();
            if (names.Count == 0) return;

            var first = names[0];
            var name = await ShowTextPromptAsync("Load Workspace", "Workspace name", first);
            if (string.IsNullOrWhiteSpace(name)) return;
            LoadWorkspaceByName(name.Trim());
        }

        private async Task SaveWorkspaceTemplateInteractiveAsync()
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return;

            string suggested = $"template-{DateTime.Now:yyyyMMdd-HHmm}";
            var name = await ShowTextPromptAsync("Save Workspace Template", "Template name", suggested);
            if (string.IsNullOrWhiteSpace(name)) return;

            var snapshot = SessionManager.CaptureSession(this, tabs);
            if (WorkspaceManager.SaveWorkspaceTemplate(name.Trim(), snapshot))
            {
                SetupCommandPalette();
            }
        }

        private async Task LoadWorkspaceTemplateInteractiveAsync()
        {
            var names = WorkspaceManager.ListWorkspaceTemplateNames();
            if (names.Count == 0) return;

            var first = names[0];
            var name = await ShowTextPromptAsync("Apply Workspace Template", "Template name", first);
            if (string.IsNullOrWhiteSpace(name)) return;
            ApplyWorkspaceTemplateByName(name.Trim());
        }

        private async Task ExportWorkspaceBundleInteractiveAsync()
        {
            if (!WorkspacePolicyManager.Current.AllowWorkspaceBundleExport)
            {
                System.Diagnostics.Debug.WriteLine("[Workspace] Export bundle blocked by policy.");
                return;
            }

            var names = WorkspaceManager.ListWorkspaceNames();
            if (names.Count == 0) return;

            string defaultName = names[0];
            string? name = await ShowTextPromptAsync("Export Workspace Bundle", "Workspace name", defaultName);
            if (string.IsNullOrWhiteSpace(name)) return;

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null) return;

            string suggestedFileName = WorkspaceBundleNaming.SuggestedFileName(name);
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export Workspace Bundle",
                SuggestedFileName = suggestedFileName,
                FileTypeChoices = new[]
                {
                    new FilePickerFileType(WorkspaceBundleNaming.PickerDisplayName) { Patterns = WorkspaceBundleNaming.PickerPatterns }
                }
            });

            if (file == null) return;

            bool ok = WorkspaceManager.ExportWorkspaceBundle(name.Trim(), file.Path.LocalPath, Environment.UserName);
            if (!ok)
            {
                System.Diagnostics.Debug.WriteLine("[Workspace] Export bundle failed.");
            }
        }

        private async Task ExportCurrentSessionBundleInteractiveAsync()
        {
            if (!WorkspacePolicyManager.Current.AllowWorkspaceBundleExport)
            {
                System.Diagnostics.Debug.WriteLine("[Workspace] Export bundle blocked by policy.");
                return;
            }

            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return;

            string suggested = $"session-{DateTime.Now:yyyyMMdd-HHmm}";
            string? label = await ShowTextPromptAsync("Export Session Bundle", "Bundle name", suggested);
            if (string.IsNullOrWhiteSpace(label)) return;

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null) return;

            string suggestedFileName = WorkspaceBundleNaming.SuggestedFileName(label);
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export Session Bundle",
                SuggestedFileName = suggestedFileName,
                FileTypeChoices = new[]
                {
                    new FilePickerFileType(WorkspaceBundleNaming.PickerDisplayName) { Patterns = WorkspaceBundleNaming.PickerPatterns }
                }
            });

            if (file == null) return;

            var snapshot = SessionManager.CaptureSession(this, tabs);
            bool ok = WorkspaceManager.ExportWorkspaceBundle(label.Trim(), snapshot, file.Path.LocalPath, Environment.UserName);
            if (!ok)
            {
                System.Diagnostics.Debug.WriteLine("[Workspace] Export current session bundle failed.");
            }
        }

        private async Task ImportWorkspaceBundleInteractiveAsync()
        {
            if (!WorkspacePolicyManager.Current.AllowWorkspaceBundleImport)
            {
                System.Diagnostics.Debug.WriteLine("[Workspace] Import bundle blocked by policy.");
                return;
            }

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import Workspace Bundle",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType(WorkspaceBundleNaming.PickerDisplayName) { Patterns = WorkspaceBundleNaming.PickerPatterns }
                }
            });

            if (files.Count == 0) return;

            string bundlePath = files[0].Path.LocalPath;
            string suggestedName = WorkspaceBundleNaming.SuggestedWorkspaceName(bundlePath);

            string? name = await ShowTextPromptAsync("Import Workspace Bundle", "Workspace name", suggestedName);
            if (string.IsNullOrWhiteSpace(name)) return;

            // Import only STORES the bundle; its commands are confirmed at execution time
            // in LoadWorkspaceByName. Confirming here would be a TOCTOU (the file could
            // change between this read and ImportWorkspaceBundle's re-read) and wouldn't
            // cover later loads — so the single confirmation gate lives at execution (#171).
            if (WorkspaceManager.ImportWorkspaceBundle(bundlePath, name.Trim(), out var error))
            {
                SetupCommandPalette();
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[Workspace] Import bundle failed: {error}");
        }

        private async Task OpenWorkspaceBundleInteractiveAsync()
        {
            if (!WorkspacePolicyManager.Current.AllowWorkspaceBundleImport)
            {
                System.Diagnostics.Debug.WriteLine("[Workspace] Open bundle blocked by policy.");
                return;
            }

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open Workspace Bundle",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType(WorkspaceBundleNaming.PickerDisplayName) { Patterns = WorkspaceBundleNaming.PickerPatterns }
                }
            });

            if (files.Count == 0) return;

            string bundlePath = files[0].Path.LocalPath;
            bool ok = WorkspaceManager.LoadWorkspaceBundleSession(bundlePath, out var _workspaceName, out var snapshot, out var error);
            if (ok && snapshot != null)
            {
                // This spawns the bundle's stored commands immediately — confirm first
                // for a foreign bundle (.ntildews.json or legacy .novaws.json) opened from disk (#171).
                if (!await ConfirmBundleCommandsAsync(snapshot, _workspaceName ?? "workspace"))
                {
                    return;
                }
                ApplySessionSnapshot(snapshot);
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[Workspace] Open bundle failed: {error}");
        }

        private async void LoadWorkspaceByName(string name)
        {
            var snapshot = WorkspaceManager.LoadWorkspace(name);
            if (snapshot == null) return;

            // Confirm ad-hoc commands against the exact stored snapshot that is about to
            // run — this is the single execution-time gate (no TOCTOU), and it covers
            // imported bundles whichever way they're loaded (#171). Profile-only
            // workspaces collect no commands and skip the prompt.
            if (!await ConfirmBundleCommandsAsync(snapshot, name))
            {
                return;
            }
            ApplySessionSnapshot(snapshot);
        }

        private void ApplyWorkspaceTemplateByName(string name)
        {
            var snapshot = WorkspaceManager.LoadWorkspaceTemplate(name);
            if (snapshot == null) return;
            ApplySessionSnapshot(snapshot);
        }

        internal static TabTemplateRule? FindTabTemplateRule(IEnumerable<TabTemplateRule>? rules, Guid profileId)
        {
            if (rules == null) return null;
            return rules.FirstOrDefault(r =>
                r != null &&
                r.Enabled &&
                r.ProfileId == profileId &&
                !string.IsNullOrWhiteSpace(r.TemplateName));
        }

        internal static bool UpsertTabTemplateRule(List<TabTemplateRule> rules, Guid profileId, string templateName)
        {
            if (rules == null) throw new ArgumentNullException(nameof(rules));
            if (string.IsNullOrWhiteSpace(templateName)) return false;

            var existing = rules.FirstOrDefault(r => r.ProfileId == profileId);
            if (existing == null)
            {
                rules.Add(new TabTemplateRule
                {
                    ProfileId = profileId,
                    TemplateName = templateName.Trim(),
                    Enabled = true
                });
                return true;
            }

            existing.TemplateName = templateName.Trim();
            existing.Enabled = true;
            return true;
        }

        internal static bool RemoveTabTemplateRule(List<TabTemplateRule> rules, Guid profileId)
        {
            if (rules == null) throw new ArgumentNullException(nameof(rules));
            int before = rules.Count;
            rules.RemoveAll(r => r.ProfileId == profileId);
            return rules.Count != before;
        }

        private async Task SetTemplateRuleForCurrentPaneProfileAsync()
        {
            var profile = _currentPane?.Profile;
            if (profile == null) return;

            var templateNames = WorkspaceManager.ListWorkspaceTemplateNames();
            if (templateNames.Count == 0) return;

            string suggested = FindTabTemplateRule(_settings.TabTemplateRules, profile.Id)?.TemplateName ?? templateNames[0];
            string? name = await ShowTextPromptAsync(
                "Set Tab Template Rule",
                $"Template for profile '{profile.Name}'",
                suggested);
            if (string.IsNullOrWhiteSpace(name)) return;

            string trimmed = name.Trim();
            if (WorkspaceManager.LoadWorkspaceTemplate(trimmed) == null)
            {
                System.Diagnostics.Debug.WriteLine($"[Workspace] Template rule not set: missing template '{trimmed}'.");
                return;
            }

            if (UpsertTabTemplateRule(_settings.TabTemplateRules, profile.Id, trimmed))
            {
                _settings.Save();
                SetupCommandPalette();
            }
        }

        private void ClearTemplateRuleForCurrentPaneProfile()
        {
            var profile = _currentPane?.Profile;
            if (profile == null) return;

            if (RemoveTabTemplateRule(_settings.TabTemplateRules, profile.Id))
            {
                _settings.Save();
                SetupCommandPalette();
            }
        }

        private bool TryApplyTemplateRuleForProfile(TerminalProfile profile)
        {
            var rule = FindTabTemplateRule(_settings.TabTemplateRules, profile.Id);
            if (rule == null) return false;

            var template = WorkspaceManager.LoadWorkspaceTemplate(rule.TemplateName);
            if (template == null || template.Tabs.Count == 0) return false;

            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return false;

            var current = SessionManager.CaptureSession(this, tabs);
            var templateTab = CloneTabSession(template.Tabs[0]);
            current.Tabs.Add(templateTab);
            current.ActiveTabIndex = current.Tabs.Count - 1;
            ApplySessionSnapshot(current);
            return true;
        }

        private static TabSession CloneTabSession(TabSession source)
        {
            return new TabSession
            {
                TabId = source.TabId,
                Title = source.Title,
                UserTitle = source.UserTitle,
                IsPinned = source.IsPinned,
                IsProtected = source.IsProtected,
                ActivePaneId = source.ActivePaneId,
                ZoomedPaneId = source.ZoomedPaneId,
                BroadcastInputEnabled = source.BroadcastInputEnabled,
                Root = ClonePaneNode(source.Root)
            };
        }

        private static PaneNode? ClonePaneNode(PaneNode? source)
        {
            if (source == null) return null;

            return new PaneNode
            {
                Type = source.Type,
                SplitOrientation = source.SplitOrientation,
                ProfileId = source.ProfileId,
                PaneId = source.PaneId,
                Command = source.Command,
                Arguments = source.Arguments,
                Sizes = source.Sizes.ToList(),
                Children = source.Children.Select(ClonePaneNode).Where(c => c != null).Select(c => c!).ToList()
            };
        }

        private void InitializeRestoredTabs(TabControl tabs)
        {
            foreach (var item in tabs.Items.Cast<TabItem>())
            {
                GetTabId(item);
                GetOrCreateTabState(item);
                ConfigureTabHeader(item, GetTabHeaderText(item));
                if (item.Content is Control c)
                {
                    WireControlTree(c);
                    RegisterPaneOwners(item, c);
                }
            }

            foreach (var item in tabs.Items.Cast<TabItem>())
            {
                if (item.Content is not Control c) continue;

                TerminalPane? active = null;
                if (item.Tag is TabSession saved &&
                    !string.IsNullOrWhiteSpace(saved.ActivePaneId) &&
                    Guid.TryParse(saved.ActivePaneId, out var activeId))
                {
                    active = FindPaneById(c, activeId);
                }

                active ??= FindFirstPane(c);
                if (active != null) _activePaneByTab[item] = active;

                if (item.Tag is TabSession savedSession && savedSession.BroadcastInputEnabled)
                {
                    _broadcastEnabledTabs.Add(item);
                }
            }

            foreach (var item in tabs.Items.Cast<TabItem>())
            {
                if (item.Tag is not TabSession saved ||
                    string.IsNullOrWhiteSpace(saved.ZoomedPaneId) ||
                    !Guid.TryParse(saved.ZoomedPaneId, out var zoomedPaneId) ||
                    item.Content is not Control c)
                {
                    continue;
                }

                var zoomPane = FindPaneById(c, zoomedPaneId);
                if (zoomPane != null)
                {
                    EnterPaneZoom(item, zoomPane, publishEvent: false);
                }
            }

            if (tabs.SelectedItem is TabItem selected)
            {
                var selectedPane = ResolvePaneForTab(selected);
                if (selectedPane != null)
                {
                    UpdateActivePane(selectedPane);
                    FocusPaneTerminal(selectedPane, defer: true);
                }
            }

            UpdateTabVisuals();
            UpdatePaneAutomationLabels();
            UpdateBroadcastIndicator();
            RefreshAllLayoutModels();
            CleanupTabMru(tabs);
            PopulateTabListMenu();
        }

        // Collects the ad-hoc shell commands a bundle would actually spawn on restore.
        // Mirrors SessionManager.RestorePaneTree: a leaf runs its raw Command/Arguments
        // ONLY when its profile doesn't resolve, so panes whose profile resolves are
        // skipped — that both matches what runs and avoids prompting for locally-saved
        // workspaces that store a ShellCommand alongside a resolvable ProfileId (#171).
        internal static List<string> CollectBundleCommands(NtildeSession? session, TerminalSettings settings)
        {
            var commands = new List<string>();
            if (session?.Tabs == null) return commands;

            void Walk(PaneNode? node)
            {
                if (node == null) return;
                if (node.Type == NodeType.Leaf)
                {
                    // Skip panes that resolve a known local/SSH profile — RestorePaneTree
                    // uses the profile and ignores Command/Arguments for those.
                    if (SessionManager.TryResolvePaneProfile(node, settings) != null)
                    {
                        return;
                    }

                    // Otherwise the fallback runs `cmd.exe`/Command with Arguments, so an
                    // argument-only leaf still runs `cmd.exe <args>` and can smuggle cmd
                    // metacharacters (#171 review).
                    bool hasCommand = !string.IsNullOrWhiteSpace(node.Command);
                    bool hasArgs = !string.IsNullOrWhiteSpace(node.Arguments);
                    if (hasCommand || hasArgs)
                    {
                        string effectiveCommand = hasCommand ? node.Command! : "cmd.exe";
                        string args = hasArgs ? " " + node.Arguments : "";
                        commands.Add((effectiveCommand + args).Trim());
                    }
                }
                else if (node.Children != null)
                {
                    foreach (var child in node.Children) Walk(child);
                }
            }

            foreach (var tab in session.Tabs)
            {
                if (tab != null) Walk(tab.Root);
            }
            return commands;
        }

        // Confirms the commands a foreign bundle will run before it spawns anything.
        // Returns true when there is nothing to run or the user approves.
        private async Task<bool> ConfirmBundleCommandsAsync(NtildeSession? session, string bundleName)
        {
            var commands = CollectBundleCommands(session, _settings);
            if (commands.Count == 0) return true; // profile-only bundles run known targets

            bool confirmed = false;
            var dialog = CreateThemedDialogWindow("Run Workspace Commands?", 520, 320, canResize: false);

            var listText = string.Join("\n", commands.ConvertAll(c => "\u2022  " + c));

            var cancelButton = new Button { Content = "Cancel", Width = 92 };
            cancelButton.Click += (_, __) => { confirmed = false; dialog.Close(); };

            var runButton = new Button { Content = "Run Commands", Width = 130 };
            runButton.Click += (_, __) => { confirmed = true; dialog.Close(); };

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
                            Text = $"The workspace \"{bundleName}\" will run these commands:",
                            FontWeight = FontWeight.SemiBold,
                            TextWrapping = TextWrapping.Wrap
                        },
                        new ScrollViewer
                        {
                            MaxHeight = 180,
                            Content = new TextBlock
                            {
                                Text = listText,
                                FontFamily = new Avalonia.Media.FontFamily("Consolas, monospace"),
                                TextWrapping = TextWrapping.Wrap
                            }
                        },
                        new TextBlock
                        {
                            Text = "Only run commands from a workspace you trust.",
                            Opacity = 0.8,
                            TextWrapping = TextWrapping.Wrap
                        },
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Spacing = 8,
                            Children = { cancelButton, runButton }
                        }
                    }
                }
            };

            await dialog.ShowDialog(this);
            return confirmed;
        }
    }
}
