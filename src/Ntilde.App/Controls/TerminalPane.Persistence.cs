using Ntilde.VT;
using Ntilde.Pty;
using Ntilde.Mux;
using Ntilde.Mux.Contracts;
using Ntilde.Shell.Mux;
using Ntilde.Shell.Mux.Remote;

namespace Ntilde.Controls
{
    /// <summary>
    /// Persistent (mux) sessions in a pane: attaching and reattaching, sharing, restarts, and the banners and
    /// notices they raise.
    /// </summary>
    public partial class TerminalPane
    {
        /// <summary>
        /// UI thread. What a <see cref="IPersistentSessionFactory"/> result means for this pane, local or
        /// remote (Phase 4 spec §7.4): its share and endpoint state, the banner or notice it calls for, and
        /// the session to wire - null when there is none to wire (unreachable, or a share that ended).
        /// <paramref name="previousLost"/> as for <see cref="CreateLocalSession"/>.
        /// </summary>
        private ITerminalSession? ApplyPersistentResult(TerminalSessionRequest request, PersistentSessionResult result, out bool previousLost)
        {
            previousLost = false;
            // Spec R2: the quiet restore is this one result's. Anything but an unreachable daemon (whose id is kept
            // for Enter's retry, which is still the restore) settles the id, and a later loss is announced.
            bool quietPreviousLost = MuxQuietPreviousLost;
            if (result.Outcome != PersistentSessionOutcome.DaemonUnreachable && result.Session is not null) MuxQuietPreviousLost = false;
            // A share that fell back to a fresh spawn is this pane's own shell, not a share.
            _muxSessionIsShare = request.AttachShared && result.Outcome == PersistentSessionOutcome.Reattached;
            MuxShareOfExitedSession = _muxSessionIsShare && result.AlreadyExited;
            MuxEndpoint = result.Endpoint;
            if (result.Session is not MuxClientSession)
            {
                // No mux attach will follow to carry a pending "previous shell in use" notice; a later,
                // unrelated attach must not raise it.
                _muxAttachedElsewhereNotice = false;
            }

            if (result.Outcome == PersistentSessionOutcome.ShareEnded)
            {
                EnterMuxShareEnded(request.ExistingMuxSessionId);
                return null;
            }

            if (result.Outcome == PersistentSessionOutcome.DaemonUnreachable || result.Session is null)
            {
                if (result.HostDisplayName is { } host) EnterRemoteUnreachable(request, result, host);
                else EnterMuxUnreachable(request.ExistingMuxSessionId, result);
                return null;
            }

            if (result.Outcome == PersistentSessionOutcome.Unavailable && result.HostDisplayName is { } remoteHost)
            {
                // Phase 4 spec §7.5: SSH worked and the remote ntilde-mux did not; plain SSH stands in, and the
                // notice says on which host and why - with the install flow when that would fix it.
                string reason = result.RemoteFailure?.Reason ?? result.Detail ?? "ntilde-mux could not be used";
                TerminalLogger.Log($"[TerminalPane] persistent SSH unavailable on {remoteHost} ({reason}); starting plain SSH");
                RaisePersistenceNotice(RemoteMuxUnavailableNoticeTitle, RemoteMuxUnavailableMessage(remoteHost, reason), RemoteNoticeActionFor(request, result, remoteHost));
            }
            else if (result.Outcome == PersistentSessionOutcome.Unavailable)
            {
                TerminalLogger.Log($"[TerminalPane] multiplexer unavailable (version mismatch: {result.VersionMismatch}, orphaned daemon: {result.OrphanedDaemon}); starting a non-persistent session");
                if (result.OrphanedDaemon)
                {
                    RaisePersistenceNotice(MuxOrphanedNoticeTitle, MuxOrphanedBanner);
                }
                else if (result.VersionMismatch)
                {
                    // Phase 5 Task 23: the hint stays, and the toast offers the restart that replaces the daemon.
                    RaisePersistenceNotice(MuxUnavailableNoticeTitle, $"{MuxUnavailableBanner}\n{MuxVersionMismatchHint}", LocalMuxRestartAction?.Invoke());
                }
                else
                {
                    RaisePersistenceNotice(MuxUnavailableNoticeTitle, MuxUnavailableBanner);
                }
            }
            else if (result.Outcome == PersistentSessionOutcome.PreviousLost)
            {
                if (quietPreviousLost)
                {
                    // Spec R2: a reboot ends every daemon session, so after one a fresh shell is expected, not news.
                    TerminalLogger.Log("[TerminalPane] previous session ended by a reboot; started a new shell");
                }
                else
                {
                    // Raised only after the attach: a failed attach shows its own banner instead.
                    previousLost = true;
                }
            }
            else if (result.Outcome == PersistentSessionOutcome.AttachedElsewhere)
            {
                // v1: the factory's check found it attached elsewhere; said once the new shell attached.
                _muxAttachedElsewhereNotice = true;
            }

            return result.Session;
        }

