using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.LogicalTree;
using Ntilde.Shell;
using Ntilde.VT;

using Ntilde.Controls;

namespace Ntilde
{
    /// <summary>
    /// Wiring a pane's events to the window, and handling what panes report: output, bells, titles, progress,
    /// exits, actions and mux sharing.
    /// </summary>
    public partial class MainWindow
    {
        private void WireControlTree(Control control)
        {
            if (control is TerminalPane pane)
            {
                WirePane(pane);
                return;
            }

            if (control is Grid grid)
            {
                foreach (var child in grid.Children)
                {
                    if (child is GridSplitter splitter)
                    {
                        WireSplitter(splitter, grid);
                    }
                    else if (child is Control cc)
                    {
                        WireControlTree(cc);
                    }
                }

                return;
            }

            if (control is Panel panel)
            {
                foreach (var child in panel.Children)
                {
                    if (child is Control cc) WireControlTree(cc);
                }

                return;
            }

            if (control is ContentControl contentControl && contentControl.Content is Control content)
            {
                WireControlTree(content);
            }
        }

        /// <summary>
        /// The single funnel every pane in this window passes through, and therefore the only
        /// correct place to inject per-pane window state.
        /// </summary>
        /// <remarks>
        /// The three creation sites in this file call it directly; panes rebuilt by
        /// <see cref="SessionManager"/> during session restore reach it via
        /// <c>InitializeRestoredTabs</c> -> <c>WireControlTree</c>. Assigning
        /// <c>CommandAssistServices</c> at the creation sites instead missed the restore path
        /// entirely, and a restored pane without the graph throws out of
        /// <c>ApplyShellIntegrationLaunchPlan</c> on the PTY spawn path - which the spawn's catch
        /// turns into "[ERROR] Failed to spawn process" and no session at all. Injecting here
        /// makes "the window wired this pane" and "the pane has its dependencies" the same fact.
        /// </remarks>
        private void WirePane(TerminalPane pane)
        {
            pane.CommandAssistServices = _commandAssistServices;
            pane.SessionFactory = _sessionFactory;
            pane.SshInteractionHandler = _sshInteractionService;
            // A method group: OpenRemoteMuxInstall is read when a notice is raised, not now.
            pane.RemoteNoticeAction = RemoteMuxNoticeAction;
            pane.LocalMuxRestartAction = LocalMuxRestartNoticeAction;
            // Kept by UnwirePane: a stale remote result reaches a closed pane, and its shell's kill asks for the pass (codex C1).
            pane.RemoteMuxReleaseCheck = ScheduleRemoteMuxHostRelease;
            pane.RequestRemoteFilesSidebarTransfer -= OnPaneRequestRemoteFilesSidebarTransfer;
            pane.WorkingDirectoryChanged -= OnPaneWorkingDirectoryChanged;
            pane.TitleChanged -= OnPaneTitleChanged;
            pane.PaneActionRequested -= OnPaneActionRequested;
            pane.OutputReceived -= OnPaneOutputReceived;
            pane.BellReceived -= OnPaneBellReceived;
            pane.ProcessExited -= OnPaneProcessExited;
            pane.LongCommandCompleted -= OnPaneLongCommandCompleted;
            pane.PersistentSessionAttached -= OnPanePersistentSessionAttached;
            pane.PersistenceNotice -= OnPanePersistenceNotice;
            pane.MuxSharingChanged -= OnPaneMuxSharingChanged;
            pane.MuxShareEnded -= OnPaneMuxShareEnded;
            pane.MuxAdoptionLost -= OnPaneMuxAdoptionLost;
            pane.OscNotificationReceived -= OnPaneOscNotification;
            pane.ProgressReported -= OnPaneProgressReported;

            pane.RequestRemoteFilesSidebarTransfer += OnPaneRequestRemoteFilesSidebarTransfer;
            pane.WorkingDirectoryChanged += OnPaneWorkingDirectoryChanged;
            pane.TitleChanged += OnPaneTitleChanged;
            pane.PaneActionRequested += OnPaneActionRequested;
            pane.OutputReceived += OnPaneOutputReceived;
            pane.BellReceived += OnPaneBellReceived;
            pane.ProcessExited += OnPaneProcessExited;
            pane.LongCommandCompleted += OnPaneLongCommandCompleted;
            pane.PersistentSessionAttached += OnPanePersistentSessionAttached;
            pane.PersistenceNotice += OnPanePersistenceNotice;
            pane.MuxSharingChanged += OnPaneMuxSharingChanged;
            pane.MuxShareEnded += OnPaneMuxShareEnded;
            pane.MuxAdoptionLost += OnPaneMuxAdoptionLost;
            pane.OscNotificationReceived += OnPaneOscNotification;
            pane.ProgressReported += OnPaneProgressReported;
        }

