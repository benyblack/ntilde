using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.LogicalTree;
using Avalonia.Controls.Presenters;
using Ntilde.Shell;
using Ntilde.VT;
using Avalonia;
using Avalonia.Media;
using Avalonia.Layout;

using Ntilde.Controls;

namespace Ntilde
{
    /// <summary>
    /// Closing tabs and panes: the running-process and shared-shell questions, detaching a pane's shell, and
    /// disposing what a closed pane leaves behind.
    /// </summary>
    public partial class MainWindow
    {
        private bool _closePaneInProgress;
        private bool _closeTabInProgress;

        /// <summary>The shared-close prompt (spec §7.4), given how many other clients show the shell. A seam so tests answer without a modal.</summary>
        internal Func<int, Task<Ntilde.Shell.Mux.SharedCloseChoice>> ConfirmSharedClose { get; set; }

        private void CloseActiveTab()
        {
            _ = CloseSelectedTabAsync();
        }

        private async Task CloseSelectedTabAsync()
        {
            if (!TryGetSelectedTab(out var selectedTab)) return;
            await CloseTabAsync(selectedTab);
        }

        private Task<bool> CloseTabAsync(TabItem tab, bool skipProcessChecks = false) =>
            CloseTabCoreAsync(tab, skipProcessChecks, Ntilde.Shell.Mux.PaneDisposition.EndSession);

        private async Task<bool> CloseTabCoreAsync(TabItem tab, bool skipProcessChecks, Ntilde.Shell.Mux.PaneDisposition requested)
        {
            if (_closeTabInProgress) return false;
            _closeTabInProgress = true;

            try
            {
                var tabState = GetOrCreateTabState(tab);
                if (tabState.IsProtected)
                {
                    return false;
                }

                if (_paneZoomStateByTab.ContainsKey(tab))
                {
                    ExitPaneZoom(tab, publishEvent: true);
                }

                var layoutRoot = GetLayoutRootForTab(tab);
                var detach = new HashSet<TerminalPane>();
                if (!skipProcessChecks && layoutRoot != null)
                {
                    foreach (var pane in EnumeratePanes(layoutRoot))
                    {
                        Ntilde.Shell.Mux.SharedCloseChoice choice = await DecidePaneCloseAsync(pane);
                        if (choice == Ntilde.Shell.Mux.SharedCloseChoice.Cancel)
                        {
                            UpdateActivePane(pane);
                            FocusPaneTerminal(pane, defer: true);
                            return false;
                        }

                        if (choice == Ntilde.Shell.Mux.SharedCloseChoice.Detach) detach.Add(pane);
                    }
                }

                PublishPaneEvent(tab, ResolvePaneForTab(tab), PaneAuditEventKind.Close, "tab");
                CloseTabCore(tab, requested, detach);
                return true;
            }
            finally
            {
                _closeTabInProgress = false;
            }
        }

        private void CloseTab(TabItem ti) => CloseTabCore(ti, Ntilde.Shell.Mux.PaneDisposition.EndSession, null);

        /// <param name="detach">Panes the user chose to detach rather than close; they override <paramref name="disposition"/>.</param>
        private void CloseTabCore(TabItem ti, Ntilde.Shell.Mux.PaneDisposition disposition, IReadOnlySet<TerminalPane>? detach)
        {
            if (_paneZoomStateByTab.ContainsKey(ti))
            {
                ExitPaneZoom(ti, publishEvent: false);
            }

            if (_activePaneByTab.TryGetValue(ti, out var mapped) && _currentPane == mapped)
            {
                _currentPane.RecordingStateChanged -= OnRecordingStateChanged;
                _currentPane.RecordingNotification -= OnRecordingNotification;
                _currentPane = null;
                OnRecordingStateChanged(false);
            }
            _activePaneByTab.Remove(ti);
            _paneZoomStateByTab.Remove(ti);
            _zoomedPaneIdByTab.Remove(ti);
            _broadcastEnabledTabs.Remove(ti);
            _tabIds.Remove(ti);
            _layoutModelByTab.Remove(ti);
            _tabMru.Remove(ti);
            _tabStateByTab.Remove(ti);
            _pendingVisualRefreshTabs.Remove(ti);

            if (ti.Content is Control content) DisposeControlTree(content, disposition, detach);
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs != null)
            {
                tabs.Items.Remove(ti);
                if (tabs.Items.Count == 0) Close();
            }