        /// <summary>
        /// UI thread. A pane reopening a daemon session could not reach the daemon: no stand-in local
        /// shell (it would bury the running one and lose its id). The id stays pending - so the
        /// session file keeps naming it and orphan adoption does not claim it - and Enter retries.
        /// Written into the buffer like the disconnect banner: no live session paints this screen.
        /// </summary>
        private void EnterMuxUnreachable(Guid? sessionId, PersistentSessionResult result)
        {
            MuxSessionIdToRestore = sessionId;
            _muxConnectionLost = true;
            // Keys must reach OnKeyDown's retry, not a previous (disposed) session the view still holds.
            TermView.SetSession(null);
            TerminalLogger.Log($"[TerminalPane] multiplexer not reachable for session {sessionId} ({result.Detail}); kept for a retry");
            string banner = result.VersionMismatch
                ? $"{MuxUnreachableBanner}\r\n{MuxVersionMismatchHint}"
                : MuxUnreachableBanner;
            WriteBanner($"\r\n\x1b[90m{banner}\x1b[0m\r\n");
            // The banner offers a retry; only the window can say why retrying will not help yet.
            if (result.OrphanedDaemon) RaisePersistenceNotice(MuxOrphanedNoticeTitle, MuxOrphanedBanner);
        }

        /// <summary>
        /// UI thread. A deliberate share's session was gone by the time this pane attached: no fresh
        /// shell (the user chose that one), and the window closes the pane. The banner covers a pane
        /// the window could not close; Enter there starts a new shell.
        /// </summary>
        private void EnterMuxShareEnded(Guid? sessionId)
        {
            UnregisterRemoteMuxSession();
            _muxConnectionLost = true;
            TermView.SetSession(null);
            TerminalLogger.Log($"[TerminalPane] shared session {sessionId} has ended; nothing to attach");
            WriteBanner($"\r\n\x1b[90m{MuxShareEndedBanner}\x1b[0m\r\n");
            this.Dispatcher.Post(() =>
            {
                if (Volatile.Read(ref _disposed)) return;
                MuxShareEnded?.Invoke(this);
            });
        }

        /// <summary>Posts <see cref="PersistenceNotice"/> to this pane's UI thread, with the action its toast offers, if any.</summary>
        private void RaisePersistenceNotice(string title, string message, PersistenceNoticeAction? action = null)
        {
            this.Dispatcher.Post(() =>
            {
                if (Volatile.Read(ref _disposed)) return;
                PersistenceNotice?.Invoke(this, title, message, action);
            });
        }

        /// <summary>
        /// A session that orders resizes in its stream resizes the buffer itself; the view only
        /// requests (spec §8). A mux session is then wired and attached, last.
        /// </summary>
        private void WireStreamOrderedSession(bool muxPreviousLost)
        {
            TermView.DefersBufferResizeToSession = Session is ITerminalSessionCapabilities { OrdersResizeInStream: true };
            if (Session is MuxClientSession mux) WireMuxSession(mux, muxPreviousLost);
            else ApplyMuxSharing(null);
        }

        /// <summary>
        /// True when <paramref name="session"/> answers device queries itself, so this pane's
        /// parser must not write its replies back to the child.
        /// </summary>
        /// <remarks>
        /// Evaluated at call time rather than cached, because the cached TermView handlers in
        /// <see cref="WireReusedTermViewHandlers"/> outlive any individual session: a pane can
        /// reconnect from a multiplexer-backed session to a local one and back.
        /// </remarks>
        private static bool SessionAnswersDeviceQueries(ITerminalSession? session)
            => session is ITerminalSessionCapabilities { AnswersDeviceQueries: true };

        internal const string MuxUnavailableBanner = "[Multiplexer unavailable \u2014 this session will not persist]";
        internal const string MuxVersionMismatchHint = "[The running multiplexer is a different version \u2014 run 'ntilde mux kill-server --force' to replace it]";
        internal const string MuxPreviousLostBanner = "[Previous session was lost \u2014 started a new shell]";
        internal const string MuxDisconnectedBanner = "[Multiplexer disconnected] [Press Enter to reconnect]";
        internal const string MuxRestartingBanner = "[The multiplexer is restarting \u2014 the new shell starts once it is back]";

