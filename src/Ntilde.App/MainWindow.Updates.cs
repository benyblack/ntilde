using Avalonia.Controls;
using Avalonia.Threading;
using Ntilde.Shell;
using Ntilde.VT;

using Ntilde.Controls;

namespace Ntilde
{
    /// <summary>
    /// App updates: the automatic and manual checks, the update toast, and applying a staged update around the
    /// mux daemon.
    /// </summary>
    public partial class MainWindow
    {
        private Ntilde.Update.UpdateCoordinator? _updateCoordinator;

        /// <summary>Test seam: installs a coordinator over a fake IUpdateService.</summary>
        internal void SetUpdateCoordinatorForTest(Ntilde.Update.UpdateCoordinator? coordinator) => _updateCoordinator = coordinator;

        /// <summary>
        /// Test seam: how <see cref="ApplyStagedUpdateAsync"/> checks for a live daemon before
        /// applying an update. The default never spawns one - a daemon that is not already up
        /// does not need to be asked about.
        /// </summary>
        internal Func<CancellationToken, Task<Ntilde.Mux.MuxClient?>> MuxProbeForUpdate { get; set; } =
            ct => Ntilde.Mux.Daemon.MuxDaemonLauncher.CreateDefault(AppLogger.Log, Ntilde.Shell.Mux.MuxCommand.ServeArguments).TryConnectExistingAsync(ct);

        /// <summary>Test seam: the daemon's descriptor, read just before <c>shutdown</c> is sent for an update.</summary>
        internal Func<Ntilde.Mux.Contracts.MuxEndpointDescriptor?> MuxReadDescriptorForUpdate { get; set; } =
            () => Ntilde.Mux.Contracts.MuxDiscovery.TryReadDescriptor(Ntilde.Mux.Contracts.MuxDiscovery.GetDescriptorPath(), out var d) ? d : null;

        /// <summary>
        /// Test seam: after <c>shutdown</c> was sent for an update, completes with true once the daemon
        /// named by the descriptor has exited, or false after 5 s. Runs off the UI thread.
        /// </summary>
        internal Func<Ntilde.Mux.Contracts.MuxEndpointDescriptor, Task<bool>> MuxWaitForDaemonExitForUpdate { get; set; } =
            before => Task.Run(() => Ntilde.Mux.Daemon.MuxDaemonExit.WaitForExit(
                Ntilde.Mux.Contracts.MuxDiscovery.GetDescriptorPath(), before, TimeSpan.FromSeconds(5), Environment.ProcessId));

        /// <summary>Test seam: the confirmation shown when an update would close running mux sessions.</summary>
        internal Func<string, Task<bool>> ConfirmSessionLossForUpdate { get; set; }

        /// <summary>
        /// Test seam: the Velopack install root whose every process an update's apply kills (Phase 5 R9), or null when
        /// this is not a Windows Velopack install, whose apply kills nothing.
        /// </summary>
        internal Func<string?> MuxInstallRootForUpdate { get; set; } = Ntilde.Shell.Mux.MuxDaemonImage.CurrentVelopackInstallRoot;

        /// <summary>
        /// Test seam: the executable the daemon a descriptor names runs from, or null when that cannot be told
        /// (<see cref="Ntilde.Update.MuxUpdateCompatibility.DaemonImagePath"/>). Runs off the UI thread.
        /// </summary>
        internal Func<Ntilde.Mux.Contracts.MuxEndpointDescriptor, string?> MuxDaemonImagePathForUpdate { get; set; } =
            Ntilde.Update.MuxUpdateCompatibility.DaemonImagePath;
        // Prevents a manual "Check for updates" from racing the automatic check (or a second
        // manual invocation) into the same staging directory - UpdateCoordinator.RunCheckAsync
        // has no serialization of its own.
        private bool _updateCheckInFlight;
        // Guards ApplyStagedUpdateAsync against re-entrancy: the toast Restart button, the palette
        // command and About's button all reach it, and nothing else stops a second click landing
        // while the first is still probing the daemon or awaiting the confirmation dialog.
        private bool _applyStagedUpdateInProgress;

        /// <summary>
        /// Raises the update-ready notice. No auto-hide: unlike a recording toast this is an
        /// offer the user may take minutes later, and closing it only dismisses the notice - the
        /// update stays staged.
        /// </summary>
        private void ShowUpdateToast(string version)
        {
            var toast = this.FindControl<Border>("UpdateToast");
            var messageBlock = this.FindControl<TextBlock>("UpdateToastMessage");
            if (toast == null || messageBlock == null)
            {
                return;
            }

            messageBlock.Text = $"Ntilde {version} is downloaded and will be applied when you restart.";
            toast.IsVisible = true;
        }

        private void HideUpdateToast()
        {
            var toast = this.FindControl<Border>("UpdateToast");
            if (toast != null)
            {
                toast.IsVisible = false;
            }
        }