            // A closed tab is still referenced from places that outlive it - menu closures, cached
            // lookups - so cut it loose from its pane tree, which is where the memory is.
            ti.Content = null;

            UpdateTabVisuals();
            UpdatePaneAutomationLabels();
            UpdateBroadcastIndicator();
            RefreshAllLayoutModels();
        }

        private void CloseActivePane()
        {
            _ = CloseActivePaneAsync();
        }

        private Task CloseActivePaneAsync(bool skipConfirm = false)
        {
            if (_currentPane == null) return Task.CompletedTask;
            return ClosePaneAsync(_currentPane, skipConfirm);
        }

        /// <summary>
        /// Closes a specific pane, resolving everything from that pane rather than from the
        /// selection. The distinction matters for #311: a shell can die in a background tab, and
        /// the old selection-based fallback would have closed the tab the user was looking at.
        /// Returns true when the pane (or its tab) actually went away — callers use that to fall
        /// back to a banner when a protected tab or an in-flight close refuses.
        /// </summary>
        private Task<bool> ClosePaneAsync(TerminalPane pane, bool skipConfirm = false) =>
            ClosePaneCoreAsync(pane, skipConfirm, Ntilde.Shell.Mux.PaneDisposition.EndSession);

        private async Task<bool> ClosePaneCoreAsync(TerminalPane pane, bool skipConfirm, Ntilde.Shell.Mux.PaneDisposition requested)
        {
            if (_closePaneInProgress || pane == null) return false;
            _closePaneInProgress = true;

            try
            {
                var paneToClose = pane;
                // FindAncestorOfType<TabItem> (visual tree) never resolves here: Avalonia's
                // TabControl renders only the selected item's Content through its own
                // PART_SelectedContentHost presenter, so a TabItem is never a visual ancestor of
                // its own Content — selected or not. The logical tree is what's actually reliable
                // (ContentControl sets the logical parent immediately on assignment, same
                // mechanism the split-detection Parent check below already leans on).
                var paneTab = paneToClose.FindLogicalAncestorOfType<TabItem>();
                if (paneTab == null)
                {
                    // A zoomed tab's EnterPaneZoom replaces TabItem.Content with only the zoomed
                    // pane, so the tab's original split Grid — and every non-zoomed sibling still
                    // inside it — is detached from the logical tree and has no TabItem ancestor.
                    // Falling through to the split branch below for such a pane would clear that
                    // detached Grid's Children while its Parent is null: none of the promotion
                    // branches match, and the sibling pane is silently orphaned with its shell
                    // still running. Returning false here instead lets the caller fall back to a
                    // banner (#311 fix-wave finding C).
                    return false;
                }

                if (_paneZoomStateByTab.ContainsKey(paneTab))
                {
                    ExitPaneZoom(paneTab, publishEvent: true);
                }

                // Agent-initiated and exit-driven closes bypass the confirmation dialog: an agent
                // can't answer a modal, a dead shell has nothing left to lose, and an unattended
                // prompt is the stuck state #311 is about.
                Ntilde.Shell.Mux.PaneDisposition disposition = requested;
                if (!skipConfirm)
                {
                    Ntilde.Shell.Mux.SharedCloseChoice choice = await DecidePaneCloseAsync(paneToClose);
                    if (choice == Ntilde.Shell.Mux.SharedCloseChoice.Cancel)
                    {
                        FocusPaneTerminal(paneToClose, defer: true);
                        return false;
                    }

                    if (choice == Ntilde.Shell.Mux.SharedCloseChoice.Detach) disposition = Ntilde.Shell.Mux.PaneDisposition.Detach;
                }
                else if (requested == Ntilde.Shell.Mux.PaneDisposition.EndSession
                    && paneToClose.Session is Ntilde.Mux.MuxClientSession { IsConnected: true, IsProcessRunning: true, InteractiveOthers: > 0 })
                {
                    // An agent cannot answer the shared-close question, and skipping it must not end a shell
                    // other windows are still using: this pane lets go and the shell keeps running for them.
                    // Not a deliberate detach, so nothing is marked. InteractiveOthers is the cached v2 count,
                    // read-only observers excluded (always null on v1, which keeps the old close). An exit-driven close never gets here:
                    // its shell is no longer running.
                    disposition = Ntilde.Shell.Mux.PaneDisposition.Leave;
                }

                // Check if we are in a split (Parent is Grid with multiple children/splitter)
                if (paneToClose.Parent is Grid parentGrid && parentGrid.Children.Count >= 2)
                {
                    // We are in a split!
                    // 1. Identify Sibling (The non-splitter control that isn't us)
                    var sibling = parentGrid.Children.OfType<Control>()
                                        .FirstOrDefault(c => c != paneToClose && !(c is GridSplitter));

                    if (sibling != null)
                    {
                        // 2. Identify Grandparent
                        var grandParent = parentGrid.Parent;

                        // 3. Detach visuals
                        parentGrid.Children.Clear();

                        // 4. Promote Sibling to Grandparent
                        if (grandParent is ContentPresenter cp) cp.Content = sibling;
                        else if (grandParent is TabItem tab) tab.Content = sibling;
                        else if (grandParent is Grid gpGrid)
                        {
                            Grid.SetRow(sibling, Grid.GetRow(parentGrid));
                            Grid.SetColumn(sibling, Grid.GetColumn(parentGrid));
                            Grid.SetRowSpan(sibling, Grid.GetRowSpan(parentGrid));
                            Grid.SetColumnSpan(sibling, Grid.GetColumnSpan(parentGrid));

                            int index = gpGrid.Children.IndexOf(parentGrid);
                            if (index >= 0)
                            {
                                gpGrid.Children.RemoveAt(index);
                                gpGrid.Children.Insert(index, sibling);
                            }
                            else gpGrid.Children.Add(sibling);
                        }
                        else if (grandParent is Panel p)
                        {
                            int index = p.Children.IndexOf(parentGrid);
                            p.Children.Remove(parentGrid);
                            if (index >= 0) p.Children.Insert(index, sibling);
                            else p.Children.Add(sibling);
                        }

                        // 5. Dispose the closed pane
                        DisposeControlTree(paneToClose, disposition);

                        // 6. Focus Sibling
                        FocusFirstPane(sibling);
                        UpdatePaneAutomationLabels();
                        RefreshLayoutModelForTab(paneTab);
                        PublishPaneEvent(paneTab, paneToClose, PaneAuditEventKind.Close);
                        return true;
                    }
                }

                // Fallback: If not in a split, close the pane's own tab. paneTab is non-null from
                // the early return above, so there is no null branch left to guard here.
                return await CloseTabCoreAsync(paneTab, skipProcessChecks: true, disposition);
            }
            finally
            {
                _closePaneInProgress = false;
            }
        }

