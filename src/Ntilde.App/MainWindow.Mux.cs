using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using Ntilde.Shell;
using Ntilde.Shell.Theming;
using Ntilde.VT;
using Ntilde.Pty;

using Ntilde.Controls;
using Ntilde.Services.Ssh;
using Ntilde.Platform.Ssh.Launch;
using Ntilde.ViewModels.Ssh;
using Ntilde.Views.Ssh;

namespace Ntilde
{
    /// <summary>
    /// Session persistence plumbing: the session factory, the daemon warm-up, the remote ntilde-mux install,
    /// and releasing mux sessions and remote hosts when panes close.
    /// </summary>
    public partial class MainWindow
    {
        // One daemon connection per endpoint, shared by every mux pane on it (spec §6, Phase 4 spec §5):
        // the local daemon's, plus a remote one per SSH endpoint once a pane uses it. Null while
        // persistence has never been on. Outlives a switch back to Off so the mux panes already open
        // keep working.
        private Ntilde.Shell.Mux.MuxConnectionHosts? _muxHosts;

        /// <summary>The local daemon connection, when session persistence is (or was) on. Tests, startup reattach, updates.</summary>
        internal Ntilde.Shell.Mux.MuxConnectionHost? MuxHost => _muxHosts?.Local;

        /// <summary>Every endpoint's connection (Phase 4 spec §5); null while persistence has never been on.</summary>
        internal Ntilde.Shell.Mux.MuxConnectionHosts? MuxHosts => _muxHosts;

        private Ntilde.Shell.Mux.MuxWindowlessSessions? _windowlessSessions;

        /// <summary>
        /// The agent host's windowless sessions of this window (Phase 5 spec §3, ruling R4): its daemons' sessions that no
        /// pane of it shows. Null while persistence has never been on (no hosts, so nothing to list); built once with the
        /// hosts, which are never replaced. UI thread.
        /// </summary>
        internal AgentHost.IWindowlessSessionSource? WindowlessSessions =>
            _windowlessSessions ??= _muxHosts is { } hosts
                ? new Ntilde.Shell.Mux.MuxWindowlessSessions(hosts, MuxSessionsShownHereAsync, AppLogger.Log)
                : null;

        /// <summary>
        /// Spec §9. An injected factory wins (a mux one brings its host along: the test path);
        /// otherwise KeepOnClose builds the daemon connection and the persistent factory.
        /// </summary>
        private Ntilde.Pty.ITerminalSessionFactory ChooseSessionFactory(Ntilde.Pty.ITerminalSessionFactory? injected)
        {
            if (injected is Ntilde.Shell.Mux.MuxTerminalSessionFactory mux)
            {
                _muxHosts = mux.Hosts;
                WatchMuxDaemonVersions();
                return mux;
            }

            if (injected is not null) return injected;
            return Ntilde.Shell.Mux.SessionPersistenceMode.IsKeepOnClose(_settings.SessionPersistence)
                ? CreatePersistentSessionFactory()
                : Ntilde.Shell.DefaultTerminalSessionFactory.Instance;
        }

        /// <summary>Only the local daemon is warmed: a remote endpoint connects when a pane first needs it.</summary>
        private void StartMuxWarmupOnce()
        {
            if (_muxWarmupStarted || _muxHosts is null) return;
            _muxWarmupStarted = true;
            _muxHosts.Local.WarmUp();
        }

        /// <summary>
        /// Builds the local daemon connection for KeepOnClose: <see cref="AppServiceBundle.MuxHostFactory"/>, whose
        /// designer version refuses, so a designer or test window never launches a daemon. A test may replace it.
        /// </summary>
        internal Func<Ntilde.Shell.Mux.MuxConnectionHost> MuxHostFactory { get; set; }

        /// <summary>
        /// Builds the connection for a remote endpoint the first time a pane uses it (Phase 4 spec §5), or
        /// returns null to decline (the profile is gone; one that does not persist remote sessions still gets a
        /// connection, to deliver a closed pane's kill: codex C2). Runs outside
        /// the registry's lock, on whichever thread asked, and may race another ask for the same endpoint
        /// (the loser is disposed unused): it may look things up, but must not connect. The constructor
        /// sets <see cref="CreateRemoteMuxHost"/>; a seam for tests.
        /// </summary>
        internal Func<Ntilde.Shell.Mux.MuxEndpointId, Ntilde.Shell.Mux.MuxConnectionHost?> RemoteMuxHostFactory { get; set; } = _ => null;