        /// <summary>
        /// Builds the update coordinator on first use, at most once per process. Deliberately not
        /// called from <see cref="OnOpened"/> directly: <see cref="StartupPerformanceTracker"/>
        /// exists because that interval is measured, and constructing
        /// <see cref="Ntilde.Update.VelopackUpdateService"/> means an assembly load plus a
        /// filesystem probe (its own doc comment explains why that constructor can throw on a
        /// non-Velopack host) - work with no business inside a measured interval, and, unlike the
        /// <c>_globalHotkey</c> block above in <see cref="OnOpened"/>, not previously guarded
        /// against throwing at all. Callers are the one-shot timer's tick (after the measured
        /// interval has closed) and the palette's manual check (a user action, never on the
        /// startup path even if it happens to land in the first 10 seconds).
        /// </summary>
        private void EnsureUpdateCoordinator()
        {
            if (_updateCoordinator != null)
            {
                return;
            }

            try
            {
                _updateCoordinator = new Ntilde.Update.UpdateCoordinator(
                    new Ntilde.Update.VelopackUpdateService(
                        Ntilde.Update.VelopackUpdateService.DefaultRepoUrl,
                        message => TerminalLogger.Log(message)),
                    () => _settings.AutomaticUpdateChecks,
                    version => Dispatcher.UIThread.Post(() =>
                    {
                        ShowUpdateToast(version);
                        SetupCommandPalette();
                    }),
                    message => TerminalLogger.Log(message));
            }
            catch (Exception ex)
            {
                TerminalLogger.Log("Update coordinator construction failed: " + ex);
            }
        }

        /// <summary>
        /// The background check's fire-and-forget entry point. Wrapping it here - rather than
        /// discarding <c>_updateCoordinator.RunAutomaticCheckAsync()</c> directly - matters
        /// because <see cref="Ntilde.Update.UpdateCoordinator.RunCheckAsync"/> only wraps
        /// the network call in its own try/catch; the <c>IsSupported</c> read and the
        /// <c>onUpdateReady</c> callback both sit outside it, so a throw from either would
        /// otherwise fault this task unobserved - invisible even in the log, which is the one
        /// thing the silent-failure design for the automatic check leans on. Also guards against
        /// racing a concurrent manual check into the same staging directory (see
        /// <see cref="_updateCheckInFlight"/>).
        /// </summary>
        private async System.Threading.Tasks.Task RunAutomaticCheckSafeAsync()
        {
            if (_updateCheckInFlight || _updateCoordinator == null)
            {
                return;
            }

            _updateCheckInFlight = true;
            try
            {
                // Task.Run, deliberately: this method is reached from a DispatcherTimer tick,
                // so everything up to the first real await would otherwise run on the UI
                // thread - and Velopack's prologue is not cheap. UpdateManager
                // .CheckForUpdatesAsync synchronously calls EnsureInstalled(),
                // Locator.GetOrCreateStagedUserId() (a file read plus write) and
                // GetLatestLocalFullPackage() -> GetLocalPackages(), which enumerates the
                // packages directory and parses the nuspec inside every .nupkg - i.e. inside a
                // ~150 MB self-contained bundle. On a cold disk, or with AV in the path, that
                // is a visible hitch ~10 s after launch. Do not "simplify" this back to a bare
                // await; the continuation still resumes on the UI thread, which is what the
                // outcome handling (ShowRecordingToast / ShowUpdateToast) needs.
                await Task.Run(() => _updateCoordinator.RunAutomaticCheckAsync());
            }
            catch (Exception ex)
            {
                TerminalLogger.Log("Automatic update check failed unexpectedly: " + ex);
            }
            finally
            {
                _updateCheckInFlight = false;
            }
        }

        /// <summary>
        /// The manual check's fire-and-forget entry point, for the same reason
        /// <see cref="RunAutomaticCheckSafeAsync"/> exists: <c>ExecuteCommand</c> invokes the
        /// palette action synchronously, so a bare <c>_ = CheckForUpdatesInteractiveAsync()</c>
        /// would leave any throw past the first await faulting an unobserved task - invisible
        /// even in the log. <see cref="CheckForUpdatesInteractiveAsync"/> handles every outcome
        /// the coordinator reports; this only catches what the coordinator cannot (a throwing
        /// <c>IsSupported</c>, a throwing toast) and makes sure the user still hears something.
        /// </summary>
        private async System.Threading.Tasks.Task RunManualCheckSafeAsync()
        {
            try
            {
                await CheckForUpdatesInteractiveAsync();
            }
            catch (Exception ex)
            {
                TerminalLogger.Log("Manual update check failed unexpectedly: " + ex);
                ShowRecordingToast("Update check failed", "Could not check for updates. See the debug log for details.", null, null, autoHide: true);
            }
        }