        /// <summary>Phase 5 Task 23: a remote pane's held Enter while its host's ntilde-mux restarts; <paramref name="host"/> is sanitized by the caller.</summary>
        internal static string RemoteMuxRestartingBanner(string host) => $"[ntilde-mux on {host} is restarting \u2014 the new shell starts once it is back]";
        internal const string MuxUnreachableBanner = "[Multiplexer not reachable \u2014 press Enter to retry]";
        internal const string MuxSessionFailedBanner = "[Multiplexer session failed \u2014 press Enter to start a new shell]";
        internal const string MuxUnavailableNoticeTitle = "Session not persistent";
        internal const string MuxOrphanedNoticeTitle = "Multiplexer";
        internal const string MuxOrphanedBanner = "[Another multiplexer is running but cannot be reached. Shells in this window are not kept. Close other ntilde windows or end the old multiplexer.]";
        internal const string MuxPreviousLostNoticeTitle = "Previous session lost";
        internal const string MuxAttachedElsewhereBanner = "[Your previous shell is open in another window \u2014 started a new shell]";
        internal const string MuxAttachedElsewhereNoticeTitle = "Previous shell in use";
        internal const string MuxKilledElsewhereBanner = "[Shell ended from another window]";
        internal const string MuxShareEndedBanner = "[The shell you chose has ended]";
        internal const string MuxShareEndedNoticeTitle = "Attach to session";
        internal const string MuxAdoptionLostBanner = "[This shell is open in another window \u2014 press Enter to start a new shell]";

        /// <summary>
        /// The action a remote notice offers for its failure (spec §7.5: "Install ntilde-mux on &lt;host&gt;…"), from the
        /// window, which reads its install flow at call time: for the failure, the profile's id and its <c>user@host</c>.
        /// Null offers none.
        /// </summary>
        internal Func<RemoteMuxFailure?, Guid, string, PersistenceNoticeAction?>? RemoteNoticeAction { get; set; }

        /// <summary>
        /// The action the "Session not persistent" notice offers when the local daemon speaks another protocol version
        /// (Phase 5 Task 23): the window's "Restart multiplexer now", read when the notice is raised. Null offers none; the
        /// notice keeps its hint either way.
        /// </summary>
        internal Func<PersistenceNoticeAction?>? LocalMuxRestartAction { get; set; }

        /// <summary>
        /// UI thread. Asks the window for its pass that releases the remote hosts no pane needs (final review F1), once the
        /// kill of a stale result's shell is queued (codex C1, <see cref="KillStaleRemoteSession"/>), and for any other
        /// discarded result, for which the factory may have queued a kill itself (codex E1): the pass this pane's
        /// close scheduled may have run before that result came back. Set when the window wires the pane and kept when it
        /// unwires it, since a closed pane's result still arrives. Null: nothing to ask (a pane with no window).
        /// </summary>
        internal Action? RemoteMuxReleaseCheck { get; set; }

        /// <summary>
        /// UI thread. This pane is (or is about to be) a session on a remote daemon (Phase 4 spec §8.4). Its remote files
        /// and transfers open connections of their own, as a plain SSH tab's do (Phase 5 spec R8), unless its host is
        /// retargeted (<see cref="IsOnRetargetedHost"/>). False for the plain SSH session that stands in when the remote
        /// ntilde-mux could not be used.
        /// </summary>
        internal bool IsPersistentRemoteTab => !MuxEndpointId.Parse(MuxEndpoint).IsLocal && Session is null or MuxClientSession;

        /// <summary>UI thread. The remote host as this pane's banners name it (<c>user@host</c>); empty until a remote session was asked for.</summary>
        internal string RemoteHostName => _remoteHostName;

        /// <summary>
        /// UI thread. Final review F1: the remote endpoint whose connection this pane still needs, or null. A pane needs
        /// it while its session is on that endpoint's daemon (connected or not: a dropped one is taken back through
        /// it), while it keeps a session id pending there (a restored tab never shown, a reattach waiting for Enter,
        /// a reopen that could not reach the host), and while its connect is in flight. A local pane, a plain SSH
        /// pane and a new remote tab whose connect failed (its Enter asks for the connection again) need none. The
        /// window releases a remote host no pane needs (<c>MuxConnectionHosts.Release</c>).
        /// <para>
        /// A plain SSH pane needs none even with an id pending there (its profile stopped persisting, spec §15): until
        /// it closes - when its kill asks for the connection again, building a host if need be (codex C2) - or spawns
        /// again, nothing it does uses the connection. Counting it would keep a host built for another pane's kill
        /// connected for as long as it stayed open.
        /// </para>
        /// </summary>
        internal MuxEndpointId? RemoteMuxEndpointInUse
        {
            get
            {
                MuxEndpointId endpoint = MuxEndpointId.Parse(MuxEndpoint);
                if (endpoint.IsLocal) return null;
                bool plainSsh = Session is not null and not MuxClientSession;
                bool inUse = Session is MuxClientSession
                    || _remoteConnecting
                    || (!plainSsh && (MuxSessionIdToRestore is not null || _muxReattachId is not null));
                return inUse ? endpoint : null;
            }
        }