        /// <summary>
        /// Opens the ntilde-mux install flow (Phase 4 spec §9) for an SSH profile's id: what the unavailable
        /// toast's "Install ntilde-mux…" / "Update ntilde-mux…" runs. The constructor sets
        /// <see cref="OpenRemoteMuxInstallForProfile"/>; while it is null, no notice offers an install or update.
        /// </summary>
        internal Action<Guid>? OpenRemoteMuxInstall { get; set; }

        /// <summary>
        /// Shows the install dialog for a saved profile over an owner window and returns how it ended
        /// (<see cref="RemoteMuxInstallDialog.ShowAsync"/>): after a success the profile's MuxOptions hold what
        /// was installed, and the flag when the user kept "Keep remote sessions running" ticked. The caller
        /// saves. The constructor sets <see cref="ShowRemoteMuxInstallDialogAsync"/>; a seam for tests.
        /// </summary>
        internal Func<Window, Ntilde.Platform.Ssh.Models.SshProfile, Task<Ntilde.Shell.Mux.Remote.RemoteMuxInstallResult?>> ShowRemoteMuxInstall { get; set; } =
            static (_, _) => Task.FromResult<Ntilde.Shell.Mux.Remote.RemoteMuxInstallResult?>(null);

        /// <summary>One client for every install's release download: it follows GitHub's redirects to its CDN.</summary>
        private static readonly Lazy<System.Net.Http.HttpClient> MuxReleaseHttp =
            new(Ntilde.Shell.Mux.Remote.GitHubReleaseMuxAssetSource.CreateHttpClient);

        /// <summary>
        /// The connection editor's "Install ntilde-mux on this host…" (Phase 4 spec §9): makes the editor's
        /// command run <see cref="InstallRemoteMuxFromEditorAsync"/> with the editor window as the dialog's owner.
        /// </summary>
        internal void WireRemoteMuxInstall(NewSshConnectionViewModel vm, Window editor)
        {
            ArgumentNullException.ThrowIfNull(vm);
            ArgumentNullException.ThrowIfNull(editor);
            vm.InstallRemoteMux = () => InstallRemoteMuxFromEditorAsync(vm, editor);
        }

        /// <summary>
        /// The editor's install: saves the pending edits first, through the editor's own
        /// <see cref="SshConnectionService.SaveProfile(NewSshConnectionViewModel)"/> - the install runs over the
        /// profile's stored host, auth and backend - then shows the dialog. An editor that does not validate shows
        /// why, and opens nothing.
        /// </summary>
        /// <remarks>
        /// After a success only the four mux fields change, in the store
        /// (<see cref="SshConnectionService.RecordRemoteMuxInstall"/>) and in the editor
        /// (<see cref="NewSshConnectionViewModel.ApplyRemoteMuxInstall"/>, whose status line follows). The editor is not
        /// saved a second time: anything in it the first save did not write stays the user's to save or discard.
        /// (The dialog is modal over the editor, so nothing can be typed meanwhile, but the flow does not rely on it.)
        /// </remarks>
        internal async Task InstallRemoteMuxFromEditorAsync(NewSshConnectionViewModel vm, Window editor)
        {
            if (!vm.Validate()) return;

            Ntilde.Platform.Ssh.Models.SshProfile saved;
            try
            {
                saved = _sshConnectionService.SaveProfile(vm);
            }
            catch (Exception ex)
            {
                vm.ValidationError = ex.Message;
                return;
            }

            // A new profile keeps this id from now on, so the editor's own save updates it rather than adding a copy.
            vm.ProfileId = saved.Id;
            RefreshProfileUIs();

            bool wasPersisting = saved.MuxOptions.PersistRemoteSessions;
            Ntilde.Shell.Mux.Remote.RemoteMuxInstallResult? result = await ShowRemoteMuxInstall(editor, saved);
            if (result is not { Success: true, Installed: { } installed }) return;

            // The dialog recorded the install in `saved`; the editor takes those four fields, whatever happens below.
            vm.ApplyRemoteMuxInstall(saved.MuxOptions);
            try
            {
                bool turnOn = !wasPersisting && saved.MuxOptions.PersistRemoteSessions;
                if (_sshConnectionService.RecordRemoteMuxInstall(saved.Id, installed, turnOn) is null)
                {
                    vm.ValidationError = "ntilde-mux was installed, but this connection is no longer saved; Save keeps it.";
                    return;
                }

                RefreshProfileUIs();
                DecideMuxNoticeAgainAfterInstall(saved.Id);
            }
            catch (Exception ex)
            {
                vm.ValidationError = $"ntilde-mux was installed, but recording it in the connection failed: {ex.Message}";
            }
        }