        /// <summary>
        /// Runs the shared app-teardown (session save, timers, global hotkey, agent host) before
        /// handing off to the coordinator. The underlying restart terminates this process itself,
        /// so <see cref="OnClosing"/> never runs for it - without doing the same teardown here
        /// first, "taking the update" would silently drop the session, which is worse than just
        /// quitting and relaunching by hand.
        /// </summary>
        /// <remarks>
        /// The try/catch lives here rather than at the call sites so that BOTH entry points get
        /// it: the toast's Restart button (a raw Click handler - an escaping exception there is
        /// an unhandled exception on the UI thread, which kills the app with no explanation) and
        /// the palette entry (whose <c>ExecuteCommand</c> try/catch would otherwise swallow the
        /// failure without telling the user anything). <c>ApplyUpdatesAndRestart</c> can throw
        /// for real reasons: a missing or locked <c>Update.exe</c>, or the update lock already
        /// held by another instance.
        /// </remarks>
        /// <summary>
        /// The toast's Restart button and the palette/About entries all invoke this as a plain
        /// <c>Action</c>, so it stays synchronous and fire-and-forget; <see cref="ApplyStagedUpdateAsync"/>
        /// is the real body and guarantees it never lets an exception escape as an unobserved task fault.
        /// </summary>
        private void ApplyStagedUpdate() => _ = ApplyStagedUpdateAsync();

        /// <summary>
        /// Runs the shared app-teardown (session save, timers, global hotkey, agent host) before
        /// handing off to the coordinator. The underlying restart terminates this process itself,
        /// so <see cref="OnClosing"/> never runs for it - without doing the same teardown here
        /// first, "taking the update" would silently drop the session, which is worse than just
        /// quitting and relaunching by hand.
        /// </summary>
        /// <remarks>
        /// The try/catch lives here rather than at the call sites so that BOTH entry points get
        /// it: the toast's Restart button (a raw Click handler - an escaping exception there is
        /// an unhandled exception on the UI thread, which kills the app with no explanation) and
        /// the palette entry (whose <c>ExecuteCommand</c> try/catch would otherwise swallow the
        /// failure without telling the user anything). <c>ApplyUpdatesAndRestart</c> can throw
        /// for real reasons: a missing or locked <c>Update.exe</c>, or the update lock already
        /// held by another instance.
        ///
        /// Before any of that, a live daemon is probed (spec §9). Phase 5 R10: when the update keeps
        /// it - the new build speaks its protocol (the staged release notes' marker) and the apply
        /// does not kill it (its image is outside the install root) - nothing is asked and nothing
        /// is shut down: the teardown detaches as any close does, and the new build reattaches.
        /// With SessionPersistence off no daemon is kept, as before Phase 5.
        /// Otherwise, if it has running sessions, the user is asked to confirm the loss; declining
        /// leaves everything untouched (no teardown, no apply). Confirming - or no running session -
        /// saves the session without the shells about to die (with persistence on; off, the teardown
        /// saves it afterwards, as before Phase 5), then sends <c>shutdown</c>, then proceeds exactly
        /// as before. If listing sessions fails, the count is unknown so there is
        /// nothing to confirm, but <c>shutdown</c> is still attempted best-effort - the alternative
        /// is the exact bug this method exists to prevent, a daemon the new build cannot use (or the
        /// apply would kill mid-flight) surviving the decision, just because a request on the way in
        /// happened to fail. A confirmation dialog that itself throws is treated as a decline
        /// (logged, toasted, no shutdown, no apply) rather than letting the exception escape this
        /// fire-and-forget method as an unobserved fault - "yes" must never be inferred from a
        /// question that could not be asked.
        ///
        /// Re-entrant: the toast button, the palette command and About's button can all reach this
        /// (see <see cref="ApplyStagedUpdate"/>), and nothing stops two of them firing before the
        /// first has finished probing. <see cref="_applyStagedUpdateInProgress"/> makes a second
        /// call while one is already running a no-op rather than double-probing/double-confirming.
        /// </remarks>
        internal async System.Threading.Tasks.Task ApplyStagedUpdateAsync()
        {
            if (_updateCoordinator is not { IsUpdateStaged: true })
            {
                return;
            }

            if (_applyStagedUpdateInProgress)
            {
                return;
            }

            _applyStagedUpdateInProgress = true;
            try
            {
                MuxUpdatePreparation prepared = await PrepareMuxDaemonForUpdateAsync();
                if (prepared == MuxUpdatePreparation.Cancel)
                {
                    return;
                }

                // The session was saved before the daemon was shut down: this teardown must not save over it.
                _sessionSavedBeforeUpdate = prepared == MuxUpdatePreparation.ApplySessionSaved;
                try
                {
                    PerformAppTeardown();
                }
                catch (Exception ex)
                {
                    // Outside the apply try before (PR #489 follow-up): a throw here escaped this
                    // fire-and-forget method as an unobserved task fault. Nothing is applied; the
                    // window stays up and its own close must run the teardown again.
                    TerminalLogger.Log("Tearing down before the update failed: " + ex);
                    _teardownDone = false;
                    ShowRecordingToast(
                        "Update could not be applied",
                        "The update was downloaded but could not be applied. Close Ntilde and start it again to finish updating.",
                        null,
                        null,
                        autoHide: false);
                    return;
                }
                finally
                {
                    // Only for that teardown: one run again after a failed apply saves as any close does.
                    _sessionSavedBeforeUpdate = false;
                }

                try
                {
                    _updateCoordinator.ApplyStagedUpdate();
                }
                catch (Exception ex)
                {
                    // Teardown already ran by this point, so the window is still up but the session
                    // has been saved and the agent host and global hotkey are stopped - the app is
                    // degraded, not healthy. The message has to say "restart manually" rather than
                    // "try again", because carrying on in this state is not a supported outcome.
                    TerminalLogger.Log("Applying the staged update failed: " + ex);
                    // The window stays up and the user is told to close it: that close must run the
                    // teardown again (above all SaveSession), not hit PerformAppTeardown's one-shot guard.
                    _teardownDone = false;
                    ShowRecordingToast(
                        "Update could not be applied",
                        "The update was downloaded but could not be applied. Close Ntilde and start it again to finish updating.",
                        null,
                        null,
                        autoHide: false);
                }
            }
            finally
            {
                _applyStagedUpdateInProgress = false;
            }
        }