        /// <summary>
        /// UI thread. Phase 5 Task 26: this pane is a share - it joined its session through "Attach to session…", shown or
        /// still pending - and who else shows that session cannot be known right now: its connection is down or reconnecting,
        /// its attach has not completed, or its remote host has no connection. Its close then detaches, local or remote, and
        /// never ends the shell, which may be another client's. False for a session of the pane's own, and for a pane that let
        /// go of its shell for a multiplexer restart (Task 23): with no session and no id, it has no share left to protect.
        /// </summary>
        internal bool IsMuxShareWithSharingUnknown => MuxShareIdWithSharingUnknown is not null;

        /// <summary>
        /// UI thread. The shared session's id when <see cref="IsMuxShareWithSharingUnknown"/> holds - the one a detach notice
        /// names (fix round 1) - else null.
        /// </summary>
        internal Guid? MuxShareIdWithSharingUnknown
        {
            get
            {
                Guid? share = _muxSessionIsShare && Session is MuxClientSession shown ? shown.Id
                    : MuxAttachSharedToRestore && MuxSessionIdToRestore is Guid pending ? pending
                    : _muxReattachShared ? _muxReattachId
                    : null;
                if (share is null) return null;
                bool known = Session is MuxClientSession { IsConnected: true, IsAttached: true }
                    && !_muxConnectionLost && !_muxReconnecting && MuxSessionIdToRestore is null
                    && (MuxEndpointId.Parse(MuxEndpoint).IsLocal || _remoteHost?.Host.CurrentClient is not null);
                return known ? null : share;
            }
        }

        /// <summary>
        /// Set by startup adoption (spec §9 orphans): this pane was opened only to show an orphaned
        /// daemon session. Cleared once it attached. Losing the attach to another instance then closes
        /// the pane instead of starting a new shell.
        /// </summary>
        internal bool MuxAdoptedOrphan { get; set; }

        /// <summary>Raised on the UI thread when an adopted orphan was claimed by another instance first (MainWindow closes the pane).</summary>
        internal event Action<TerminalPane>? MuxAdoptionLost;

        /// <summary>Raised on the UI thread when a deliberate share found its session gone (MainWindow closes the pane).</summary>
        internal event Action<TerminalPane>? MuxShareEnded;

        /// <summary>
        /// UI thread: the current session is a share of a shell that had already exited when it was
        /// chosen. Its exit arrives with the attach, so MainWindow keeps the pane (last screen and exit
        /// banner) instead of applying ShellExitPolicy. Reset by every new local session.
        /// </summary>
        internal bool MuxShareOfExitedSession { get; private set; }

        /// <summary>UI thread: how many OTHER clients are attached to this pane's mux session (0 = not shared, or unknown).</summary>
        internal int MuxOtherClients { get; private set; }

        /// <summary>Raised on the UI thread when <see cref="MuxOtherClients"/> changes (MainWindow marks the tab).</summary>
        internal event Action<TerminalPane>? MuxSharingChanged;

        /// <summary>
        /// UI thread. <paramref name="interactiveOthers"/> is <see cref="MuxClientSession.InteractiveOthers"/>:
        /// read-only observers are not counted. Null (unknown, disconnected, replaced session) hides the badge.
        /// </summary>
        internal void ApplyMuxSharing(int? interactiveOthers)
        {
            int others = interactiveOthers is int n ? Math.Max(0, n) : 0;
            MuxSharedIndicator.IsVisible = others > 0;
            MuxSharedText.Text = others > 0 ? $"shared with {others}" : string.Empty;
            if (others == MuxOtherClients) return;
            MuxOtherClients = others;
            MuxSharingChanged?.Invoke(this);
        }

        /// <summary>
        /// UI thread. Latest resize wins (spec §7.5): when another client has resized the shared
        /// session, this view letterboxes; when it regains focus it asks for its own grid again, so the
        /// window the user is typing in wins. A no-op when the grids already agree.
        /// </summary>
        internal void ReassertMuxGrid()
        {
            if (Session is not MuxClientSession { IsAttached: true } mux || Buffer is not { } buffer) return;
            int cols = TermView.Cols, rows = TermView.Rows;
            if (cols <= 0 || rows <= 0) return;
            if (buffer.Cols == cols && buffer.Rows == rows) return;
            mux.Resize(cols, rows);
        }

        /// <summary>UI thread: the terminal gained keyboard focus (focus visuals, and the grid re-request of spec §7.5).</summary>
        internal void HandleTermViewGotFocus()
        {
            UpdateFocusVisuals(true);
            ReassertMuxGrid();
        }

        /// <summary>
        /// Raised on the UI thread with (title, message, action) when this pane's session will not persist
        /// or replaced a lost one. MainWindow shows it as a toast (never written into the buffer); a
        /// non-null action is the toast's button (Phase 4 spec §7.5: "Install ntilde-mux…").
        /// </summary>
        internal event Action<TerminalPane, string, string, PersistenceNoticeAction?>? PersistenceNotice;

        /// <summary>Set by SessionManager.RestorePaneTree: the daemon session this pane should reopen (consumed once).</summary>
        internal Guid? MuxSessionIdToRestore { get; set; }