        /// <summary>
        /// Close, Detach or Cancel for one pane. A share whose sharing cannot be known now detaches, unasked (Phase 5 Task 26).
        /// A mux pane whose shell other clients also show asks the three-way question first; the count comes from listSessions
        /// (bounded, works on v1 too). Otherwise the Phase 2 decision (<see cref="ShouldClosePaneAsync"/>) stands.
        /// </summary>
        private async Task<Ntilde.Shell.Mux.SharedCloseChoice> DecidePaneCloseAsync(TerminalPane pane)
        {
            // Its link is down or reconnecting, or its attach pending: nobody can say who else uses that shell, and a close
            // only detaches it (DisposeControlTree, which says so), so there is nothing to ask - not even the running-process
            // question.
            if (pane.MuxShareIdWithSharingUnknown is Guid share)
            {
                TerminalLogger.Log($"[MainWindow] closing shared session {share}, whose other clients cannot be known now: detaching it, unasked");
                return Ntilde.Shell.Mux.SharedCloseChoice.Detach;
            }

            // One budget for both daemon reads (the sharing count here, the child-process probe in
            // ShouldClosePaneAsync): a stalled daemon costs a close about a second, not two.
            var budget = System.Diagnostics.Stopwatch.StartNew();
            if (pane.Session is Ntilde.Mux.MuxClientSession { IsConnected: true, IsAttached: true, IsProcessRunning: true } mux)
            {
                int? interactive; // clients that can type, this one included
                using var cts = new CancellationTokenSource(PaneCloseRefreshBudget);
                try
                {
                    interactive = await mux.RefreshSharingAsync(cts.Token).WaitAsync(PaneCloseRefreshBudget);
                }
                catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or Ntilde.Mux.Contracts.MuxProtocolException or ObjectDisposedException or IOException)
                {
                    interactive = mux.InteractiveOthers + 1; // stale is acceptable
                }

                if (interactive is int n && n > 1)
                {
                    try
                    {
                        return await ConfirmSharedClose(n - 1);
                    }
                    catch (Exception ex)
                    {
                        // A question that could not be asked is never read as "close it for everyone".
                        AppLogger.Log($"[MainWindow] the shared-close prompt failed: {ex.Message}");
                        return Ntilde.Shell.Mux.SharedCloseChoice.Cancel;
                    }
                }
            }