        /// <summary>What the mux half of <see cref="ApplyStagedUpdateAsync"/> decided.</summary>
        private enum MuxUpdatePreparation
        {
            /// <summary>Declined, or the question could not be asked: nothing is torn down or applied.</summary>
            Cancel,

            /// <summary>
            /// Go ahead: no daemon, one the update keeps, or - with persistence off - one shut down as before Phase 5. The
            /// teardown saves the session as any close does.
            /// </summary>
            Apply,

            /// <summary>Go ahead: the daemon was shut down, and the session saved just before; the teardown leaves that file alone.</summary>
            ApplySessionSaved,
        }

        /// <summary>
        /// Set only around the update's own <see cref="PerformAppTeardown"/> call when the session was saved before the daemon
        /// was shut down: that teardown does not save again (a save after the shutdown would capture panes it ended, or
        /// closed by the exit policy).
        /// </summary>
        private bool _sessionSavedBeforeUpdate;

        /// <summary>
        /// The mux half of <see cref="ApplyStagedUpdateAsync"/> (spec §9, Phase 5 R10): probes for a live daemon and, when
        /// the update does not keep it, confirms the loss of its running sessions, saves the session without them, and shuts
        /// it down. A daemon the update keeps is left alone, unasked; with SessionPersistence off none is kept.
        /// </summary>
        private async System.Threading.Tasks.Task<MuxUpdatePreparation> PrepareMuxDaemonForUpdateAsync()
        {
            Ntilde.Mux.MuxClient? daemon = await ProbeMuxDaemonForUpdateAsync();
            if (daemon is null)
            {
                return MuxUpdatePreparation.Apply;
            }

            using (daemon)
            {
                // SessionPersistence off changes nothing from before Phase 5: no daemon is kept, and the question words it
                // as it always did - "the new version cannot keep them" may not be true of a daemon it could keep.
                bool persistenceOff = !Ntilde.Shell.Mux.SessionPersistenceMode.IsKeepOnClose(_settings.SessionPersistence);
                // None with persistence off: the question then gives no reason, as before Phase 5.
                Ntilde.Update.MuxUpdateStopReason why = Ntilde.Update.MuxUpdateStopReason.None;
                if (persistenceOff)
                {
                    AppLogger.Log("[MainWindow] session persistence is off; the update closes the multiplexer's sessions, as before Phase 5");
                }
                else if ((why = await WhyUpdateStopsMuxDaemonAsync()) == Ntilde.Update.MuxUpdateStopReason.None)
                {
                    return MuxUpdatePreparation.Apply;
                }

                if (!await ConfirmMuxSessionLossForUpdateAsync(daemon, why))
                {
                    return MuxUpdatePreparation.Cancel;
                }

                if (persistenceOff)
                {
                    // As before Phase 5: the teardown saves the session after the shutdown and its exit wait.
                    await ShutdownMuxDaemonForUpdateAsync(daemon);
                    return MuxUpdatePreparation.Apply;
                }

                // The shells about to die are left out of the session, saved now, before the shutdown: the file names
                // none of them, and the teardown does not save over it with panes the shutdown has since ended.
                MarkLocalShellsEnded();
                SaveSessionWithoutEndedShells();
                await ShutdownMuxDaemonForUpdateAsync(daemon);
                return MuxUpdatePreparation.ApplySessionSaved;
            }
        }

