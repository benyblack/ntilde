using Ntilde.Shell;
using Ntilde.VT;
using Ntilde.Pty;
using Ntilde.Mux;
using Ntilde.Shell.Mux;
using Ntilde.Shell.Mux.Remote;

namespace Ntilde.Controls
{
    /// <summary>
    /// Remote persistent sessions: connecting through the remote host, following its connection, reconnecting,
    /// and the remote banners.
    /// </summary>
    public partial class TerminalPane
    {
        /// <summary>
        /// UI thread. No session from the remote daemon (Phase 4 spec §7.5): the id the request named is kept
        /// (none for a new tab whose SSH connect failed - the Task 19 ruling), and Enter retries. A reattach after
        /// a drop that failed goes back to where the host is (Review Focus 2): into the loop while it still runs,
        /// else to the Enter banner - never to no session and no banner. A failure that needs the user says why
        /// under either banner (<see cref="RemoteNeedsUserLine"/>).
        /// </summary>
        private void EnterRemoteUnreachable(TerminalSessionRequest request, PersistentSessionResult result, string host)
        {
            MuxSessionIdToRestore = request.ExistingMuxSessionId;
            MuxAttachSharedToRestore = request.AttachShared; // a share retried is still a share (Task 26)
            TermView.SetSession(null);
            TerminalLogger.Log($"[TerminalPane] no session from {host} for {request.ExistingMuxSessionId?.ToString() ?? "a new tab"} ({result.Detail}); kept for a retry");
            string? needsUser = RemoteNeedsUserLine(result.RemoteFailure, host);
            bool agentNeedsUser = TellTheAgentItNeedsTheUser(result.RemoteFailure, out Ntilde.AgentHost.AgentNeedsUserReason reason);
            // No reconnect was automatic, so the plain sign-in line says what Enter does instead.
            if (agentNeedsUser && reason == Ntilde.AgentHost.AgentNeedsUserReason.SignIn) needsUser = RemoteSignInNeedsYouAgentLine;

            if (request.ReattachAfterDrop && request.ExistingMuxSessionId is not null)
            {
                _muxReattachAfterDrop = true;
                if (_remoteHost?.Host.IsReconnecting == true) EnterRemoteReconnecting();
                else EnterRemoteWaitingForEnter(RemoteAbandonedBanner(host), detail: needsUser, agentNeedsUser: agentNeedsUser);
                return;
            }

            _muxReattachAfterDrop = false;
            _muxConnectionLost = true;
            WriteBanner($"\r\n\x1b[90m{RemoteUnreachableBanner(SanitizeBannerValue(host))}\x1b[0m\r\n");
            if (agentNeedsUser) WriteBanner($"\x1b[90m{RemoteAgentOpenedLine}\x1b[0m\r\n");
            if (needsUser is not null) WriteBanner($"\x1b[90m{SanitizeBannerValue(needsUser)}\x1b[0m\r\n");
            // SSH worked and only ntilde-mux did not: the toast says why, with the install flow when that
            // fixes it. A failed SSH connect needs no toast: the banner already says the host is not reachable.
            if (result.RemoteFailure is { Kind: not (RemoteFailureKind.SshFailed or RemoteFailureKind.NeedsUser) } failure)
            {
                RaisePersistenceNotice(RemoteMuxUnavailableNoticeTitle, RemoteMuxUnavailableMessage(host, failure.Reason), RemoteNoticeActionFor(request, result, host));
            }
        }

        /// <summary>
        /// The window's action for a remote result's notice (spec §7.5), for the profile the request named, on
        /// <paramref name="host"/> - the host the notice's line names, which its button names too (final review I1).
        /// </summary>
        private PersistenceNoticeAction? RemoteNoticeActionFor(TerminalSessionRequest request, PersistentSessionResult result, string host) =>
            request.Ssh is { } ssh ? RemoteNoticeAction?.Invoke(result.RemoteFailure, ssh.ProfileId, host) : null;