        /// <summary>Set with <see cref="MuxSessionIdToRestore"/> by "Attach to session…": join it shared (consumed once).</summary>
        internal bool MuxAttachSharedToRestore { get; set; }

        /// <summary>
        /// Set with <see cref="MuxSessionIdToRestore"/> by SessionManager.RestorePaneTree (spec R2) on a pane reopening a
        /// local daemon session that a reboot or logoff ended (the startup restore marks such sessions in the loaded file,
        /// and the session file keeps the mark until the pane spawns): the fresh shell starts without the "previous session
        /// lost" notice. Covers that restore only: the first result that settles the id clears it, so a loss later in the
        /// pane's life is announced as usual.
        /// </summary>
        internal bool MuxQuietPreviousLost { get; set; }

        /// <summary>
        /// The endpoint (a <see cref="MuxEndpointId"/> string, Phase 4 spec §5) the current session lives on,
        /// or the pending <see cref="MuxSessionIdToRestore"/> lives on; null when neither is persistent. Set by
        /// SessionManager.RestorePaneTree with the id, so a pane saved before it ever spawned writes it back.
        /// </summary>
        internal string? MuxEndpoint { get; set; }

        /// <summary>Raised on the UI thread after a mux session attached (MainWindow saves the session file).</summary>
        internal event Action<TerminalPane>? PersistentSessionAttached;

        private Guid? _muxReattachId;    // UI thread: set when the connection dropped, consumed by Reconnect
        private bool _muxConnectionLost; // UI thread
        private bool _muxAttachedElsewhereNotice; // UI thread: raise the notice once the replacement shell attached
        private bool _muxSessionIsShare;          // UI thread: the current session was a deliberate share
        internal bool MuxSessionIsShare => _muxSessionIsShare;
        private bool _muxReattachShared;          // UI thread: the lost session was a share, reattach it shared

        // Phase 4 spec §7.4, a pane on a remote daemon. All UI thread.
        private int _remoteGen;                     // bumped by every (re)spawn: a remote result of an older one is stale
        private bool _remoteConnecting;             // the remote factory call of _remoteGen is in flight
        private bool _muxReconnecting;              // the host's reconnect loop runs for this pane's lost session
        private bool _remoteInputHintShown;         // RemoteInputDroppedHint written in this episode
        private bool _muxReattachAfterDrop;         // the pending id is this pane's own dropped session: reattach it Shared
        private string _remoteHostName = string.Empty; // user@host, for the banners
        private RemoteHostSubscription? _remoteHost; // the host whose events this pane follows

        /// <summary>
        /// Whether the next spawn joins its session shared: a pending "Attach to session…" id, or the
        /// reattach of a share whose connection dropped. Consumed with the id.
        /// </summary>
        /// <param name="plainSsh">An SSH pane that does not go to a remote daemon: it consumes nothing.</param>
        private bool TakeMuxAttachShared(bool plainSsh)
        {
            if (plainSsh) return false;
            bool shared = _muxReattachId is not null
                ? _muxReattachShared
                : MuxSessionIdToRestore is not null && MuxAttachSharedToRestore;
            MuxAttachSharedToRestore = false;
            _muxReattachShared = false;
            return shared;
        }

        /// <summary>
        /// A plain SSH pane (one whose profile does not persist remote sessions, Phase 4 spec §7.6) never reopens a
        /// daemon session, and leaves any pending id unconsumed - on its endpoint, so the next save writes both back.
        /// </summary>
        private Guid? TakeMuxSessionIdToRestore(bool plainSsh) => plainSsh ? null : TakeMuxSessionIdToRestore();

        private Guid? TakeMuxSessionIdToRestore()
        {
            Guid? id = _muxReattachId ?? MuxSessionIdToRestore;
            _muxReattachId = null;
            MuxSessionIdToRestore = null;
            return id;
        }

        /// <summary>
        /// UI thread, for a pane that runs plain SSH: its profile no longer keeps remote sessions. Such a pane keeps a pending id
        /// for the session file and its close's kill (Phase 4 spec §7.6) - but not a share's (Phase 5 Task 26). That shell is
        /// another client's, which this pane neither saves nor ends: the id is let go, and the log says so.
        /// </summary>
        private void LetGoOfPendingShareOnPlainSsh()
        {
            if (!MuxAttachSharedToRestore || MuxSessionIdToRestore is not Guid share) return;
            TerminalLogger.Log($"[TerminalPane] the profile no longer keeps remote sessions, so this tab runs plain SSH; letting go of shared session {share}, which stays with its other clients");
            MuxSessionIdToRestore = null;
            MuxAttachSharedToRestore = false;
        }