        private void UnwirePane(TerminalPane pane)
        {
            // The pane stops reporting: re-derive its tab's marker once the layout has let it go.
            if (pane.MuxOtherClients > 0 && ResolveOwningTabForPane(pane) is { } sharedTab)
            {
                this.Dispatcher.Post(() => RefreshTabSharedMarker(sharedTab), DispatcherPriority.Background);
            }

            // Before the owner map goes: a closing pane withdraws its tab progress so the
            // bar/taskbar don't keep showing a dead report.
            WithdrawPaneProgress(pane);
            _paneOwnerTab.Remove(pane);
            pane.RequestRemoteFilesSidebarTransfer -= OnPaneRequestRemoteFilesSidebarTransfer;
            pane.WorkingDirectoryChanged -= OnPaneWorkingDirectoryChanged;
            pane.TitleChanged -= OnPaneTitleChanged;
            pane.PaneActionRequested -= OnPaneActionRequested;
            pane.OutputReceived -= OnPaneOutputReceived;
            pane.BellReceived -= OnPaneBellReceived;
            pane.ProcessExited -= OnPaneProcessExited;
            pane.LongCommandCompleted -= OnPaneLongCommandCompleted;
            pane.PersistentSessionAttached -= OnPanePersistentSessionAttached;
            pane.PersistenceNotice -= OnPanePersistenceNotice;
            pane.MuxSharingChanged -= OnPaneMuxSharingChanged;
            pane.MuxShareEnded -= OnPaneMuxShareEnded;
            pane.MuxAdoptionLost -= OnPaneMuxAdoptionLost;
            pane.OscNotificationReceived -= OnPaneOscNotification;
            pane.ProgressReported -= OnPaneProgressReported;
        }

        /// <summary>
        /// UI thread. "Attach to session…" chose a session that was gone by the time the pane attached:
        /// say so and close the pane (it has no session, so the close kills nothing).
        /// </summary>
        private void OnPaneMuxShareEnded(TerminalPane pane)
        {
            EnqueueNotice(TerminalPane.MuxShareEndedNoticeTitle, TerminalPane.MuxShareEndedBanner);
            _ = CloseSessionlessPaneAsync(pane, "whose shared session ended");
        }

        /// <summary>UI thread. An adopted orphan another instance claimed first: the tab goes quietly (it has no session).</summary>
        private void OnPaneMuxAdoptionLost(TerminalPane pane) => _ = CloseSessionlessPaneAsync(pane, "whose adopted session is open elsewhere");

        private async Task CloseSessionlessPaneAsync(TerminalPane pane, string which)
        {
            try
            {
                await ClosePaneAsync(pane, skipConfirm: true);
            }
            catch (Exception ex)
            {
                // Fire-and-forget: nothing else observes it. The pane's own banner stays as the fallback.
                AppLogger.Log($"[MainWindow] closing a pane {which} failed: {ex.Message}");
            }
        }

        /// <summary>UI thread. A tab is marked shared while any of its panes has other clients attached.</summary>
        private void OnPaneMuxSharingChanged(TerminalPane pane)
        {
            if (ResolveOwningTabForPane(pane) is { } tab) RefreshTabSharedMarker(tab);
        }