        /// <summary>
        /// UI thread. Phase 4 spec §7.4: the factory call for a remote request runs off the UI thread - it can
        /// wait minutes for its connection, behind SSH prompts that need the UI thread - and its result is applied
        /// back here by <see cref="CompleteRemoteSession"/>, unless a later spawn (or the pane's disposal) made it stale.
        /// </summary>
        private void BeginRemoteSession(MuxTerminalSessionFactory factory, TerminalSessionRequest request, TerminalProfile profile)
        {
            int generation = _remoteGen;
            _remoteHostName = factory.RemoteHostDisplayName(request) ?? profile.Name;
            _remoteConnecting = true;
            // Until the result is back the id is still this pane's: a save meanwhile writes it, with its endpoint - and a
            // share's with its flag, which a close meanwhile reads too (Task 26: its attach is pending, so it detaches).
            MuxSessionIdToRestore = request.ExistingMuxSessionId;
            MuxAttachSharedToRestore = request.AttachShared;
            MuxEndpoint = MuxEndpointId.ForSsh(request.Ssh!.ProfileId).ToString();
            TermView.SetSession(null); // a previous, disposed session must not get keys meanwhile
            WriteBanner($"\r\n\x1b[90m{RemoteConnectingBanner(SanitizeBannerValue(_remoteHostName))}\x1b[0m\r\n");

            Task<PersistentSessionResult> work;
            try
            {
                work = RunOffUiThread(() => factory.CreatePersistent(request));
            }
            catch (Exception ex)
            {
                work = Task.FromException<PersistentSessionResult>(ex);
            }

            work.ContinueWith(
                done => this.Dispatcher.Post(() => CompleteRemoteSession(generation, factory, request, profile, done)),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        /// <summary>
        /// UI thread. A remote factory call is back. A stale one (a later spawn, or a disposed pane) is discarded;
        /// otherwise its result goes through the same handling as a local one (<see cref="ApplyPersistentResult"/>),
        /// and its session is wired as <see cref="InitializeSessionCore"/> wires one.
        /// </summary>
        private void CompleteRemoteSession(int generation, MuxTerminalSessionFactory factory, TerminalSessionRequest request, TerminalProfile profile, Task<PersistentSessionResult> work)
        {
            PersistentSessionResult? result = work.IsCompletedSuccessfully ? work.Result : null;
            if (generation != _remoteGen || Volatile.Read(ref _disposed))
            {
                DiscardRemoteResult(factory, result);
                return;
            }

            _remoteConnecting = false;
            if (result is null)
            {
                // As a plain SSH spawn that threw (the plain SSH stand-in can): the banner says why, the id stays
                // pending, and Enter retries.
                string why = work.Exception?.GetBaseException().Message ?? "the connection attempt was cancelled";
                TerminalLogger.Log($"[TerminalPane] remote session for '{profile.Name}' failed: {why}");
                WriteBanner($"\r\n[ERROR] SSH Connection Failed: {SanitizeBannerValue(why)}\r\n");
                return;
            }

            MuxSessionIdToRestore = null; // consumed by this result (an unreachable one keeps it again)
            MuxAttachSharedToRestore = false;
            MuxEndpointId endpoint = MuxEndpointId.Parse(result.Endpoint);
            FollowRemoteHost(endpoint.IsLocal ? null : factory.Hosts.TryGet(endpoint));

            ITerminalSession? session;
            bool previousLost;
            try
            {
                session = ApplyPersistentResult(request, result, out previousLost);
                if (result.Endpoint is null)
                {
                    // NotPersistent: the profile stopped persisting after the route was chosen (or the window is
                    // closing), and plain SSH stood in. As for any plain SSH pane, the id stays pending on its endpoint -
                    // unless it is a share's.
                    MuxSessionIdToRestore = request.ExistingMuxSessionId;
                    MuxAttachSharedToRestore = request.AttachShared;
                    LetGoOfPendingShareOnPlainSsh();
                    if (MuxSessionIdToRestore is not null) MuxEndpoint = MuxEndpointId.ForSsh(request.Ssh!.ProfileId).ToString();
                }

                Session = session;
                if (session is null) return; // unreachable: the banner, and the id kept, were done there
                ShellCommand = session.ShellCommand;
                ShellArgs = string.Empty;
                WireSessionLifecycle(session, profile);
            }
            catch (Exception ex)
            {
                TerminalLogger.Log($"[TerminalPane] wiring the remote session for '{profile.Name}' failed: {ex.Message}");
                WriteBanner($"\r\n[ERROR] SSH Connection Failed: {SanitizeBannerValue(ex.Message)}\r\n");
                return;
            }

            WireSessionStream(session, previousLost);
        }

        /// <summary>
        /// UI thread. A remote result nobody will show: a later spawn replaced it, or the pane is gone. A shell started
        /// for that result alone - not the one an id reopened, which it did not start - has nobody left to show it, so
        /// it is ended rather than left running unseen on the remote (<see cref="KillStaleRemoteSession"/>). Any other
        /// result still asks for the window's release pass (<see cref="RemoteMuxReleaseCheck"/>): the factory may have
        /// queued a kill of its own for it (codex E1). Then the session is let go off the UI thread (a plain SSH
        /// stand-in's dispose may block).
        /// </summary>
        private void DiscardRemoteResult(MuxTerminalSessionFactory factory, PersistentSessionResult? result)
        {
            if (result?.Session is { } kept && result.Outcome is not PersistentSessionOutcome.Reattached && kept is MuxClientSession mux)
            {
                // Before the dispose below, and before any release pass runs (they run on this thread).
                KillStaleRemoteSession(factory.Hosts, MuxEndpointId.Parse(result.Endpoint), mux.Id);
            }
            else
            {
                // No shell of this result's to end here - a plain SSH stand-in, a reopen, no session - but the factory
                // may have queued a kill of its own on the way (codex E1: a spawn whose reply was lost), on a host this
                // pane's close had released and the registry then took back or built again. The pass lets such a host
                // go once that kill is delivered; one that is not needed is released as any other.
                RemoteMuxReleaseCheck?.Invoke();
            }

            if (result?.Session is not { } session) return;

            _ = Task.Run(() =>
            {
                try
                {
                    session.Dispose();
                }
                catch (Exception ex)
                {
                    TerminalLogger.Log($"[TerminalPane] discarding a stale remote session failed: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// UI thread. Codex C1: the shell a stale result started is killed the way a closed pane's is, through its
        /// endpoint's host (<see cref="MuxConnectionHost.KillWhenConnected"/>): sent and tracked while connected, else
        /// queued for the next connect - one non-interactive attempt on an idle host - and queued again if its
        /// connection closes first. The session's own fire-and-forget kill was lost on a link already down, or once
        /// the window released the host. A host released meanwhile is taken back or built again
        /// (<see cref="MuxConnectionHosts.GetOrCreate"/>), and a release waits for the kill. The window's release pass
        /// then runs (<see cref="RemoteMuxReleaseCheck"/>): this pane's close may have run it before the result came
        /// back, and a host kept or built only for this kill must still go once it is delivered. No host at all
        /// (the window is closing, the profile is gone) is logged: that shell keeps running.
        /// </summary>
        private void KillStaleRemoteSession(MuxConnectionHosts hosts, MuxEndpointId endpoint, Guid sessionId)
        {
            MuxConnectionHost? host;
            string? why = null;
            try
            {
                host = hosts.GetOrCreate(endpoint);
            }
            catch (Exception ex)
            {
                host = null;
                why = $"its connection could not be set up: {ex.Message}";
            }

            if (host is null)
            {
                why ??= hosts.IsDisposed ? "the window is closing" : "its SSH profile is gone";
                TerminalLogger.Log($"[TerminalPane] cannot end session {sessionId} on {endpoint}, started for a result nobody shows: no connection to send its kill on ({why}); it keeps running there");
                return;
            }

            host.KillWhenConnected(sessionId);
            RemoteMuxReleaseCheck?.Invoke();
        }

        /// <summary>UI thread. Follows <paramref name="host"/>'s events (null: none), dropping the previous host's.</summary>
        private void FollowRemoteHost(MuxConnectionHost? host)
        {
            if (ReferenceEquals(host, _remoteHost?.Host)) return;
            _remoteHost?.Dispose();
            _remoteHost = host is null ? null : new RemoteHostSubscription(this, host);
        }

        private enum RemoteHostEvent
        {
            ConnectionLost,
            Reconnected,
            ReconnectAbandoned,
            DaemonStopped,
        }

        /// <summary>
        /// One remote host's events, as this pane follows them (Phase 4 spec §7.4). They arrive on the pool, one
        /// at a time; each is posted to the UI thread, where <see cref="OnRemoteHostEvent"/> acts only while this is
        /// still the pane's subscription.
        /// </summary>
        private sealed class RemoteHostSubscription : IDisposable
        {
            private readonly Action<string> _lost;
            private readonly Action<MuxClient> _reconnected;
            private readonly Action _abandoned;
            private readonly Action _stopped;

            public RemoteHostSubscription(TerminalPane pane, MuxConnectionHost host)
            {
                Host = host;
                _lost = _ => pane.PostRemoteHostEvent(this, RemoteHostEvent.ConnectionLost);
                _reconnected = _ => pane.PostRemoteHostEvent(this, RemoteHostEvent.Reconnected);
                _abandoned = () => pane.PostRemoteHostEvent(this, RemoteHostEvent.ReconnectAbandoned);
                _stopped = () => pane.PostRemoteHostEvent(this, RemoteHostEvent.DaemonStopped);
                host.ConnectionLost += _lost;
                host.Reconnected += _reconnected;
                host.ReconnectAbandoned += _abandoned;
                host.DaemonStopped += _stopped;
            }

            public MuxConnectionHost Host { get; }

            public void Dispose()
            {
                Host.ConnectionLost -= _lost;
                Host.Reconnected -= _reconnected;
                Host.ReconnectAbandoned -= _abandoned;
                Host.DaemonStopped -= _stopped;
            }
        }

        /// <summary>Pool thread (the host's event chain): never blocks it.</summary>
        private void PostRemoteHostEvent(RemoteHostSubscription source, RemoteHostEvent hostEvent) =>
            this.Dispatcher.Post(() => OnRemoteHostEvent(source, hostEvent));

        /// <summary>
        /// UI thread. The host's events name no client (Task 20), so the pane decides from its own state: a loss
        /// or a stop matters only while its own session's connection is the one gone; a reconnect only while it
        /// waits to take its dropped session back, and not while its own attempt (an Enter) is running - the
        /// Reconnected that follows that attempt is a duplicate.
        /// </summary>
        private void OnRemoteHostEvent(RemoteHostSubscription source, RemoteHostEvent hostEvent)
        {
            if (Volatile.Read(ref _disposed) || !ReferenceEquals(source, _remoteHost) || _remoteConnecting) return;
            bool ownSessionDropped = Session is MuxClientSession { IsConnected: false };
            switch (hostEvent)
            {
                case RemoteHostEvent.ConnectionLost:
                    // Not while Enter is armed: a later link (another pane's Enter) dropping is not this pane's loss.
                    if (ownSessionDropped && !_muxReconnecting && !_muxConnectionLost) EnterRemoteReconnecting();
                    break;
                case RemoteHostEvent.DaemonStopped:
                    // Its sessions are gone with it: no Reconnected brings this one back, only the user's Enter.
                    if (ownSessionDropped || _muxReconnecting)
                    {
                        UnregisterRemoteMuxSession();
                        EnterRemoteWaitingForEnter(RemoteDaemonStoppedBanner(_remoteHostName), reattachOnReconnect: false);
                    }

                    break;
                case RemoteHostEvent.ReconnectAbandoned:
                    // A give-up because signing in needs the user has recorded that failure by now: the banner says why, and
                    // an agent's tab tells the agent (#631, Codex review of #633).
                    if (_muxReconnecting)
                    {
                        RemoteMuxFailure? failure = (source.Host.LastFailure as RemoteMuxUnavailableException)?.Failure;
                        EnterRemoteWaitingForEnter(
                            RemoteAbandonedBanner(_remoteHostName),
                            detail: RemoteNeedsUserLine(failure, _remoteHostName),
                            agentNeedsUser: TellTheAgentItNeedsTheUser(failure, out _));
                    }

                    break;
                case RemoteHostEvent.Reconnected:
                    if (AwaitsRemoteReattach()) ReattachAfterReconnect();
                    break;
            }
        }

        /// <summary>UI thread: this pane's dropped session is waiting to be taken back (in the loop, or at an Enter banner).</summary>
        private bool AwaitsRemoteReattach() =>
            _muxReattachAfterDrop
            && (_muxReconnecting || _muxConnectionLost)
            && (_muxReattachId ?? MuxSessionIdToRestore) is not null
            && Session is not MuxClientSession { IsConnected: true };

        /// <summary>
        /// UI thread. The link is back (Phase 4 spec §7.4): the factory runs again, off the UI thread, for this pane's
        /// own session, opened Shared. Its snapshot then replaces the screen and the banners with it; a session gone
        /// meanwhile takes the PreviousLost path.
        /// </summary>
        private void ReattachAfterReconnect()
        {
            TerminalLogger.Log($"[TerminalPane] {_remoteHostName} is back; reattaching {_muxReattachId ?? MuxSessionIdToRestore}");
            Reconnect();
        }

        /// <summary>
        /// UI thread. The host lost the link and its loop runs (Phase 4 spec §7.4): the banner under the last screen,
        /// keys stop reaching the session, and what is typed is dropped (<see cref="OnKeyDown"/>).
        /// </summary>
        private void EnterRemoteReconnecting()
        {
            if (Session is MuxClientSession mux) KeepForReattach(mux.Id);
            _muxReattachAfterDrop = true;
            _muxReconnecting = true;
            _muxConnectionLost = false;
            _remoteInputHintShown = false;
            TermView.SetSession(null);
            ApplyMuxSharing(null);
            WriteBanner($"\r\n\x1b[90m{RemoteReconnectingBanner(SanitizeBannerValue(_remoteHostName))}\x1b[0m\r\n");
        }

        /// <summary>
        /// UI thread. Nothing will bring the session back on its own - the loop gave up, the daemon stopped, or a
        /// reattach failed with the link up (Review Focus 2) - so Enter does (<see cref="OnKeyDown"/>, <see cref="Reconnect"/>).
        /// The id is kept. <paramref name="reattachOnReconnect"/>: the host's Reconnected (a give-up ended by any Enter)
        /// still brings this pane back too; false after a stopped daemon, whose sessions went with it.
        /// <paramref name="detail"/>, when given, is a line under the banner (<see cref="RemoteNeedsUserLine"/>).
        /// </summary>
        /// <param name="agentNeedsUser">The tab is an agent's, and has told it it needs the user: <see cref="RemoteAgentOpenedLine"/> comes first.</param>
        private void EnterRemoteWaitingForEnter(string banner, bool reattachOnReconnect = true, string? detail = null, bool agentNeedsUser = false)
        {
            if (Session is MuxClientSession mux && MuxSessionIdToRestore is null) KeepForReattach(mux.Id);
            _muxReattachAfterDrop = reattachOnReconnect;
            _muxReconnecting = false;
            _muxConnectionLost = true;
            TermView.SetSession(null);
            ApplyMuxSharing(null);
            WriteBanner($"\r\n\x1b[90m{SanitizeBannerValue(banner)}\x1b[0m\r\n");
            if (agentNeedsUser) WriteBanner($"\x1b[90m{RemoteAgentOpenedLine}\x1b[0m\r\n");
            if (detail is not null) WriteBanner($"\x1b[90m{SanitizeBannerValue(detail)}\x1b[0m\r\n");
        }

        /// <summary>
        /// #631: a persistent-remote tab an agent opened has no session - on its first connect, or after a reconnect that gave
        /// up - and the agent is told so, with why when it is a failure that needs the user (true then): the tab never asks
        /// them. Any other failure is reported as an exit with no reason, rather than leaving the agent a status from before
        /// (Codex review of #633). Nothing is told for the user's own tab.
        /// </summary>
        private bool TellTheAgentItNeedsTheUser(RemoteMuxFailure? failure, out Ntilde.AgentHost.AgentNeedsUserReason reason)
        {
            reason = default;
            if (!OpenedByAgent) return false;

            Ntilde.AgentHost.AgentNeedsUserReason? needed = AgentNeedsUserFor(failure);
            _agentRegistration?.StatusMachine.NotifyExited(-1, needed);
            if (needed is not { } why) return false;

            reason = why;
            return true;
        }

        /// <summary>
        /// UI thread. A remote session's connection dropped. The host says within about two seconds what that was - a lost
        /// link (its loop) or a stopped daemon - and its event shows the banner; until then the pane only stops
        /// sending to the dead connection and remembers what to reattach (an Enter meanwhile reattaches at once).
        /// </summary>
        private void HandleRemoteMuxDisconnected(MuxClientSession source)
        {
            if (!IsCurrentMux(source) || _muxConnectionLost || _muxReconnecting) return;
            KeepForReattach(source.Id);
            _muxReattachAfterDrop = true;
            ApplyMuxSharing(null);
            TermView.SetSession(null);
            TerminalLogger.Log($"[TerminalPane] connection to {_remoteHostName} lost for session {source.Id}");
        }

        /// <summary>
        /// UI thread. Review Focus 2: a remote attach failed (an attach RPC timeout, a session gone between the open
        /// and the attach). Back into the loop while the host is in it, else the Enter banner - never no session
        /// and no banner.
        /// </summary>
        private void HandleRemoteAttachFailed(MuxClientSession source, Exception error)
        {
            if (!IsCurrentMux(source) || _muxConnectionLost || _muxReconnecting) return;
            TerminalLogger.Log($"[TerminalPane] attach to {source.Id} on {_remoteHostName} failed: {error.Message}");
            if (_remoteHost?.Host.IsReconnecting == true) EnterRemoteReconnecting();
            else
            {
                KeepForReattach(source.Id);
                EnterRemoteWaitingForEnter(RemoteAbandonedBanner(_remoteHostName));
            }
        }

        /// <summary>
        /// UI thread. A remote session whose connection dropped, kept for the next spawn to take back - as a share when it was
        /// one (Phase 5 Task 26): a reconnected share is joined shared and stays a share, and one whose shell ended meanwhile
        /// takes the share-ended path, never a fresh shell in its place.
        /// </summary>
        private void KeepForReattach(Guid sessionId)
        {
            _muxReattachId = sessionId;
            _muxReattachShared = _muxSessionIsShare;
        }

        /// <summary>UI thread: the first key dropped in a reconnect episode says so, once.</summary>
        private void NoteRemoteInputDropped()
        {
            if (_remoteInputHintShown) return;
            _remoteInputHintShown = true;
            WriteBanner($"\r\n\x1b[90m{RemoteInputDroppedHint}\x1b[0m\r\n");
        }

        /// <summary>
        /// UI thread: Enter while the host's loop runs. Not sent, and no reattach of its own: the host tries at once
        /// (a user's GetClient is the loop's attempt now, and its backoff starts over - Task 20), off the UI thread
        /// because it may prompt. Its Reconnected then brings this pane back.
        /// </summary>
        private void RetryRemoteNow()
        {
            if (_remoteHost?.Host is not { } host) return;
            OpenedByAgent = false; // the user's Enter: the tab is theirs now (#631), as ReconnectByUser makes it
            TerminalLogger.Log($"[TerminalPane] Enter while reconnecting to {_remoteHostName}: trying now");
            _ = Task.Run(() => host.GetClient(host.Policy.ConnectTimeout));
        }

        /// <summary>UI thread: whether the current session is on a remote daemon whose host this pane follows.</summary>
        private bool FollowsRemoteSession => _remoteHost is not null && !MuxEndpointId.Parse(MuxEndpoint).IsLocal;

        /// <summary>Phase 4 spec §7.5: a persisted SSH tab whose remote ntilde-mux could not be used.</summary>
        internal const string RemoteMuxUnavailableNoticeTitle = "Persistent SSH unavailable";

        /// <summary>
        /// The notice's action when the remote has no ntilde-mux (<c>RemoteFailureKind.NotInstalled</c>). It names the host
        /// it installs on (final review I1): a toast that merges several hosts' notices offers only the last action.
        /// </summary>
        internal static string RemoteMuxInstallActionLabel(string host) => $"Install ntilde-mux on {host}\u2026";

        /// <summary>The notice's action when the remote ntilde-mux speaks another protocol (<c>RemoteFailureKind.VersionMismatch</c>), naming its host.</summary>
        internal static string RemoteMuxUpdateActionLabel(string host) => $"Update ntilde-mux on {host}\u2026";

        /// <summary>The <see cref="RemoteMuxUnavailableNoticeTitle"/> notice's line: the host the tab is on, and why it will not persist.</summary>
        internal static string RemoteMuxUnavailableMessage(string host, string reason) =>
            $"[{RemoteOutputText.Quote(host)}: {RemoteOutputText.Quote(reason)} \u2014 this tab will not survive a disconnect]";

        /// <summary>Phase 4 spec §7.4: while a remote pane's factory call runs off the UI thread.</summary>
        internal static string RemoteConnectingBanner(string host) => $"[Connecting to {host}\u2026]";

        /// <summary>Phase 4 spec §7.4: the host lost the link and its reconnect loop runs; the last screen stays.</summary>
        internal static string RemoteReconnectingBanner(string host) => $"[Connection to {host} lost \u2014 reconnecting\u2026]";

        /// <summary>Phase 4 spec §7.4: written once per reconnect episode, for the first key dropped in it.</summary>
        internal const string RemoteInputDroppedHint = "[Input is not sent while reconnecting]";

        /// <summary>Phase 4 spec §7.4: the loop gave up (or a reattach failed with the link up); Enter reattaches.</summary>
        internal static string RemoteAbandonedBanner(string host) => $"[Connection to {host} lost] [Press Enter to reconnect]";

        /// <summary>Phase 4 spec §7.4: the remote daemon stopped (the proxy exited 3: its process is gone); Enter reconnects to a new one.</summary>
        internal static string RemoteDaemonStoppedBanner(string host) => $"[ntilde-mux on {host} stopped] [Press Enter to reconnect]";

        /// <summary>Phase 4 spec §7.5: no session could be had from the remote daemon; the id (if any) is kept, and Enter retries.</summary>
        internal static string RemoteUnreachableBanner(string host) => $"[{host} not reachable \u2014 press Enter to retry]";

        /// <summary>Under an Enter banner: an automatic attempt offered the profile's saved password, and it was refused.</summary>
        internal const string RemoteSavedPasswordRefusedLine = "[The saved password was refused \u2014 press Enter to sign in]";

        /// <summary>Under an Enter banner: signing in needs an answer an automatic attempt does not give.</summary>
        internal const string RemoteSignInNeedsYouLine = "[Automatic reconnect can't sign in without you \u2014 press Enter]";

        /// <summary>Under an Enter banner, first, when the tab an agent opened needs the user to sign in (#631).</summary>
        internal const string RemoteAgentOpenedLine = "[An agent opened this connection, and a connection an agent opens never shows a sign-in dialog]";

        /// <summary>Under <see cref="RemoteAgentOpenedLine"/>, in place of <see cref="RemoteSignInNeedsYouLine"/>: no reconnect was automatic.</summary>
        internal const string RemoteSignInNeedsYouAgentLine = "[Signing in needs you \u2014 press Enter to connect yourself]";

        /// <summary>Under an Enter banner: an automatic attempt met a host key the user does not trust, never seen or changed.</summary>
        internal static string RemoteHostKeyLine(string host) => $"[Host key for {host} is unknown or has changed \u2014 press Enter to review]";

        /// <summary>
        /// Under an Enter banner: OpenSSH met a host key that changed since it was trusted. ssh refuses it and asks nothing,
        /// so Enter alone cannot show it: the line says what to do first.
        /// </summary>
        internal static string RemoteHostKeyChangedLine(string host) =>
            $"[Host key for {host} has changed \u2014 if you trust the new key, remove the old one from known_hosts, then press Enter]";

        /// <summary>
        /// The line under an Enter banner (<see cref="RemoteUnreachableBanner"/>, <see cref="RemoteAbandonedBanner"/>) when
        /// signing in needs the user (<see cref="RemoteFailureKind.NeedsUser"/>), by its <see cref="RemoteNeedsUserCause"/>,
        /// so the user knows what the retry needs: <see cref="RemoteSavedPasswordRefusedLine"/>,
        /// <see cref="RemoteHostKeyLine"/> or <see cref="RemoteHostKeyChangedLine"/> naming <paramref name="host"/> (the
        /// banner's own, as the app knows the host), or
        /// <see cref="RemoteSignInNeedsYouLine"/>. Never ssh's or rusty_ssh's own words - a "Permission denied" under a
        /// banner that asks for Enter only confused - which stay in the failure's reason, for the log. The native SSH
        /// backend switched off (codex4 F) keeps its own message, which says what to turn on: Enter alone is refused again.
        /// Null for any other failure, or none: an SSH failure's banner says enough, and ntilde-mux's own failures get the
        /// notice.
        /// </summary>
        internal static string? RemoteNeedsUserLine(RemoteMuxFailure? failure, string host) => failure switch
        {
            null or { Kind: not RemoteFailureKind.NeedsUser } => null,
            { Cause: RemoteNeedsUserCause.SavedPasswordRefused } => RemoteSavedPasswordRefusedLine,
            { Cause: RemoteNeedsUserCause.HostKey } => RemoteHostKeyLine(host),
            { Cause: RemoteNeedsUserCause.HostKeyChanged } => RemoteHostKeyChangedLine(host),
            { Cause: RemoteNeedsUserCause.NativeSshDisabled, Reason: { Length: > 0 } reason } => $"[{reason}]",
            _ => RemoteSignInNeedsYouLine,
        };

        /// <summary>The title of the toast that refuses the remote-files sidebar or a transfer on a persisted remote tab.</summary>
        internal const string RemoteFilesUnavailableNoticeTitle = "Remote Files";

        /// <summary>Spec §15 (codex D2), Phase 5 Task 28: the toast's line while the tab's host is retargeted (<see cref="IsOnRetargetedHost"/>).</summary>
        internal const string RemoteFilesRetargetedMessage = "Not available while this tab still runs on the host it was opened on — reopen the tab to use the new host";

        /// <summary>
        /// UI thread. This persisted remote tab's host still runs where it first connected while its profile names another
        /// destination now (spec §15, codex D2): SFTP, the listing and transfers rebuild their options from the stored profile
        /// and would reach the new destination, so they are refused on this tab. The profile is read at each call - this is
        /// asked at the moment of a request. The host is the one this pane follows, else (a tab that has not attached yet)
        /// its window's host for the endpoint.
        /// </summary>
        internal bool IsOnRetargetedHost
        {
            get
            {
                if (!IsPersistentRemoteTab) return false;
                MuxConnectionHost? host = _remoteHost?.Host
                    ?? (SessionFactory as MuxTerminalSessionFactory)?.Hosts.TryGet(MuxEndpointId.Parse(MuxEndpoint));
                return host?.IsRetargeted == true;
            }
        }

        /// <summary>Tests: bumped by every (re)spawn; a remote result that comes back under an older one is stale.</summary>
        internal int RemoteConnectGenerationForTest => _remoteGen;

        /// <summary>
        /// Runs a remote factory call (Phase 4 spec §7.4) off the UI thread: it may wait minutes on its
        /// connection, behind SSH prompts that are shown through the UI thread. A seam for tests.
        /// </summary>
        /// <remarks>
        /// Long-running by default: each call can block its thread for the remote connect timeout (two minutes), and
        /// several panes restoring at once must not hold pool threads the connection itself needs.
        /// </remarks>
        internal Func<Func<PersistentSessionResult>, Task<PersistentSessionResult>> RunOffUiThread { get; set; } =
            f => Task.Factory.StartNew(f, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
}