        /// <summary>The production <see cref="OpenRemoteMuxInstall"/>: fire and forget, so every failure is logged here.</summary>
        private void OpenRemoteMuxInstallForProfile(Guid profileId) => _ = OpenRemoteMuxInstallForProfileAsync(profileId);

        /// <summary>
        /// The toast's install (spec §7.5, §9): the stored profile, the dialog over this window, and after a success
        /// only the four mux fields recorded in the profile as stored then
        /// (<see cref="SshConnectionService.RecordRemoteMuxInstall"/>): the user never opened it, so nothing else of
        /// it may change.
        /// </summary>
        private async Task OpenRemoteMuxInstallForProfileAsync(Guid profileId)
        {
            try
            {
                Ntilde.Platform.Ssh.Models.SshProfile? profile = _sshConnectionService.GetStoredProfile(profileId);
                if (profile is null)
                {
                    EnqueueNotice("Install ntilde-mux", "That SSH connection no longer exists.", prompted: true);
                    return;
                }

                bool wasPersisting = profile.MuxOptions.PersistRemoteSessions;
                Ntilde.Shell.Mux.Remote.RemoteMuxInstallResult? result = await ShowRemoteMuxInstall(this, profile);
                if (result is not { Success: true, Installed: { } installed }) return;

                bool turnOn = !wasPersisting && profile.MuxOptions.PersistRemoteSessions;
                if (_sshConnectionService.RecordRemoteMuxInstall(profileId, installed, turnOn) is null)
                {
                    EnqueueNotice("Install ntilde-mux", "ntilde-mux was installed, but its SSH connection was deleted meanwhile.", prompted: true);
                    return;
                }

                RefreshProfileUIs();
                DecideMuxNoticeAgainAfterInstall(profileId);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[MainWindow] ntilde-mux install for SSH profile {profileId:N} failed: {ex.Message}");
                EnqueueNotice("Install ntilde-mux", $"ntilde-mux could not be installed: {ex.Message}", prompted: true);
            }
        }

        /// <summary>The production <see cref="ShowRemoteMuxInstall"/>: the dialog, themed like this window's dialogs.</summary>
        private Task<Ntilde.Shell.Mux.Remote.RemoteMuxInstallResult?> ShowRemoteMuxInstallDialogAsync(Window owner, Ntilde.Platform.Ssh.Models.SshProfile profile) =>
            RemoteMuxInstallDialog.ShowAsync(
                owner,
                profile,
                (source, report, progress) => CreateRemoteMuxInstaller(profile, source, report, progress),
                AppVersionInfo.Version,
                TopLevel.GetTopLevel(owner)?.Clipboard,
                ThemedWindowChrome.Apply);