        private void RefreshTabSharedMarker(TabItem tab)
        {
            // A posted refresh can outlive its tab; recreating the closed tab's state would leak it.
            if (this.FindControl<TabControl>("Tabs")?.Items.Contains(tab) != true) return;
            TabRuntimeState state = GetOrCreateTabState(tab);
            bool shared = EnumeratePanes(GetLayoutRootForTab(tab)).Any(p => p.MuxOtherClients > 0);
            if (state.IsShared == shared) return;
            state.IsShared = shared;
            QueueTabVisualRefresh(tab);
        }

        private int _sessionSaveQueued; // 1 while a coalesced save is posted

        /// <summary>
        /// A crash right after launch must still know which daemon sessions are this window's
        /// (spec §9): save the session file after each attach, coalesced into one Background pass.
        /// Shells already ended for a close or an update (<see cref="_localSessionsEndedOnClose"/>)
        /// stay out of it, as from every other save: a pass that runs after the update's own save
        /// must not name them again.
        /// </summary>
        private void OnPanePersistentSessionAttached(TerminalPane pane)
        {
            _ = pane;
            if (Interlocked.Exchange(ref _sessionSaveQueued, 1) == 1) return;
            Dispatcher.UIThread.Post(() =>
            {
                Volatile.Write(ref _sessionSaveQueued, 0);
                // After teardown the connection is gone; the teardown's own save is the last word.
                if (_teardownDone) return;
                SaveSessionWithoutEndedShells();
            }, DispatcherPriority.Background);
        }

        private void OnPaneRequestRemoteFilesSidebarTransfer(TerminalPane srcPane, SidebarTransferRequest request)
        {
            if (RefusedOnARetargetedHost(srcPane)) return;
            _ = InitiateSidebarSftpTransfer(srcPane, request.Direction, request.Kind, request.RemotePath);
        }

        /// <summary>
        /// A transfer on a persisted remote tab runs as on a plain SSH tab, over a connection of its own (Phase 5 spec R8) -
        /// unless the tab's host is retargeted (<see cref="TerminalPane.IsOnRetargetedHost"/>; spec §15, codex D2): the
        /// transfer rebuilds its options from the stored profile and would reach the profile's new destination, not the
        /// one this tab's shell runs on. Then the window says why and does nothing else.
        /// </summary>
        private bool RefusedOnARetargetedHost(TerminalPane pane)
        {
            if (!pane.IsOnRetargetedHost) return false;
            EnqueueNotice(TerminalPane.RemoteFilesUnavailableNoticeTitle, TerminalPane.RemoteFilesRetargetedMessage);
            return true;
        }

        private void OnPaneWorkingDirectoryChanged(TerminalPane srcPane, string cwd)
        {
            _ = srcPane;
            _ = cwd;
            Dispatcher.UIThread.Post(() => UpdateTabVisuals());
        }

        private void OnPaneTitleChanged(TerminalPane srcPane, string title)
        {
            _ = srcPane;
            _ = title;
            Dispatcher.UIThread.Post(() => UpdateTabVisuals());
        }

        private void OnPaneOutputReceived(TerminalPane pane)
        {
            var tab = ResolveOwningTabForPane(pane);
            if (tab == null) return;

            var tabState = GetOrCreateTabState(tab);
            tabState.Status.NoteOutput(DateTime.UtcNow);
            tabState.PreviewDirty = true;

            if (TryGetSelectedTab(out var selectedTabForStartup) && selectedTabForStartup == tab && ResolvePaneForTab(selectedTabForStartup) == pane)
            {
                _startup.Mark(StartupPhase.FirstTerminalReady);
            }

            if (!TryGetSelectedTab(out var selectedTab) || selectedTab != tab)
            {
                var state = GetOrCreateTabState(tab);
                state.HasActivity = true;
                QueueTabVisualRefresh(tab);
            }
        }

        private void OnPaneBellReceived(TerminalPane pane)
        {
            // ResolveOwningTabForPane rather than a tree walk (#314): it carries the
            // _paneOwnerTab cache and a rebuild fallback, so it also answers for a pane whose
            // tab is currently zoomed — EnterPaneZoom detaches the tab's other panes from the
            // logical tree, and a bell from one of those should still mark the tab. The previous
            // FindAncestorOfType<TabItem> call was a visual-tree lookup that never resolved for
            // any pane, so this handler returned here every time and the bell indicator had
            // never once appeared.
            var tab = ResolveOwningTabForPane(pane);
            if (tab == null) return;

            if (!TryGetSelectedTab(out var selectedTab) || selectedTab != tab)
            {
                var state = GetOrCreateTabState(tab);
                var now = DateTime.UtcNow;
                if ((now - state.LastBellUtc) < BellDebounceWindow)
                {
                    return;
                }

                state.LastBellUtc = now;
                state.HasBell = true;
                state.Status.NoteBell();
                QueueTabVisualRefresh(tab);
            }
        }