        /// <summary>
        /// UI thread, after every other handler is wired: rebuilds the parser to match the daemon's,
        /// subscribes the stream events, then attaches - so the snapshot cannot arrive before its
        /// handler exists (spec §8).
        /// </summary>
        private void WireMuxSession(MuxClientSession mux, bool previousLost)
        {
            ApplyMuxSharing(null);
            CreateAndWireParser(mux.ForceConPtyFiltering, muxBacked: true);
            float cw = TermView.Metrics.CellWidth, ch = TermView.Metrics.CellHeight;
            if (cw > 0) Parser!.CellWidth = cw;
            if (ch > 0) Parser!.CellHeight = ch;

            mux.SixelCellSizeChanged += (width, height) =>
            {
                if (!IsCurrentMux(mux) || Parser is not { } parser) return;
                parser.SixelCellSizeOverride = new SixelCellSize(width, height);
            };
            mux.SnapshotReceived += snapshot => HandleMuxSnapshot(mux, snapshot);
            mux.StreamResize += (c, r) => HandleMuxStreamResize(mux, c, r);
            // Disconnected can fire on any thread (even concurrently with a delivery); both marshal. A remote
            // session's host says what the drop was (Phase 4 spec §7.4), and its event shows the banner.
            bool remote = FollowsRemoteSession;
            mux.Disconnected += _ => this.Dispatcher.Post(() =>
            {
                if (remote) HandleRemoteMuxDisconnected(mux);
                else HandleMuxConnectionLost(mux, MuxDisconnectedBanner);
            });
            // A faulted session is gone for good (the daemon will not deliver it again): its own
            // wording, and no reattach - Enter ends it and starts a new shell (see Reconnect).
            mux.Faulted += _ => this.Dispatcher.Post(() => HandleMuxConnectionLost(mux, MuxSessionFailedBanner, reattach: false));
            // Delivery thread; marshal. The attach itself changes the count, so a v2 daemon announces
            // the initial sharing right after the snapshot. One already posted when the connection
            // dropped must not re-show the badge that the loss hid.
            // A remote drop sets neither flag until its host speaks, hence the connection check too.
            mux.SessionChanged += () =>
            {
                // Delivery thread: apply the capability ceiling before subsequent output,
                // not after a UI dispatch. Older daemons stay at disambiguation-only.
                if (IsCurrentMux(mux) && mux.IsConnected) Buffer?.Modes.KittyKeyboard.SetSupportedFlags(mux.KittyKeyboardSupportedFlags);
                this.Dispatcher.Post(() =>
                {
                    if (IsCurrentMux(mux) && !_muxConnectionLost && !_muxReconnecting && mux.IsConnected) ApplyMuxSharing(mux.InteractiveOthers);
                });
            };
            _ = AttachMuxAsync(mux, previousLost);
        }

        /// <remarks>
        /// A delivery can run just after the session was disposed (MuxClientSession.Dispose does not
        /// wait for one in progress), so every stream handler checks this first.
        /// </remarks>
        private bool IsCurrentMux(MuxClientSession source) => !Volatile.Read(ref _disposed) && ReferenceEquals(Session, source);

        /// <summary>
        /// Delivery thread (MuxClientRead). Restores here, under the buffer's own lock - never
        /// marshalled to the UI thread and never blocking, because a cancelled attach can be waiting
        /// on this handler. Internal for tests.
        /// </summary>
        internal void HandleMuxSnapshot(MuxClientSession source, TerminalStateSnapshot snapshot)
        {
            if (!IsCurrentMux(source) || Buffer is not { } buffer || Parser is not { } parser) return;
            try
            {
                buffer.Modes.KittyKeyboard.SetSupportedFlags(source.KittyKeyboardSupportedFlags);
                TerminalStateTransfer.Restore(buffer, parser, snapshot);
            }
            catch (Exception ex)
            {
                TerminalLogger.Log($"[TerminalPane] snapshot restore for {source.Id} failed: {ex.Message}");
                return;
            }

            this.Dispatcher.Post(() => { if (IsCurrentMux(source)) TermView.NotifySessionResizedBuffer(); });
            QueueOutputUiRefresh();
        }

        /// <summary>Delivery thread: the mux resized at this point in the stream; the pane follows exactly there.</summary>
        private void HandleMuxStreamResize(MuxClientSession source, int cols, int rows)
        {
            if (!IsCurrentMux(source) || Buffer is not { } buffer) return;
            buffer.Resize(cols, rows);
            this.Dispatcher.Post(() => { if (IsCurrentMux(source)) TermView.NotifySessionResizedBuffer(); });
            QueueOutputUiRefresh();
        }