        /// <summary>
        /// Phase 5 R10: whether the staged update keeps the live daemon - the new build speaks its protocol (the release
        /// notes' marker; none counts as compatible) and the apply does not kill it (its image is outside the install root) -
        /// and if not, why (<see cref="Ntilde.Update.MuxUpdateStopReason"/>, which the question gives: PR #511 heads-up). A
        /// daemon whose descriptor cannot be read, or anything failing on the way, is not kept, as one whose image is unknown:
        /// today's question and shutdown. The image lookup runs off the UI thread. Never throws.
        /// </summary>
        private async System.Threading.Tasks.Task<Ntilde.Update.MuxUpdateStopReason> WhyUpdateStopsMuxDaemonAsync()
        {
            try
            {
                Ntilde.Mux.Contracts.MuxEndpointDescriptor? descriptor = MuxReadDescriptorForUpdate();
                if (descriptor is null)
                {
                    AppLogger.Log("[MainWindow] the multiplexer's descriptor could not be read; the update closes its sessions");
                    return Ntilde.Update.MuxUpdateStopReason.UnknownImage;
                }

                string? notes = _updateCoordinator?.StagedReleaseNotes;
                (int Min, int Max)? newBuild = Ntilde.Update.MuxUpdateCompatibility.ParseProtocolRange(notes);
                // A release build embeds the ntilde-mux pins; a dev build embeds none and stages no release's notes.
                if (Ntilde.Update.MuxUpdateCompatibility.MissingProtocolMarkerWarning(notes, Ntilde.Shell.Mux.Remote.MuxAssetPins.Load().Count > 0) is { } warning)
                {
                    AppLogger.Log(warning);
                }

                string? installRoot = MuxInstallRootForUpdate();
                string? image = installRoot is null ? null : await Task.Run(() => MuxDaemonImagePathForUpdate(descriptor));
                Ntilde.Update.MuxUpdateStopReason why = Ntilde.Update.MuxUpdateCompatibility.WhyUpdateStopsDaemon((descriptor.MinVersion, descriptor.MaxVersion), newBuild, image, installRoot);
                string newRange = newBuild is { } b ? $"{b.Min}-{b.Max}" : "not stated";
                string where = installRoot is null ? "the apply kills no process" : $"its image is {image ?? "unknown"}, the install root {installRoot}";
                string verdict = why == Ntilde.Update.MuxUpdateStopReason.None ? "keeps" : $"closes ({why})";
                AppLogger.Log($"[MainWindow] the update {verdict} the multiplexer: protocol {descriptor.MinVersion}-{descriptor.MaxVersion}, the new build's {newRange}; {where}");
                return why;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[MainWindow] deciding whether the update keeps the multiplexer failed; it closes its sessions: {ex.Message}");
                return Ntilde.Update.MuxUpdateStopReason.UnknownImage;
            }
        }

        /// <summary>
        /// Marks every local shell this window names ended (<see cref="_localSessionsEndedOnClose"/>): the local daemon is
        /// about to be shut down with all of them - Task 17's quit, an update that does not keep it - so no save names them
        /// for reattach, and the next launch starts fresh shells quietly instead of reporting them lost. A live pane's, and
        /// (PR #511 review, Greptile P2) every id held without a live pane: a pane's pending one and a startup placeholder's
        /// (<see cref="HeldLocalMuxSessionIds"/>). Shares included: the whole daemon stops, so a shared shell ends with it
        /// (the quit kills it by name, R20), as a live share pane's always was marked here; a tab not shown yet then reopens
        /// as a shown one does, with a fresh shell. Idempotent. The update marks them all before its shutdown, as it saves
        /// before it; the quit marks the held ids only once their end is known (<see cref="MarkHeldLocalShellsEnded"/>).
        /// </summary>
        private void MarkLocalShellsEnded()
        {
            MarkLivePaneShellsEnded();
            MarkHeldLocalShellsEnded(only: null);
        }

        /// <summary>The live local panes' half of <see cref="MarkLocalShellsEnded"/>.</summary>
        private void MarkLivePaneShellsEnded()
        {
            foreach (TerminalPane pane in _paneOwnerTab.Keys)
            {
                if (ShowsLocalMuxEndpoint(pane) && pane.Session is Ntilde.Mux.MuxClientSession mux) _localSessionsEndedOnClose.Add(mux.Id);
            }
        }

        /// <summary>
        /// The held half of <see cref="MarkLocalShellsEnded"/> (<see cref="HeldLocalMuxSessionIds"/>): every such id, or with
        /// <paramref name="only"/> just those in it - the quit's, when its stop failed, are the ones it killed by name.
        /// </summary>
        private void MarkHeldLocalShellsEnded(IReadOnlySet<Guid>? only)
        {
            foreach ((Guid id, _, _) in HeldLocalMuxSessionIds())
            {
                if (only is null || only.Contains(id)) _localSessionsEndedOnClose.Add(id);
            }
        }

        /// <summary>
        /// Saves the startup session file, leaving out the local shells already ended (<see cref="_localSessionsEndedOnClose"/>).
        /// Every save of that file goes through here: the teardown's, each attach's coalesced one, and the update's.
        /// </summary>
        private void SaveSessionWithoutEndedShells()
        {
            if (this.FindControl<TabControl>("Tabs") is { } tabs) SessionManager.SaveSession(this, tabs, _localSessionsEndedOnClose);
        }

        private async System.Threading.Tasks.Task<Ntilde.Mux.MuxClient?> ProbeMuxDaemonForUpdateAsync() =>
            (await ProbeLocalDaemonAsync("the update")).Daemon;

        /// <summary>
        /// The live local daemon, never starting one; null when none answers. <c>VersionMismatch</c> says the one there
        /// speaks another protocol version (it cannot be asked anything, only terminated). <paramref name="purpose"/> is
        /// what the caller is doing, for the log.
        /// </summary>
        private async System.Threading.Tasks.Task<(Ntilde.Mux.MuxClient? Daemon, bool VersionMismatch)> ProbeLocalDaemonAsync(string purpose)
        {
            try
            {
                return (await MuxProbeForUpdate(CancellationToken.None), false);
            }
            catch (Exception ex)
            {
                // Best-effort: a daemon that cannot even be reached is not one the caller
                // needs to wait on (there is no client to send `shutdown` to either).
                AppLogger.Log($"[MainWindow] mux probe before {purpose} failed: {ex.Message}");
                return (null, ex is Ntilde.Mux.Daemon.MuxUnavailableException { VersionMismatch: true });
            }
        }