        private void OnPaneLongCommandCompleted(TerminalPane pane, string? commandText, int? exitCode, TimeSpan duration)
        {
            // Policy: opt-in, and only when the user isn't already looking at
            // this pane (a different pane is current, or the window is in the
            // background). The pane already applied the duration threshold.
            if (!LongCommandNotificationPolicy.ShouldNotify(
                    _settings.LongCommandNotificationsEnabled,
                    windowActive: IsActive,
                    isCurrentPane: ReferenceEquals(pane, _currentPane)))
            {
                return;
            }

            ShowRecordingToast(
                "Command finished",
                LongCommandNotificationPolicy.BuildMessage(commandText, exitCode, duration, pane.GetBaseTabTitle()),
                filePath: null,
                folderPath: null,
                autoHide: true);
        }

        private void OnPaneOscNotification(TerminalPane pane, string text)
        {
            // Same policy as the long-command toast — opt-in, suppressed while the user
            // is looking at the pane. The difference is provenance: the program asked
            // for this ping explicitly (OSC 9, #271 — e.g. Claude Code's completion
            // notification) rather than the terminal inferring "long command finished".
            if (!LongCommandNotificationPolicy.ShouldNotify(
                    _settings.Osc9NotificationsEnabled,
                    windowActive: IsActive,
                    isCurrentPane: ReferenceEquals(pane, _currentPane)))
            {
                return;
            }

            ShowRecordingToast(
                pane.GetBaseTabTitle(),
                text,
                filePath: null,
                folderPath: null,
                autoHide: true);
        }

        private void OnPaneProgressReported(TerminalPane pane, TerminalProgressReport? report)
        {
            var tab = ResolveOwningTabForPane(pane);
            if (tab == null) return;

            var state = GetOrCreateTabState(tab);
            if (report is null)
            {
                // Withdraw only what this pane owns; a split's other pane may still be
                // mid-report.
                if (!ReferenceEquals(state.ProgressPane, pane)) return;
                state.Progress = null;
                state.ProgressPane = null;
            }
            else
            {
                state.Progress = report;
                state.ProgressPane = pane;
            }

            QueueTabVisualRefresh(tab);
            RefreshTaskbarProgressFromSelection();
        }

        /// <summary>A pane went away (closed or exited): drop its progress report so the
        /// tab bar and taskbar don't keep advertising a dead operation.</summary>
        private void WithdrawPaneProgress(TerminalPane pane)
        {
            var tab = ResolveOwningTabForPane(pane);
            if (tab == null) return;

            // Lookup, not GetOrCreateTabState: CloseTab removes the tab's state BEFORE
            // the content dispose that unwires panes, so the create-on-miss path would
            // re-add an empty state entry for a tab that is already on its way out.
            if (!_tabStateByTab.TryGetValue(tab, out var state)) return;
            if (!ReferenceEquals(state.ProgressPane, pane)) return;

            state.Progress = null;
            state.ProgressPane = null;
            QueueTabVisualRefresh(tab);
            RefreshTaskbarProgressFromSelection();
        }

        /// <summary>
        /// The taskbar mirrors the SELECTED tab's OSC 9;4 progress (Windows shows one
        /// state per window). Re-run on every progress edge, tab switch, and pane
        /// removal; when the setting is off, this is what actively clears a state set
        /// before the toggle.
        /// </summary>
        private void RefreshTaskbarProgressFromSelection()
        {
            TerminalProgressReport? report = null;
            if (TryGetSelectedTab(out var tab))
            {
                report = GetOrCreateTabState(tab).Progress;
            }

            Taskbar.Apply(_settings.Osc9ProgressReportingEnabled ? report : null);
        }