        /// <summary>
        /// UI thread. Phase 5 Task 23 (review item 6, and round 2 item 6 for a remote one): the window is about to restart the
        /// daemon this pane's shell runs in, which ends the shell. The pane lets go of it first - a plain detach, so the
        /// daemon's stop cannot reach it as the shell's exit, which would take the exit policy's path (a local pane could close,
        /// a remote one would claim its SSH session ended) - and shows the banner the daemon's end shows anyway: "multiplexer
        /// disconnected", or "ntilde-mux on host stopped". It keeps no session and no id (that shell is about to end), so no
        /// save names it, and Enter starts a new shell. Until <see cref="EndMuxRestartHold"/>, Enter waits (round 2 M1): the
        /// host still holds the old daemon's connection, and a shell started there would end with the rest. False when the
        /// pane shows no daemon session.
        /// </summary>
        internal bool LetGoOfMuxSessionForRestart()
        {
            if (Session is not MuxClientSession mux) return false;
            _muxRestartHold = true; // first: whatever fails below, the window ends the hold it sees here
            _muxReleasedForRestart = true;
            UnregisterRemoteMuxSession(); // the restart ends that shell
            _muxRestartHoldRemote = FollowsRemoteSession;
            if (_muxRestartHoldRemote)
            {
                EnterRemoteWaitingForEnter(RemoteDaemonStoppedBanner(_remoteHostName), reattachOnReconnect: false);
            }
            else
            {
                HandleMuxConnectionLost(mux, MuxDisconnectedBanner, reattach: false);
                _muxConnectionLost = true; // also when it was lost already, and HandleMuxConnectionLost left it as it was
            }

            _muxReattachId = null;
            _muxReattachShared = false;
            _muxReattachAfterDrop = false;
            _muxReconnectAfterRestart = false;
            _muxRestartHoldNoted = false;
            Session = null;
            _agentRegistration?.SetLifecycle(null);
            // Keys reach OnKeyDown, never a session that no longer delivers anything to this pane.
            TermView.SetSession(null);
            mux.Detach(userDetached: false);
            return true;
        }

        /// <summary>
        /// UI thread. The restart that let go of this pane's shell (<see cref="LetGoOfMuxSessionForRestart"/>) is over, however
        /// it ended: Enter starts a new shell again, and one pressed meanwhile does so now.
        /// </summary>
        internal void EndMuxRestartHold()
        {
            if (!_muxRestartHold) return;
            _muxRestartHold = false;
            if (!_muxReconnectAfterRestart || Volatile.Read(ref _disposed)) return;
            _muxReconnectAfterRestart = false;
            Reconnect();
        }

        /// <summary>
        /// UI thread. Asked to reconnect while held (<see cref="Reconnect"/>, from Enter or the palette): it reconnects once the
        /// restart ends, and says so once, in words chosen by the pane's kind - never by what a daemon said.
        /// </summary>
        private void HoldReconnectForRestart()
        {
            _muxReconnectAfterRestart = true;
            if (_muxRestartHoldNoted) return;
            _muxRestartHoldNoted = true;
            string line = _muxRestartHoldRemote ? RemoteMuxRestartingBanner(SanitizeBannerValue(_remoteHostName)) : MuxRestartingBanner;
            WriteBanner($"\x1b[90m{line}\x1b[0m\r\n");
        }

        /// <summary>Whether a reconnect (<see cref="LetGoOfMuxSessionForRestart"/>) waits for its restart to end. UI thread.</summary>
        private bool _muxRestartHold;
        private bool _muxRestartHoldRemote;     // the pane let go of a remote daemon's shell
        private bool _muxReconnectAfterRestart; // a reconnect was asked for while held
        private bool _muxRestartHoldNoted;      // the held line is written once

        /// <summary>
        /// The pane let go of its shell for a restart and has started none since (round 3): only a reconnect (Enter, the
        /// palette) starts one, as its banner says - being shown again (the attach fallback) does not. UI thread; reset with
        /// every new session.
        /// </summary>
        private bool _muxReleasedForRestart;

        /// <summary>UI thread. The daemon or the connection is gone; the shell may still be running there.</summary>
        private void HandleMuxConnectionLost(MuxClientSession source, string banner, bool reattach = true)
        {
            if (!IsCurrentMux(source) || _muxConnectionLost) return;
            _muxConnectionLost = true;
            ApplyMuxSharing(null);
            if (!reattach) UnregisterRemoteMuxSession(); // a faulted session is gone for good
            _muxReattachId = reattach ? source.Id : null;
            _muxReattachShared = reattach && _muxSessionIsShare;
            // Input must not reach a daemon session this pane is not showing (after a failed attach
            // the connection is still up, and SendInput would deliver it). With no session the view
            // leaves keys unhandled, so Enter reaches OnKeyDown's reconnect.
            if (!source.IsAttached) TermView.SetSession(null);
            TerminalLogger.Log($"[TerminalPane] multiplexer connection lost for session {source.Id}");
            WriteBanner($"\r\n\x1b[90m{banner}\x1b[0m\r\n");
            // Deliberately NOT ProcessExited: MainWindow would apply ShellExitPolicy and may close the pane.
        }