            TimeSpan left = PaneCloseRefreshBudget - budget.Elapsed;
            bool close = await ShouldClosePaneAsync(pane, left > TimeSpan.Zero ? left : TimeSpan.Zero);
            return close ? Ntilde.Shell.Mux.SharedCloseChoice.Close : Ntilde.Shell.Mux.SharedCloseChoice.Cancel;
        }

        /// <summary>How long a pane close waits on the daemon, in total, before deciding on cached values.</summary>
        private static readonly TimeSpan PaneCloseRefreshBudget = TimeSpan.FromSeconds(1);

        private async Task<Ntilde.Shell.Mux.SharedCloseChoice> ShowSharedCloseDialogAsync(int others)
        {
            (Window dialog, Task<Ntilde.Shell.Mux.SharedCloseChoice> result) = BuildSharedCloseWindow(others);
            await dialog.ShowDialog(this);
            return await result;
        }

        /// <summary>
        /// The shared-close dialog, not yet shown; the task completes with the choice (Cancel when the
        /// window is dismissed) when it closes. Enter detaches, Escape cancels, and Close (which ends
        /// the shell for everyone) is never the default. Separate from <see cref="ShowSharedCloseDialogAsync"/>
        /// so a headless test can show it and press keys.
        /// </summary>
        internal (Window Window, Task<Ntilde.Shell.Mux.SharedCloseChoice> Result) BuildSharedCloseWindow(int others)
        {
            var choice = Ntilde.Shell.Mux.SharedCloseChoice.Cancel;
            var dialog = CreateThemedDialogWindow("Close Shared Shell", 480, 200, canResize: false);
            string who = others == 1 ? "1 other window is" : $"{others} other windows are";
            var cancel = new Button { Content = "Cancel", Width = 92, IsCancel = true };
            var detach = new Button { Content = "Detach", Width = 92, IsDefault = true };
            var close = new Button { Content = "Close (ends it)", Width = 130 };
            var closed = new TaskCompletionSource<Ntilde.Shell.Mux.SharedCloseChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
            dialog.Closed += (_, _) => closed.TrySetResult(choice);
            // Detach holds focus, not Cancel: a focused button takes Enter for itself before IsDefault
            // is consulted, so a focused Cancel would make Enter cancel. Close is never focused.
            dialog.Opened += (_, _) => detach.Focus();
            cancel.Click += (_, _) => { choice = Ntilde.Shell.Mux.SharedCloseChoice.Cancel; dialog.Close(); };
            detach.Click += (_, _) => { choice = Ntilde.Shell.Mux.SharedCloseChoice.Detach; dialog.Close(); };
            close.Click += (_, _) => { choice = Ntilde.Shell.Mux.SharedCloseChoice.Close; dialog.Close(); };
            dialog.Content = new Border
            {
                Padding = new Thickness(16),
                Child = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock { Text = $"{who} attached to this shell.", FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = "Close ends the shell for every window. Detach closes only this pane and keeps the shell running.", TextWrapping = TextWrapping.Wrap },
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Spacing = 8,
                            Children = { cancel, detach, close },
                        },
                    },
                },
            };

            return (dialog, closed.Task);
        }

        private void DetachActivePane()
        {
            if (_currentPane is { } pane) _ = DetachPaneAsync(pane);
        }

        /// <summary>"Pane: Detach" (spec §7.4): the pane closes, the shell keeps running in the daemon.</summary>
        internal async Task<bool> DetachPaneAsync(TerminalPane pane)
        {
            if (pane.Session is not Ntilde.Mux.MuxClientSession { IsConnected: true } mux)
            {
                EnqueueNotice("Pane: Detach", pane.Session is Ntilde.Mux.MuxClientSession
                    ? "The multiplexer connection was lost."
                    : "Only a persistent shell can be detached.");
                return false;
            }

            // Its shell already exited: nothing would keep running, so this is a plain close (which
            // sends no kill for an exited shell) and no "kept running" toast.
            if (!mux.IsProcessRunning) return await ClosePaneAsync(pane, skipConfirm: true);

            // Read before the close: the pane is gone after it. A share whose sharing is unknown gets the notice from the
            // close itself (DisposeControlTree), so it is not said twice.
            string detached = ShellDetachedMessage(pane, mux.Id);
            bool noticedByTheClose = pane.IsMuxShareWithSharingUnknown;
            bool closed = await ClosePaneCoreAsync(pane, skipConfirm: true, Ntilde.Shell.Mux.PaneDisposition.Detach);
            if (closed && !_teardownDone && !noticedByTheClose) EnqueueNotice(ShellDetachedNoticeTitle, detached);
            return closed;
        }

        private const string ShellDetachedNoticeTitle = "Shell detached";

        /// <summary>
        /// The "Shell detached" notice's line for <paramref name="pane"/>'s shell <paramref name="sessionId"/> ("Pane: Detach",
        /// spec §7.4): it kept running, and how to get it back - for a remote shell, on which host
        /// (<see cref="RemoteDetachedMessage"/>). Read before the pane's close, which lets go of what it names.
        /// </summary>
        private string ShellDetachedMessage(TerminalPane pane, Guid sessionId)
        {
            Ntilde.Shell.Mux.MuxEndpointId endpoint = Ntilde.Shell.Mux.MuxEndpointId.Parse(pane.MuxEndpoint);
            if (endpoint.IsLocal) return "Shell kept running \u2014 Attach to session\u2026 to get it back";
            string remoteHost = pane.RemoteHostName is { Length: > 0 } named
                ? named
                : _muxHosts?.TryGet(endpoint)?.Policy.DisplayName ?? "its host";
            // The picker offers that host only while its profile keeps its sessions: read now, at the detach.
            bool reopenable = endpoint.SshProfileId is Guid profileId
                && _sshConnectionService.GetStoredProfile(profileId) is { MuxOptions.PersistRemoteSessions: true };
            return RemoteDetachedMessage(remoteHost, sessionId, reopenable);
        }

        /// <summary>
        /// The remote detach toast, naming the shell's host (<c>user@host</c>, as the remote banners say). A detached remote
        /// shell comes back through "Attach to session…" (Phase 5 spec §5) - its host listed while connected, offered as
        /// "Connect to …" once the detach let its connection go - while its profile keeps its sessions
        /// (<paramref name="reopenable"/>). Otherwise the picker does not offer it, and it comes back through
        /// <c>ntilde-mux attach</c> on its own host, by the full id <c>ntilde-mux ls</c> shows.
        /// </summary>
        internal static string RemoteDetachedMessage(string host, Guid sessionId, bool reopenable) => reopenable
            ? $"Shell kept running on {host} \u2014 Attach to session\u2026 reopens it"
            : $"Shell kept running on {host} \u2014 run 'ntilde-mux attach {sessionId}' on that host to get it back";

        /// <summary>Test seam: the budget the last <see cref="ShouldClosePaneAsync"/> was given.</summary>
        internal TimeSpan? LastPaneCloseRefreshBudgetForTest { get; private set; }

        /// <param name="refreshBudget">What is left of the close's daemon budget; zero decides on the cached probe.</param>
        private async Task<bool> ShouldClosePaneAsync(TerminalPane pane, TimeSpan refreshBudget)
        {
            // A mux session's child-process flag is a cached daemon probe: refresh it (bounded)
            // so the decision below is not made on a stale answer.
            LastPaneCloseRefreshBudgetForTest = refreshBudget;
            if (refreshBudget > TimeSpan.Zero) await RefreshPersistentSessionInfoAsync(pane.Session, refreshBudget);

            if (ShouldAutoAcceptRunningPaneClose(
                pane.IsProcessRunning,
                pane.HasActiveChildProcesses,
                pane.HasUserInteraction,
                pane.Profile?.Type,
                pane.Profile?.Command,
                pane.ShellArgs,
                _settings.PaneClosePolicy))
            {
                return true;
            }

            string policy = (_settings.PaneClosePolicy ?? "Confirm").Trim().ToLowerInvariant();
            switch (policy)
            {
                case "force":
                    return true;
                case "graceful":
                    try
                    {
                        pane.Session?.SendInput("\x03");
                        pane.Session?.SendInput("exit\r");
                    }
                    catch { }

                    await Task.Delay(150);
                    if (!pane.IsProcessRunning) return true;
                    return await ShowRunningProcessCloseConfirmationAsync("Process is still running in this pane.");
                default:
                    return await ShowRunningProcessCloseConfirmationAsync("Closing this pane will terminate the running process.");
            }
        }

        /// <summary>
        /// #311: whether a pane whose shell just exited should close itself. Protection and
        /// close-in-progress guards deliberately live outside this decision — the caller attempts
        /// the close and falls back to the banner if it does not happen, so that a pane which
        /// cannot close still says something.
        /// </summary>
        internal static bool ShouldClosePaneOnExit(string? shellExitPolicy, bool isSsh, int exitCode)
        {
            // A dropped SSH session keeps its [Press Enter to reconnect] banner regardless: the
            // remote end may have cost an MFA prompt or a jump host to reach.
            if (isSsh) return false;

            string policy = shellExitPolicy?.Trim() ?? string.Empty;

            if (policy.Equals("Never", StringComparison.OrdinalIgnoreCase)) return false;
            if (policy.Equals("Always", StringComparison.OrdinalIgnoreCase)) return true;
            if (policy.Equals("Graceful", StringComparison.OrdinalIgnoreCase)) return exitCode == 0;

            // Fall-through: an unrecognised or empty value behaves as "Never", which is now more
            // conservative than the default ("Graceful") rather than equal to it — deliberately. A
            // typo in a hand-edited settings file must not be more destructive than what the user
            // asked for, and keeping the pane is the recoverable outcome: "Never" still tells them
            // what happened through the exit banner, whereas a closed pane cannot be un-closed.
            return false;
        }

        internal static bool ShouldAutoAcceptRunningPaneClose(
            bool isProcessRunning,
            bool hasActiveChildProcesses,
            bool hasUserInteraction,
            ConnectionType? profileType,
            string? shellCommand,
            string? shellArgs,
            string? paneClosePolicy)
        {
            if (!isProcessRunning) return true;

            if (string.Equals(paneClosePolicy?.Trim(), "force", StringComparison.OrdinalIgnoreCase))
                return true;

            bool isWsl = shellCommand?.Contains("wsl", StringComparison.OrdinalIgnoreCase) == true;
            bool isSsh = profileType == ConnectionType.SSH;

            bool isSafeArgs = string.IsNullOrWhiteSpace(shellArgs);
            if (!isSafeArgs && isWsl)
            {
                // wsl profiles often have "-d Ubuntu" or similar as default args.
                // We treat these as safe to close without prompting.
                isSafeArgs = true;
            }

            if (isSsh)
            {
                // SSH connections are always considered precious and should warn on close
                // unless forced or process is dead.
                return false;
            }
            else if (isWsl)
            {
                // WSL is an opaque shell. We cannot see its Linux child processes.
                if (!hasUserInteraction && isSafeArgs)
                {
                    return true;
                }
            }
            else
            {
                // For native/transparent shells, we can trust explicit tracking.
                // If there are no active child processes, it's safe to close silently,
                // even if the user has been interacting with it.
                if (!hasActiveChildProcesses && isSafeArgs)
                {
                    return true;
                }
            }

            return false;
        }

        /// <param name="disposition">
        /// EndSession for a close: a mux shell is killed rather than left running detached in the daemon
        /// (spec §9). Detach for Pane: Detach and the shared prompt's Detach (Phase 3 spec §7.4).
        /// </param>
        /// <param name="detach">Panes detached whatever <paramref name="disposition"/> says (a tab close's per-pane Detach answers).</param>
        private void DisposeControlTree(Control control, Ntilde.Shell.Mux.PaneDisposition disposition = Ntilde.Shell.Mux.PaneDisposition.EndSession, IReadOnlySet<TerminalPane>? detach = null)
        {
            // All call sites are UI event paths, but marshal defensively: the UI-affine
            // detach below throws VerifyAccess off the UI thread.
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => DisposeControlTree(control, disposition, detach));
                return;
            }

            if (control is TerminalPane pane)
            {
                // Read first: the teardown below lets go of the pane's session and remote host.
                Guid? shareOfUnknownSharing = pane.MuxShareIdWithSharingUnknown;
                string? detachedMessage = shareOfUnknownSharing is Guid share ? ShellDetachedMessage(pane, share) : null;
                UnwirePane(pane);

                // The pane's scrollback and glyph atlases are only reclaimable after a full GC, and
                // nothing else would trigger one in an idle window.
                IdleMemoryReclaimer.RequestIdleCollection();

                // Two-phase teardown (#154): UI-affine detach runs here on the UI thread;
                // only the potentially blocking session teardown moves to the pool.
                // Previously the whole pane.Dispose() ran in Task.Run with a swallowed
                // catch, so a VerifyAccess throw aborted teardown before the session was
                // disposed, leaking the PTY and its child shell.
                var session = pane.DetachFromUiThread();
                Ntilde.Shell.Mux.PaneDisposition effective = detach?.Contains(pane) == true ? Ntilde.Shell.Mux.PaneDisposition.Detach : disposition;
                if (effective == Ntilde.Shell.Mux.PaneDisposition.EndSession && shareOfUnknownSharing is not null)
                {
                    // Phase 5 Task 26, local and remote alike: a share whose sharing cannot be known now (its link down or
                    // reconnecting, its attach pending) may be another client's shell. However the close came - an agent's,
                    // one that skipped the question, a Close answered as the link dropped - it detaches: no kill now, and
                    // none queued for the next connect.
                    TerminalLogger.Log($"[MainWindow] closing shared session {shareOfUnknownSharing}, whose other clients cannot be known now: detaching it, not ending it");
                    effective = Ntilde.Shell.Mux.PaneDisposition.Detach;
                }

                // Fix round 1, I1: the pane goes and its shell does not, so the window says so, as "Pane: Detach" does - unless
                // the window itself is going, which leaves every shell running anyway.
                if (effective == Ntilde.Shell.Mux.PaneDisposition.Detach && detachedMessage is not null && !_teardownDone)
                {
                    EnqueueNotice(ShellDetachedNoticeTitle, detachedMessage);
                }

                // Here on the UI thread, not in the Task.Run below (see KillMuxSessionOnClose). Detach: no kill.
                KillMuxSessionOnClose(session, effective, pane.MuxEndpoint);
                if (session is not Ntilde.Mux.MuxClientSession)
                {
                    KillPendingRemoteMuxSessionOnClose(pane.MuxSessionIdToRestore, effective, pane.MuxEndpoint);
                }
                if (effective != Ntilde.Shell.Mux.PaneDisposition.EndSession && session is Ntilde.Mux.MuxClientSession detaching)
                {
                    // A deliberate detach (the command, or the shared prompt's Detach): tell a v2 daemon, so the
                    // next launch does not adopt it back (spec §7.7). Leave is a plain detach. On the UI thread;
                    // the pool Dispose below is then a no-op.
                    detaching.Detach(userDetached: effective == Ntilde.Shell.Mux.PaneDisposition.Detach);
                }

                // After its kill is queued (or its detach sent): if no pane needs that remote connection any more, it goes.
                if (!Ntilde.Shell.Mux.MuxEndpointId.Parse(pane.MuxEndpoint).IsLocal) ScheduleRemoteMuxHostRelease();

                if (session != null)
                {
                    Task.Run(() =>
                    {
                        try { session.Dispose(); }
                        catch (Exception ex)
                        {
                            // Debug.WriteLine is compiled out of Release builds; use the
                            // logger so production dispose failures leave a trace.
                            TerminalLogger.Log($"[MainWindow] Session dispose failed: {ex.Message}");
                        }
                    });
                }
            }
            else if (control is Panel panel) { foreach (var child in panel.Children) if (child is Control c) DisposeControlTree(c, disposition, detach); }
            else if (control is ContentPresenter cp && cp.Content is Control childContent) DisposeControlTree(childContent, disposition, detach);
        }
    }
}