        private void OnPaneProcessExited(TerminalPane pane, int exitCode)
        {
            // The pane's program is gone; its OSC 9;4 progress (if any) is stale.
            WithdrawPaneProgress(pane);

            // SSH panes write their own [SSH session disconnected] banner in HandleSessionExit and
            // never auto-close, so there is nothing left to do for them here. This has to run
            // before the tab==null branch below: an SSH pane that has already left the logical
            // tree by the time its shell exits must not also get a local banner written on top of
            // the one HandleSessionExit already wrote (#311 fix-wave finding B).
            if (pane.Profile?.Type == ConnectionType.SSH) return;

            var tab = pane.FindLogicalAncestorOfType<TabItem>();
            if (tab == null)
            {
                // No tab to close; the pane still has to say what happened.
                pane.WriteLocalExitBanner(exitCode);
                return;
            }

            // The tab strip says nothing about exit codes, deliberately (#311, then #314). The
            // ✓/✖N glyph it used to carry was written both per-command and on process exit and
            // rendered identically, so a dead shell looked exactly like a failed command — and
            // the only code that cleared it never ran, because it asked for its TabItem through
            // the visual tree. #314 resolved that by deleting the glyph rather than reviving an
            // ambiguous marker: a dead pane announces itself in the pane body instead. If a tab
            // marker is ever wanted, it needs its own state and its own reset, not this one.

            // A share of a shell that had already exited reports that exit with its attach: the user
            // opened it to look at, so it keeps its last screen whatever the policy says.
            if (pane.MuxShareOfExitedSession || !ShouldClosePaneOnExit(_settings.ShellExitPolicy, isSsh: false, exitCode))
            {
                pane.WriteLocalExitBanner(exitCode);
                return;
            }

            _ = HandlePaneExitCloseAsync(pane, exitCode);
        }

        /// <summary>
        /// #311: try to close a pane whose shell exited cleanly, and fall back to the banner when
        /// the close does not happen — a protected tab, an in-flight close, or a pane that has
        /// already left the tree. Every one of those paths has to end with a pane that says
        /// something rather than a pane that silently ignores you. This runs fire-and-forget from
        /// <see cref="OnPaneProcessExited"/>, so a throw here has no other observer: catch it,
        /// log it the same way other unexpected UI-side failures in this file do, and still fall
        /// back to the banner. There are three outcomes now: closed (nothing more to do), not
        /// closed (banner), and threw (log + banner).
        /// </summary>
        private async Task HandlePaneExitCloseAsync(TerminalPane pane, int exitCode)
        {
            bool closed;
            try
            {
                closed = await ClosePaneAsync(pane, skipConfirm: true);
            }
            catch (Exception ex)
            {
                TerminalLogger.Error($"[MainWindow] ClosePaneAsync failed while closing a pane whose shell exited (exit code {exitCode}): {ex.Message}");
                closed = false;
            }

            if (closed)
            {
                return;
            }

            try
            {
                pane.WriteLocalExitBanner(exitCode);
            }
            catch (Exception ex)
            {
                TerminalLogger.Error($"[MainWindow] WriteLocalExitBanner failed for a pane whose shell exited (exit code {exitCode}): {ex.Message}");
            }
        }

        private void OnPaneActionRequested(TerminalPane pane, PaneAction action)
        {
            UpdateActivePane(pane);
            FocusPaneTerminal(pane, defer: true);

            switch (action)
            {
                case PaneAction.SplitVertical:
                    SplitPane(Avalonia.Layout.Orientation.Horizontal);
                    break;
                case PaneAction.SplitHorizontal:
                    SplitPane(Avalonia.Layout.Orientation.Vertical);
                    break;
                case PaneAction.Equalize:
                    EqualizeCurrentSplit();
                    break;
                case PaneAction.ToggleZoom:
                    TogglePaneZoomForCurrentTab();
                    break;
                case PaneAction.ToggleBroadcast:
                    ToggleBroadcastForCurrentTab();
                    break;
                case PaneAction.Close:
                    CloseActivePane();
                    break;
            }
        }
    }
}