        /// <summary>
        /// True to go ahead: no running sessions, an unknown count, or the user confirmed. <paramref name="why"/> is the reason
        /// the question gives (<see cref="Ntilde.Update.MuxUpdateCompatibility.SessionLossQuestion"/>): the protocol, the install
        /// folder, or a daemon that could not be checked - each only when the update was asked to keep the daemon and could
        /// not. <see cref="Ntilde.Update.MuxUpdateStopReason.None"/> (persistence off) words it as before Phase 5.
        /// </summary>
        private async System.Threading.Tasks.Task<bool> ConfirmMuxSessionLossForUpdateAsync(Ntilde.Mux.MuxClient daemon, Ntilde.Update.MuxUpdateStopReason why)
        {
            IReadOnlyList<Ntilde.Mux.Contracts.SessionSummary> sessions;
            try
            {
                sessions = await daemon.ListSessionsAsync();
            }
            catch (Exception ex)
            {
                // The session count is now unknown, so there is nothing to confirm -
                // but the daemon answered the probe, so it is still there to shut down.
                AppLogger.Log($"[MainWindow] mux list-sessions before update failed: {ex.Message}");
                return true;
            }

            int running = sessions.Count(s => s.Running);
            if (running == 0)
            {
                return true;
            }

            try
            {
                return await ConfirmSessionLossForUpdate(Ntilde.Update.MuxUpdateCompatibility.SessionLossQuestion(running, why));
            }
            catch (Exception ex)
            {
                // Treated as a decline: a confirmation dialog that cannot even
                // ask the question must not be read as "yes, close the sessions"
                // - the safer failure is leaving the daemon and the update alone.
                AppLogger.Log($"[MainWindow] mux update confirmation failed: {ex.Message}");
                ShowRecordingToast(
                    "Update not applied",
                    "Could not confirm closing the multiplexed sessions; try again.",
                    null,
                    null,
                    autoHide: false);
                return false;
            }
        }

        /// <summary>How the probe before a stop found the local daemon.</summary>
        private enum LocalDaemonProbe
        {
            /// <summary>It answered: there is a connection to it.</summary>
            Reached,

            /// <summary>None answered, and no descriptor advertises one.</summary>
            NotRunning,

            /// <summary>One is advertised, but none answered (hung, or just gone).</summary>
            Unreachable,

            /// <summary>One answered as another protocol version: it cannot be asked anything, only terminated.</summary>
            VersionMismatch,
        }

        /// <summary>How <see cref="ShutdownLocalDaemonAsync"/> ended.</summary>
        private enum LocalDaemonStop
        {
            /// <summary>The daemon is known to be gone: it exited after <c>shutdown</c>, or was terminated.</summary>
            Gone,

            /// <summary>No daemon was reached, and none was there to terminate.</summary>
            NoneReached,

            /// <summary>The restart's last look left the daemon alone: nothing was sent.</summary>
            LeftAlone,

            /// <summary><c>shutdown</c> went out, but no descriptor said which process to watch: the stop cannot be checked.</summary>
            StopUnconfirmed,

            /// <summary>A daemon was there, and is not known to be gone.</summary>
            NotStopped,
        }

        /// <summary>
        /// Probes for the local daemon and, when one is up, sends it <c>shutdown</c> and waits up to
        /// <paramref name="wait"/> for it to exit (the update path's probe-and-shutdown, shared with Task 17's quit and
        /// Task 23's restart). A daemon that cannot be reached or does not stop is logged, never thrown; only an exception
        /// from <paramref name="restart"/> propagates.
        /// </summary>
        /// <param name="purpose">What the caller is doing, for the log ("quitting", "the restart").</param>
        /// <param name="restart">
        /// Task 23's restart, or null. It is shown the daemon the probe reached (null when none answered) and how the probe
        /// found it, just before <c>shutdown</c> would be sent, and false leaves it alone (<see cref="LocalDaemonStop.LeftAlone"/>).
        /// With it, a daemon still there after the wait, or one that could not be asked, is terminated by pid, as
        /// <c>kill-server --force</c> does (<see cref="MuxTerminateDaemon"/>).
        /// </param>
        private async System.Threading.Tasks.Task<LocalDaemonStop> ShutdownLocalDaemonAsync(TimeSpan wait, string purpose, Func<Ntilde.Mux.MuxClient?, LocalDaemonProbe, bool>? restart = null)
        {
            // Read before the shutdown, as the exit wait's is: the daemon deletes its descriptor on the way out.
            Ntilde.Mux.Contracts.MuxEndpointDescriptor? described = restart is not null ? ReadMuxDescriptorBeforeShutdown(purpose) : null;
            (Ntilde.Mux.MuxClient? probed, bool versionMismatch) = await ProbeLocalDaemonAsync(purpose);
            LocalDaemonProbe found = probed is not null ? LocalDaemonProbe.Reached
                : versionMismatch ? LocalDaemonProbe.VersionMismatch
                : described is null ? LocalDaemonProbe.NotRunning
                : LocalDaemonProbe.Unreachable;
            using (Ntilde.Mux.MuxClient? daemon = probed)
            {
                if (restart is not null && !restart(daemon, found)) return LocalDaemonStop.LeftAlone;
                if (daemon is not null)
                {
                    switch (await ShutdownMuxDaemonForUpdateAsync(daemon, wait, purpose))
                    {
                        case DaemonShutdown.Exited: return LocalDaemonStop.Gone;
                        case DaemonShutdown.Unwatched: return LocalDaemonStop.StopUnconfirmed;
                    }
                }
                else if (!versionMismatch && restart is null)
                {
                    return LocalDaemonStop.NoneReached;
                }
            }

            if (described is null) return LocalDaemonStop.NotStopped;
            try
            {
                return await Task.Run(() => MuxTerminateDaemon(described)) ? LocalDaemonStop.Gone : LocalDaemonStop.NotStopped;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[MainWindow] terminating the multiplexer (pid {described.Pid}) for {purpose} failed: {ex.Message}");
                return LocalDaemonStop.NotStopped;
            }
        }