        /// <summary>
        /// One install run's installer (spec §9: every step over the profile's own exec transport). The user is
        /// present, so the transport is interactive: OpenSSH may prompt through askpass, the native backend
        /// through this window's prompts. Called off the UI thread by the dialog.
        /// </summary>
        private Ntilde.Shell.Mux.Remote.RemoteMuxInstaller CreateRemoteMuxInstaller(
            Ntilde.Platform.Ssh.Models.SshProfile profile,
            Ntilde.Shell.Mux.Remote.IMuxDaemonAssetSource? source,
            Action<Ntilde.Shell.Mux.Remote.RemoteMuxInstallStep, string> report,
            IProgress<Ntilde.Shell.Mux.Remote.RemoteMuxInstallProgress>? progress) =>
            new(
                CreateRemoteMuxTransport(profile, new Ntilde.Shell.Mux.Remote.RemoteMuxTransportRequest(Interactive: true, _sshInteractionService)),
                source ?? new Ntilde.Shell.Mux.Remote.GitHubReleaseMuxAssetSource(
                    MuxReleaseHttp.Value,
                    AppVersionInfo.Version,
                    Ntilde.Shell.Mux.Remote.GitHubReleaseMuxAssetSource.DefaultCacheDirectory,
                    Ntilde.Shell.Mux.Remote.MuxAssetPins.Load()),
                report)
            {
                Progress = progress,
            };

        /// <summary>
        /// The exec transport for <paramref name="profile"/> (spec §8.2, §8.3), by its backend: what the remote hosts'
        /// attempts and the install flow both run over. May block (OpenSSH plans its config file, and an automatic attempt
        /// may run <c>ssh -V</c> once per ssh executable): off the UI thread.
        /// OpenSSH plans this very profile, which may be a host's blend of an edited or deleted one, self-contained once the
        /// host's destination is pinned (codex D2). A Native profile is refused while native SSH is off, as this window's
        /// settings say at that call (<see cref="NativeSshEnabledNow"/>; codex4 F).
        /// </summary>
        private Ntilde.Platform.Ssh.Exec.ISshExecTransport CreateRemoteMuxTransport(
            Ntilde.Platform.Ssh.Models.SshProfile profile,
            Ntilde.Shell.Mux.Remote.RemoteMuxTransportRequest request) =>
            Ntilde.Shell.Mux.Remote.RemoteMuxHostFactory.CreateTransport(
                profile,
                request,
                (p, selfContained) => _sshConnectionService.BuildLaunchDetailsFor(p, SshDiagnosticsLevel.None, selfContained),
                static () => new Ntilde.Platform.Ssh.Native.NativeSshInterop(),
                NativeSshEnabledNow,
                SshAskPassCommand.LocateHelper(),
                AppLogger.Log,
                Ntilde.Platform.Ssh.Exec.OpenSshClientVersionCache.Shared);

        /// <summary>
        /// The global native SSH switch (Settings &gt; SSH, <see cref="TerminalSettings.ExperimentalNativeSshEnabled"/>) as
        /// this window's settings say now - the source a plain SSH pane's spawn reads it from. A save replaces
        /// <see cref="_settings"/> whole, so this follows it. Read by each remote attempt's transport, off the UI thread.
        /// </summary>
        private bool NativeSshEnabledNow() => _settings.ExperimentalNativeSshEnabled;

        /// <summary>
        /// The action a persisted SSH tab's notice offers for <paramref name="failure"/> (spec §7.5): the install
        /// flow for <paramref name="profileId"/> when ntilde-mux is missing or outdated, and only while
        /// <see cref="OpenRemoteMuxInstall"/> is set; otherwise null. Its button names <paramref name="host"/>.
        /// </summary>
        internal PersistenceNoticeAction? RemoteMuxNoticeAction(Ntilde.Shell.Mux.Remote.RemoteMuxFailure? failure, Guid profileId, string host) =>
            PersistenceNoticeAction.ForRemoteFailure(failure, profileId, host, OpenRemoteMuxInstall);

        /// <summary>
        /// The production <see cref="RemoteMuxHostFactory"/> (Phase 4 spec §7.1): the profile from the SSH
        /// store, and per attempt a transport by the profile's backend - OpenSSH from its launch plan with
        /// the askpass helper (batch mode for automatic attempts, or its vault-only mode when the profile's
        /// password is saved), or native with this window's prompts. Automatic attempts read the saved password
        /// from the vault (<see cref="Vault"/>), off the UI thread, and sign in with it once.
        /// </summary>
        private Ntilde.Shell.Mux.MuxConnectionHost? CreateRemoteMuxHost(Ntilde.Shell.Mux.MuxEndpointId id) =>
            Ntilde.Shell.Mux.Remote.RemoteMuxHostFactory.Create(
                id,
                _sshConnectionService.GetStoredProfile,
                CreateRemoteMuxTransport,
                AppLogger.Log,
                _sshInteractionService,
                savedPassword: static profile => Ntilde.Shell.Mux.Remote.RemoteMuxHostFactory.ReadSavedPassword(Vault ?? new VaultService(), profile),
                askPassRecords: new SshAskPassSessionMarkers(static () => SshAskPassSessionMarkers.DefaultDirectory, AppLogger.Log));