        private async Task AttachMuxAsync(MuxClientSession mux, bool previousLost)
        {
            MuxPresentation presentation = BuildMuxPresentation(); // UI thread, before the first await
            int scrollback = Math.Clamp(_settings?.MaxHistory ?? 10_000, 0, 50_000);
            bool remote = FollowsRemoteSession;
            try
            {
                await mux.AttachAsync(scrollback, presentation).ConfigureAwait(false);
                this.Dispatcher.Post(() =>
                {
                    if (!IsCurrentMux(mux)) return;
                    _muxReattachAfterDrop = false; // the drop (if any) is over: this session is shown again
                    if (remote) RegisterRemoteMuxSession(mux);
                    if (previousLost)
                    {
                        TerminalLogger.Log($"[TerminalPane] previous multiplexer session was lost; started {mux.Id}");
                        PersistenceNotice?.Invoke(this, MuxPreviousLostNoticeTitle, MuxPreviousLostBanner, null);
                    }

                    if (_muxAttachedElsewhereNotice)
                    {
                        _muxAttachedElsewhereNotice = false;
                        PersistenceNotice?.Invoke(this, MuxAttachedElsewhereNoticeTitle, MuxAttachedElsewhereBanner, null);
                    }

                    MuxAdoptedOrphan = false; // it is this pane's shell now
                    PersistentSessionAttached?.Invoke(this);
                });
            }
            catch (MuxProtocolException ex) when (ex.Code == MuxErrorCodes.SessionAttached)
            {
                // The restore lost to another window, atomically on the daemon (spec §7.1): start fresh.
                TerminalLogger.Log($"[TerminalPane] session {mux.Id} is attached in another window; starting a new shell");
                this.Dispatcher.Post(() => RestartAfterAttachedElsewhere(mux));
            }
            catch (Exception ex)
            {
                TerminalLogger.Log($"[TerminalPane] attach to {mux.Id} failed: {ex.Message}");
                this.Dispatcher.Post(() =>
                {
                    if (remote) HandleRemoteAttachFailed(mux, ex);
                    else HandleMuxConnectionLost(mux, $"[Multiplexer attach failed: {SanitizeBannerValue(ex.Message)}] [Press Enter to reconnect]");
                });
            }
        }

        /// <summary>UI thread. Drops the refused session (a detach) and spawns a fresh shell; the notice follows its attach.</summary>
        private void RestartAfterAttachedElsewhere(MuxClientSession source)
        {
            if (!IsCurrentMux(source)) return;
            _muxReattachId = null;
            MuxSessionIdToRestore = null;
            if (MuxAdoptedOrphan)
            {
                GiveUpAdoptedOrphan(source);
                return;
            }

            _muxAttachedElsewhereNotice = true;
            Reconnect();
        }

        /// <summary>
        /// UI thread. An adopted crash orphan was claimed by another instance first: that window shows
        /// it now, and this tab only existed to show it. No new shell and no toast; the window closes
        /// the pane. The banner covers a pane the window could not close, where Enter starts a new shell.
        /// </summary>
        private void GiveUpAdoptedOrphan(MuxClientSession source)
        {
            MuxAdoptedOrphan = false;
            Session = null;
            _agentRegistration?.SetLifecycle(null);
            TermView.SetSession(null);
            source.Dispose(); // a plain detach of an attach that never happened: never a kill
            _muxConnectionLost = true;
            TerminalLogger.Log($"[TerminalPane] adopted session {source.Id} is open in another window; closing this tab");
            WriteBanner($"\r\n\x1b[90m{MuxAdoptionLostBanner}\x1b[0m\r\n");
            MuxAdoptionLost?.Invoke(this);
        }

        /// <summary>
        /// What the daemon's parser needs from this pane's presentation: grid, cell size in device
        /// pixels (DIPs x effective render scaling - the unit in-band size reports use), the
        /// profile-effective theme's default colours and the kitty keyboard gate.
        /// </summary>
        internal MuxPresentation BuildMuxPresentation()
        {
            double scale = TermView.EffectiveRenderScaling;
            if (double.IsNaN(scale) || scale <= 0) scale = 1;
            TerminalTheme? theme = _settings is null ? null : BuildEffectiveSettings(_settings).ActiveTheme;
            int cols = TermView.Cols > 0 ? TermView.Cols : Buffer?.Cols ?? 80;
            int rows = TermView.Rows > 0 ? TermView.Rows : Buffer?.Rows ?? 24;
            return new MuxPresentation
            {
                Cols = cols,
                Rows = rows,
                CellWidthPx = (float)(TermView.Metrics.CellWidth * scale),
                CellHeightPx = (float)(TermView.Metrics.CellHeight * scale),
                SupportsSixelScrolling = true,
                DefaultFg = theme?.Foreground.ToUint(),
                DefaultBg = theme?.Background.ToUint(),
                KittyKeyboardEnabled = _settings?.EnableKittyKeyboardProtocol ?? true,
                KittyKeyboardSupportedFlags = KittyKeyboardState.SupportedFlags,
            };
        }
    }
}