        /// <summary>What sending a daemon <c>shutdown</c> came to (<see cref="ShutdownMuxDaemonForUpdateAsync(Ntilde.Mux.MuxClient, TimeSpan, string)"/>).</summary>
        private enum DaemonShutdown
        {
            /// <summary>Sent, and the process the descriptor names exited.</summary>
            Exited,

            /// <summary>It could not be sent, or was not answered.</summary>
            NotSent,

            /// <summary>Sent, but no descriptor says which process to watch.</summary>
            Unwatched,

            /// <summary>Sent, and the process was still there when the wait ran out.</summary>
            StillThere,
        }

        private System.Threading.Tasks.Task<DaemonShutdown> ShutdownMuxDaemonForUpdateAsync(Ntilde.Mux.MuxClient daemon) =>
            ShutdownMuxDaemonForUpdateAsync(daemon, System.Threading.Timeout.InfiniteTimeSpan, "the update");

        /// <summary>The descriptor, read through <see cref="MuxReadDescriptorForUpdate"/>; null when it cannot be read.</summary>
        private Ntilde.Mux.Contracts.MuxEndpointDescriptor? ReadMuxDescriptorBeforeShutdown(string purpose)
        {
            try { return MuxReadDescriptorForUpdate(); }
            catch (Exception ex)
            {
                AppLogger.Log($"[MainWindow] reading the mux descriptor before {purpose} failed: {ex.Message}");
                return null;
            }
        }

        /// <param name="daemon">A connection to the daemon.</param>
        /// <param name="wait">How long to wait for the exit on top of the exit seam's own limit; infinite adds none.</param>
        /// <param name="purpose">What the caller is doing, for the log ("the update", "quitting", "the restart").</param>
        /// <returns>Whether it went out, and whether the daemon is known to have exited (<see cref="DaemonShutdown"/>).</returns>
        private async System.Threading.Tasks.Task<DaemonShutdown> ShutdownMuxDaemonForUpdateAsync(Ntilde.Mux.MuxClient daemon, TimeSpan wait, string purpose)
        {
            // Read before the shutdown: the daemon deletes its descriptor on the way out,
            // and the pid in it is what says when the process is really gone.
            Ntilde.Mux.Contracts.MuxEndpointDescriptor? before = ReadMuxDescriptorBeforeShutdown(purpose);

            try
            {
                await daemon.ShutdownServerAsync();
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[MainWindow] mux shutdown before {purpose} failed: {ex.Message}");
                return DaemonShutdown.NotSent;
            }

            // As kill-server does: the apply replaces the executable the daemon runs from,
            // so let it finish exiting first. Awaited, never blocking the UI thread.
            if (before is null)
            {
                return DaemonShutdown.Unwatched;
            }

            bool gone = false;
            try
            {
                Task<bool> exit = MuxWaitForDaemonExitForUpdate(before);
                if (wait != System.Threading.Timeout.InfiniteTimeSpan && await Task.WhenAny(exit, Task.Delay(wait)) != exit)
                {
                    gone = false;
                }
                else
                {
                    gone = await exit;
                }
            }
            catch (Exception ex) { AppLogger.Log($"[MainWindow] waiting for the mux daemon to exit failed: {ex.Message}"); }
            if (!gone) AppLogger.Log($"[MainWindow] the mux daemon (pid {before.Pid}) did not exit within 5 s; going on with {purpose}");
            return gone ? DaemonShutdown.Exited : DaemonShutdown.StillThere;
        }