        /// <summary>
        /// Reuses the hosts kept from an earlier On period; only the first call builds them. Building
        /// the local host can throw (e.g. no Environment.ProcessPath to spawn the daemon from): that
        /// must not crash startup or a settings save, so it logs and falls back to normal sessions.
        /// </summary>
        private Ntilde.Pty.ITerminalSessionFactory CreatePersistentSessionFactory()
        {
            try
            {
                // Through a lambda, not the delegate itself: a test may set the seam after construction.
                _muxHosts ??= new Ntilde.Shell.Mux.MuxConnectionHosts(MuxHostFactory(), id => RemoteMuxHostFactory(id), AppLogger.Log);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[MainWindow] session persistence unavailable, using normal sessions: {ex.Message}");
                return Ntilde.Shell.DefaultTerminalSessionFactory.Instance;
            }

            WatchMuxDaemonVersions();

            // The SSH profile store, through a lambda: this runs before _sshConnectionService is assigned
            // (Task 19's note), and a profile's PersistRemoteSessions is read at each spawn. Panes make
            // their remote requests off the UI thread (TerminalPane.RunOffUiThread, Phase 4 spec §7.4).
            return new Ntilde.Shell.Mux.MuxTerminalSessionFactory(
                _muxHosts,
                Ntilde.Shell.DefaultTerminalSessionFactory.Instance,
                id => _sshConnectionService?.GetStoredProfile(id),
                AppLogger.Log);
        }

        /// <summary>
        /// A mux session's HasActiveChildProcesses is a cached probe; the close confirmation asks
        /// the daemon first, waiting at most <paramref name="timeout"/>. A no-op for other sessions.
        /// </summary>
        internal static async Task RefreshPersistentSessionInfoAsync(ITerminalSession? session, TimeSpan timeout)
        {
            if (session is not Ntilde.Mux.MuxClientSession { IsConnected: true } mux) return;
            // The token also retires the request itself, so a daemon that never answers leaves no
            // pending request (or later unobserved fault) behind the abandoned wait.
            using var cts = new System.Threading.CancellationTokenSource(timeout);
            try
            {
                await mux.RefreshSessionInfoAsync(cts.Token).WaitAsync(timeout);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or Ntilde.Mux.Contracts.MuxProtocolException or ObjectDisposedException or IOException)
            {
                // Stale is acceptable: the confirmation then uses the last probed value.
            }
        }

        /// <summary>
        /// Session persistence is on for this window: the mux commands exist only then. The host
        /// alone is not enough - it outlives an on→off flip for the mux panes still open, while the
        /// factory follows the setting.
        /// </summary>
        private bool IsMuxPersistenceActive => _muxHosts is not null && _sessionFactory is Ntilde.Shell.Mux.MuxTerminalSessionFactory;

        /// <summary>
        /// Release hardening item 1: SessionPersistence was turned Off during this window's life. The factory followed
        /// (<see cref="ApplySessionPersistenceSetting"/>), but the panes opened while it was on still show daemon shells, and
        /// the hosts stay for them. Off means "a shell ends when its window closes", so the close ends them, as a
        /// "Close them" does (<see cref="EndLocalSessionsOnTeardown"/>). A window whose setting was Off from the start has no
        /// hosts (or, in tests, still the injected mux factory), so nothing changes for it.
        /// </summary>
        private bool IsMuxPersistenceTurnedOff =>
            _muxHosts is not null
            && _sessionFactory is not Ntilde.Shell.Mux.MuxTerminalSessionFactory
            && !Ntilde.Shell.Mux.SessionPersistenceMode.IsKeepOnClose(_settings.SessionPersistence);