        /// <summary>
        /// The palette's "Check for updates". Unlike the background check this one always says
        /// what happened: the user asked a direct question and silence would read as a hang.
        /// Constructs the coordinator on demand (see <see cref="EnsureUpdateCoordinator"/>) so
        /// this works even in the first 10 seconds of the process's life, before the deferred
        /// startup timer would otherwise have built it. Reporting goes through
        /// <see cref="Ntilde.Update.IUpdateCheckFeedback"/> so the About window can run
        /// this same pipeline and render the answer inline; without one, the answers surface
        /// as toasts (see <see cref="ToastUpdateCheckFeedback"/>).
        /// </summary>
        private async System.Threading.Tasks.Task CheckForUpdatesInteractiveAsync(Ntilde.Update.IUpdateCheckFeedback? feedback = null)
        {
            feedback ??= new ToastUpdateCheckFeedback(this);

            EnsureUpdateCoordinator();
            if (_updateCoordinator == null)
            {
                // Construction itself failed unexpectedly (see EnsureUpdateCoordinator) - distinct
                // from Unsupported below, which is a healthy check reporting a non-Velopack host.
                feedback.CoordinatorUnavailable();
                return;
            }

            if (_updateCheckInFlight)
            {
                // A check (automatic, or an earlier manual one) is already running; let it finish
                // rather than racing a second download into the same staging directory. Say so,
                // though: returning silently here contradicts this method's whole contract, and
                // the startup check runs 10 s in, so a user who opens the palette promptly is
                // exactly who hits this.
                //
                // The wording is deliberately narrower than "the result will appear". If the
                // in-flight check is the AUTOMATIC one, it reports nothing unless it finds an
                // update - UpToDate and Failed are swallowed by design, because a background
                // check must not interrupt anyone. Promising a result we would not deliver is
                // worse than promising less. Sharing the in-flight task so a manual request
                // could adopt its outcome is the fuller answer, but it is a lot of machinery
                // for a ~10 s window, and it would make a background check's failure suddenly
                // user-visible depending on timing. (Codex P2 on #340.)
                feedback.AlreadyRunning();
                return;
            }

            _updateCheckInFlight = true;
            Ntilde.Update.UpdateCheckOutcome outcome;
            try
            {
                // Task.Run for the same reason as RunAutomaticCheckSafeAsync above: Velopack's
                // check does synchronous file I/O (staged-user-id read/write, nuspec parse of
                // every local .nupkg) before its first await, and this runs from a palette
                // click on the UI thread. The continuation resumes on the UI thread, which the
                // feedback surfaces (toasts, or the About window's status area) require.
                outcome = await Task.Run(() => _updateCoordinator.RunManualCheckAsync());
            }
            finally
            {
                _updateCheckInFlight = false;
            }

            feedback.Outcome(outcome, _updateCoordinator.StagedVersion);
        }

        /// <summary>
        /// The palette's surface: the same answers every check gives, delivered as toasts. The
        /// strings come from <see cref="Ntilde.Update.UpdateCheckMessages"/> so this and
        /// the About window's inline rendering cannot drift apart.
        /// </summary>
        private sealed class ToastUpdateCheckFeedback : Ntilde.Update.IUpdateCheckFeedback
        {
            private readonly MainWindow _owner;

            public ToastUpdateCheckFeedback(MainWindow owner)
            {
                _owner = owner;
            }

            // A toast IS the announcement; there is no interim state to show.
            public void Checking()
            {
            }

            public void AlreadyRunning()
            {
                _owner.ShowRecordingToast(
                    Ntilde.Update.UpdateCheckMessages.AlreadyRunningTitle,
                    Ntilde.Update.UpdateCheckMessages.AlreadyRunningMessage,
                    null,
                    null,
                    autoHide: true);
            }

            public void CoordinatorUnavailable()
            {
                _owner.ShowRecordingToast(
                    Ntilde.Update.UpdateCheckMessages.CoordinatorUnavailableTitle,
                    Ntilde.Update.UpdateCheckMessages.CoordinatorUnavailableMessage,
                    null,
                    null,
                    autoHide: true);
            }

            public void Outcome(Ntilde.Update.UpdateCheckOutcome outcome, string? stagedVersion)
            {
                switch (outcome)
                {
                    case Ntilde.Update.UpdateCheckOutcome.UpdateReady:
                        // Show it here rather than relying on the coordinator's onUpdateReady
                        // callback. That callback fires only when the staged version CHANGES (its
                        // announce-once guard, which exists so a second check does not re-nag about
                        // an update the user already dismissed). So for a user who dismissed the
                        // toast and then asked again, the callback correctly stays silent - and
                        // trusting it here left the manual check answering a direct question with
                        // nothing at all, and no visible way to restart. Re-showing is idempotent:
                        // when the callback did just fire, this sets the same text again.
                        // (Codex P2 on #340.)
                        _owner.ShowUpdateToast(stagedVersion ?? string.Empty);
                        break;
                    case Ntilde.Update.UpdateCheckOutcome.UpToDate:
                    case Ntilde.Update.UpdateCheckOutcome.Unsupported:
                    case Ntilde.Update.UpdateCheckOutcome.Failed:
                        _owner.ShowRecordingToast(
                            Ntilde.Update.UpdateCheckMessages.OutcomeTitle(outcome),
                            Ntilde.Update.UpdateCheckMessages.OutcomeMessage(outcome, stagedVersion),
                            null,
                            null,
                            autoHide: true);
                        break;
                    case Ntilde.Update.UpdateCheckOutcome.Disabled:
                        // Unreachable: a manual check ignores the automatic-checks setting.
                        break;
                }
            }
        }
    }
}