        /// <summary>
        /// A user closed this pane: a mux shell must end. KillAsync (not the fire-and-forget Kill): its
        /// reply means the kill landed, and the host waits for it on dispose, so closing the last tab
        /// cannot drop it. Still enqueued synchronously on the UI thread (RequestAsync enqueues before
        /// its first await). A session that already exited is left alone, and so is a local one whose
        /// connection is gone; a remote one goes through its host whatever its connection says. A share
        /// whose sharing cannot be known never gets here with EndSession: DisposeControlTree detaches it.
        /// </summary>
        /// <param name="muxEndpoint">The pane's <see cref="TerminalPane.MuxEndpoint"/>: its own endpoint's host tracks the kill (Phase 4 spec §5).</param>
        private void KillMuxSessionOnClose(ITerminalSession? session, Ntilde.Shell.Mux.PaneDisposition disposition, string? muxEndpoint)
        {
            if (disposition != Ntilde.Shell.Mux.PaneDisposition.EndSession || session is not Ntilde.Mux.MuxClientSession mux)
            {
                return;
            }

            if (!mux.IsProcessRunning) return;
            Ntilde.Shell.Mux.MuxEndpointId endpoint = Ntilde.Shell.Mux.MuxEndpointId.Parse(muxEndpoint);
            if (!endpoint.IsLocal)
            {
                // Review Focus 1: the user meant to end that shell, and nobody can adopt a remote one. Always through
                // its host: sent at once (and tracked) while connected, else queued for the next connect - and queued
                // again if the connection closes before the daemon answers. A dead link can still look connected
                // until the liveness ping notices, and a kill sent into it straight from here would only be logged.
                KillThroughRemoteHost(endpoint, mux.Id);
                return;
            }

            // A local session whose connection is gone is left to orphan adoption, as before.
            if (!mux.IsConnected) return;

            try
            {
                Task kill = mux.KillAsync();
                // The session's own connection is the one whose teardown must wait for this reply. Its
                // host exists: the factory built it to open the session. TryGet, never build one here.
                _muxHosts?.TryGet(endpoint)?.TrackPendingKill(kill);
                _ = kill.ContinueWith(
                    t => TerminalLogger.Log($"[MainWindow] mux kill of {mux.Id} failed: {t.Exception?.GetBaseException().Message}"),
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                TerminalLogger.Log($"[MainWindow] mux kill failed: {ex.Message}");
            }
        }

        private bool _remoteMuxReleasePosted; // UI thread: a ReleaseUnusedRemoteMuxHosts pass is queued

        /// <summary>
        /// UI thread. Final review F1: a remote pane closed. The pass that releases the remote hosts no pane needs runs
        /// once the close is done - a tab's whole tree disposed, the tab itself removed - not in the middle of it, where
        /// the closing tab's other panes would still count. One pass for every pane closed together. A pane also asks for
        /// one once it has queued the kill of a shell its stale remote result started (codex C1), or discarded any other
        /// stale remote result, for which the factory may have queued a kill (codex E1;
        /// <see cref="TerminalPane.RemoteMuxReleaseCheck"/>): that result may come back after its close's pass ran.
        /// </summary>
        private void ScheduleRemoteMuxHostRelease()
        {
            if (_remoteMuxReleasePosted || _muxHosts is null) return;
            _remoteMuxReleasePosted = true;
            Dispatcher.UIThread.Post(ReleaseUnusedRemoteMuxHosts, DispatcherPriority.Background);
        }

        /// <summary>
        /// UI thread. Releases every remote host whose endpoint no pane of this window needs
        /// (<see cref="TerminalPane.RemoteMuxEndpointInUse"/>) and no open "Attach to session…" picker holds
        /// (<see cref="ConnectForMuxPickerAsync"/>): closed once its kills are delivered
        /// (<see cref="Ntilde.Shell.Mux.MuxConnectionHosts.Release"/>), so it stops pinging and reconnecting, the remote
        /// proxy and daemon are let go, and its remembered secret is forgotten. Not during teardown, which closes every host.
        /// </summary>
        private void ReleaseUnusedRemoteMuxHosts()
        {
            _remoteMuxReleasePosted = false;
            if (_teardownDone || _muxHosts is not { } hosts) return;
            HashSet<Ntilde.Shell.Mux.MuxEndpointId> needed = [.. AllPanes().Select(p => p.RemoteMuxEndpointInUse).OfType<Ntilde.Shell.Mux.MuxEndpointId>(), .. _muxPickerHeldHosts];
            foreach (Ntilde.Shell.Mux.MuxEndpointId endpoint in hosts.RemoteEndpoints)
            {
                if (!needed.Contains(endpoint)) hosts.Release(endpoint);
            }
        }

        /// <summary>
        /// Review Focus 1, for a remote pane closed with no session of its own: its id is still pending - a restored
        /// tab never shown, a connect or reattach in flight, a reattach that failed, a restore that could not reach
        /// the host. The user meant to end that shell too, and nobody can adopt a remote one, so its host kills it -
        /// connecting for it if nothing else is (<see cref="Ntilde.Shell.Mux.MuxConnectionHost.KillWhenConnected"/>).
        /// A local pending id is left to orphan adoption, as before; a share's pending id is never killed (Phase 5 Task 26:
        /// DisposeControlTree detaches a share whose attach is pending, and a plain SSH pane lets a share's id go).
        /// </summary>
        private void KillPendingRemoteMuxSessionOnClose(Guid? pendingId, Ntilde.Shell.Mux.PaneDisposition disposition, string? muxEndpoint)
        {
            if (disposition != Ntilde.Shell.Mux.PaneDisposition.EndSession || pendingId is not Guid id) return;
            Ntilde.Shell.Mux.MuxEndpointId endpoint = Ntilde.Shell.Mux.MuxEndpointId.Parse(muxEndpoint);
            if (endpoint.IsLocal) return;
            KillThroughRemoteHost(endpoint, id);
        }

        /// <summary>
        /// A closed pane's remote shell is killed through its endpoint's host (<see cref="RemoteMuxHostFor"/>). Without one
        /// the kill cannot be sent at all, and that is logged, naming the shell left running: nothing adopts a remote orphan.
        /// </summary>
        private void KillThroughRemoteHost(Ntilde.Shell.Mux.MuxEndpointId endpoint, Guid sessionId)
        {
            if (RemoteMuxHostFor(endpoint, out string? whyNone) is { } host)
            {
                host.KillWhenConnected(sessionId);
                return;
            }

            TerminalLogger.Log($"[MainWindow] cannot end session {sessionId} on {endpoint}: no connection to send its kill on ({whyNone}); it keeps running there");
        }

        /// <summary>
        /// The remote endpoint's host, built if no pane has used it yet - a restored tab closed before it was shown,
        /// or before its own factory call got that far (the creator only constructs; it never connects). Built whatever
        /// the profile's <c>PersistRemoteSessions</c> says (codex C2): the flag decides where new tabs go, not whether a
        /// shell the user closed ends, and a pane whose profile lost the flag still keeps its pending id. A host built
        /// only for a kill connects for it in one automatic attempt, which never prompts, and the window releases it once
        /// the kill is delivered.
        /// </summary>
        /// <param name="whyNone">
        /// When there is none, the reason that holds (codex residual round): session persistence was never on in this
        /// window, so it has no connection registry; the window is closing (the registry is disposed); the SSH profile
        /// is gone (the only thing the creator declines); or setting the connection up failed.
        /// </param>
        private Ntilde.Shell.Mux.MuxConnectionHost? RemoteMuxHostFor(Ntilde.Shell.Mux.MuxEndpointId endpoint, out string? whyNone)
        {
            whyNone = null;
            if (_muxHosts is not { } hosts)
            {
                whyNone = "session persistence has not been on in this window, so it keeps no remote connections";
                return null;
            }

            try
            {
                if (hosts.GetOrCreate(endpoint) is { } host) return host;
                whyNone = hosts.IsDisposed ? "the window is closing" : "its SSH profile is gone";
            }
            catch (Exception ex)
            {
                whyNone = $"its connection could not be set up: {ex.Message}";
            }

            return null;
        }
    }
}
