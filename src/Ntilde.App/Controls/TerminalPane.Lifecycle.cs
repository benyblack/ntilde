using Ntilde.Shell;
using Avalonia.Threading;
using Avalonia;
using Ntilde.VT;
using Ntilde.Platform.Ssh.Models;
using Ntilde.Services.Ssh;
using Ntilde.Pty;
using Ntilde.Mux;

namespace Ntilde.Controls
{
    /// <summary>
    /// The end of a pane's session and of the pane: exit handling and its banners, reconnecting, detaching from
    /// the UI thread, and the SSH and remote-mux registrations.
    /// </summary>
    public partial class TerminalPane
    {
        public void Reconnect()
        {
            // Phase 5 Task 23: the shell was let go for a restart that is still stopping the old daemon, whose connection the
            // host still holds - a shell started over it would end with the rest. Held until the restart ends.
            if (_muxRestartHold)
            {
                HoldReconnectForRestart();
                return;
            }

            CloseRemoteFilesSidebar();

            if (Session != null)
            {
                ITerminalSession session = Session;
                UnregisterActiveSshSession(session);
                UnsubscribeRecordingFailure(forgetAttempts: true);
                Session = null;
                _agentRegistration?.SetLifecycle(null);

                // The daemon will never deliver a faulted session again and the factory will not
                // reopen it, so detaching alone would leave its child running unattached forever
                // (the reaper only collects exited sessions). End it; Enter then starts a fresh one.
                if (session is MuxClientSession { IsFaulted: true, IsConnected: true } faulted) faulted.Kill();
                session.Dispose();
            }

            LastExitCode = null;
            // A lost mux connection reopens the same daemon session (InitializeSessionCore consumes
            // _muxReattachId); its snapshot then replaces this banner along with the rest of the screen.
            WriteBanner(_muxReattachId is null && MuxSessionIdToRestore is null
                ? "\r\n\x1b[90m[Reconnecting...]\x1b[0m\r\n"
                : "\r\n\x1b[90m[Reattaching...]\x1b[0m\r\n");
            InitializeSession(ShellCommand, Profile, TermView.Cols, TermView.Rows, ShellArgs);
        }

        /// <remarks>
        /// A mux session whose connection dropped (or whose daemon-side parser faulted) still reports
        /// its process as running - it may well be, in the daemon - so it is named here explicitly.
        /// </remarks>
        internal static bool ShouldReconnectOnEnter(ITerminalSession? session)
        {
            return session == null
                || !session.IsProcessRunning
                || session is MuxClientSession { IsConnected: false }
                || session is MuxClientSession { IsFaulted: true };
        }

        internal void HandleSessionExitForTesting(int code)
        {
            HandleSessionExit(Session, code);
        }

        private void HandleSessionExit(ITerminalSession? session, int code)
        {
            if (session != null && !ReferenceEquals(Session, session))
            {
                return;
            }

            if (session is MuxClientSession) UnregisterRemoteMuxSession(); // its shell ended: nothing left to browse
            LastExitCode = code;

            if (Profile?.Type == ConnectionType.SSH)
            {
                if (_remoteFilesSidebarViewModel?.IsOpen == true)
                {
                    _remoteFilesSidebarViewModel.MarkDisconnected();
                    UpdateRemoteFilesSidebarVisibility();
                    UpdateRemoteFilesSidebarEntryPointState();
                }

                if (SshNeedsUserBanner() is { } needsUser) WriteBanner(needsUser);
                else WriteSshDisconnectedBanner(code);
            }

            ProcessExited?.Invoke(this, code);
        }

        private void WriteSshDisconnectedBanner(int code)
        {
            string exitCodeLine = code == 0
                ? string.Empty
                : $"[Exit code: {code}]\r\n";
            WriteBanner(
                $"\r\n[SSH session disconnected]\r\n{exitCodeLine}[Press Enter to reconnect]\r\n");
        }

        /// <summary>
        /// #311: a local pane whose shell exited. Same shape as the SSH banner — including
        /// dropping the exit-code line when the code is 0 — because the restart it advertises is
        /// the same mechanism: Enter on a dead session reaches <see cref="Reconnect"/>.
        /// No interpolated value is caller- or remote-derived, so nothing needs sanitizing.
        /// </summary>
        internal void WriteLocalExitBanner(int code)
        {
            if (Session is MuxClientSession { WasKilledElsewhere: true })
            {
                // Another window (or `ntilde mux kill`) ended it (spec §7.5): say so, not "exited".
                WriteBanner($"\r\n{MuxKilledElsewhereBanner}\r\n[Press Enter to restart]\r\n");
                return;
            }

            string exitCodeLine = code == 0
                ? string.Empty
                : $"[Exit code: {code}]\r\n";
            WriteBanner(
                $"\r\n[Shell exited]\r\n{exitCodeLine}[Press Enter to restart]\r\n");
        }

        /// <summary>
        /// Strips control characters (C0 incl. ESC, DEL, and C1) from interpolated banner values.
        /// Banner text is parsed as terminal input, so remote- or profile-derived values such as
        /// SSH/spawn error messages could otherwise smuggle escape sequences that move the cursor,
        /// clear the screen, switch modes, or rewrite the title. Only printable content survives.
        /// </summary>
        internal static string SanitizeBannerValue(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            var sb = new System.Text.StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (c < 0x20 || c == 0x7F || (c >= 0x80 && c <= 0x9F))
                {
                    continue;
                }

                sb.Append(c);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Writes Ntilde-generated banner text (connection errors, disconnect/reconnect
        /// notices) into the terminal. Banners are routed through the ANSI parser rather than
        /// <see cref="TerminalBuffer.WriteContent"/> — which writes graphemes verbatim — so that
        /// embedded SGR color codes and CR/LF line breaks are interpreted, instead of leaving
        /// literal "[90m" garbage collapsed onto a single line. A fresh parser is used each time so
        /// the banner renders with a clean slate: it never inherits a partial escape sequence or
        /// accumulated SGR state from the (possibly just-disposed) session parser, and never shares
        /// mutable parser state with the background-thread session output pump.
        /// Interpolated values must be passed through <see cref="SanitizeBannerValue"/> first.
        /// </summary>
        private void WriteBanner(string text)
        {
            if (Buffer == null)
            {
                return;
            }

            new AnsiParser(Buffer).Process(text);
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);

            // Fallback: Ensure session is initialized if it wasn't yet (e.g. nested split timing). Not while a
            // remote connect is in flight, or the host's loop is getting this pane's session back: both end in a
            // session of their own, and a re-parent (a split, a zoom) must not start another connect. Nor for a pane a
            // multiplexer restart let go of (Phase 5 Task 23): it waits for Enter, as its banner says.
            if (Session == null && !_remoteConnecting && !_muxReconnecting && !_muxReleasedForRestart)
            {
                InitializeSession(ShellCommand, Profile, TermView.Cols, TermView.Rows);
            }

            // Force initial render availability
            this.Dispatcher.Post(() =>
            {
                UpdateFocusVisuals(IsKeyboardFocusWithin);
                TermView.InvalidateVisual();
                UpdateCommandAssistOverlayPlacement();
            }, DispatcherPriority.Loaded);
        }

        public void Dispose()
        {
            // Synchronous teardown for UI-thread callers. MainWindow.DisposeControlTree
            // splits the two phases instead, so the (potentially blocking) session
            // teardown runs off the UI thread while the UI-affine detach stays on it.
            DetachFromUiThread()?.Dispose();
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Detaches UI-affine state (control visibility, DispatcherTimer, event handlers)
        /// and transfers ownership of the underlying session to the caller for disposal.
        /// MUST be called on the UI thread. Returns <c>null</c> when already disposed or
        /// when the pane has no session. See #154: previously the whole Dispose ran on a
        /// worker thread with a swallowed catch, so a VerifyAccess throw in the UI-affine
        /// part aborted teardown before the session was disposed, leaking the PTY and its
        /// child shell.
        /// </summary>
        public ITerminalSession? DetachFromUiThread()
        {
            // Enforce the UI-thread contract BEFORE flipping _disposed: if this threw
            // mid-teardown after _disposed was set, every later Dispose() would return
            // early and the session would leak permanently — the exact bug this method
            // exists to prevent.
            this.Dispatcher.VerifyAccess();

            if (_disposed) return null;
            _disposed = true;
            UnsubscribeRecordingFailure(forgetAttempts: true);

            (_agentRegistry ?? Ntilde.AgentHost.AgentSessionRegistry.Instance).Unregister(PaneId);
            if (_agentRegistration != null)
            {
                _agentRegistration.AttentionMachine.Changed -= OnAgentAttentionChanged;
                _agentRegistration.ActabilityChanged -= OnAgentActabilityChanged;
            }

            CloseRemoteFilesSidebar();

            // A remote result still on its way finds the pane disposed and lets its session go (Phase 4 spec §7.4);
            // the host's events stop reaching it now.
            FollowRemoteHost(null);

            // Cancels any suggestion pass still in flight. Without this a debounced pass
            // outlives the pane that owns it and publishes into a surface that is being torn
            // down; see the dispatcher note in InitializeCommandAssist for what that cost.
            _commandAssistController?.Dispose();


            // Its timers too: a running one keeps the view, this pane and the window alive (#543).
            TermView.Retire();

            // Detach handlers on the reused TermView so a disposed pane stops reacting.
            if (_onTermViewResize != null) TermView.OnResize -= _onTermViewResize;
            if (_onTermViewMetricsChanged != null) TermView.MetricsChanged -= _onTermViewMetricsChanged;
            if (_onTermViewMetricsLayout != null) TermView.MetricsChanged -= _onTermViewMetricsLayout;

            if (Buffer != null)
            {
                Buffer.OnScreenSwitched -= OnBufferScreenSwitched;
                if (_agentOutputTracker != null)
                {
                    Buffer.OnInvalidate -= _agentOutputTracker.NotifyInvalidate;
                }
            }
            _agentOutputTracker?.Dispose();
            _agentOutputTracker = null;
            _statusTimer?.Stop();
            _statusTimer = null;
            SftpService.Instance.JobUpdated -= Sftp_JobUpdated;
            UnregisterRemoteMuxSession(); // also when the pane's session is gone already
            if (Session != null)
            {
                ITerminalSession session = Session;
                UnregisterActiveSshSession(session);
                UnsubscribeRecordingFailure(forgetAttempts: true);
                Session = null;
                _agentRegistration?.SetLifecycle(null);
                return session;
            }
            return null;
        }

        private static void RegisterActiveSshSession(ITerminalSession session, TerminalProfile? profile)
        {
            if (profile?.Type != ConnectionType.SSH || profile.SshBackendKind != SshBackendKind.Native)
            {
                return;
            }

            ActiveSshSessionRegistry.Instance.Register(new ActiveSshSessionDescriptor(
                session.Id,
                profile.Id,
                profile.SshBackendKind));
        }

        private void UnregisterActiveSshSession(ITerminalSession session)
        {
            // A daemon session is registered (if at all) through RegisterRemoteMuxSession, and let go of the same way: by id,
            // it could take another pane's registration of the same shared session with it.
            if (session is MuxClientSession)
            {
                UnregisterRemoteMuxSession();
                return;
            }

            ActiveSshSessionRegistry.Instance.Unregister(session.Id);
        }

        /// <summary>UI thread: what this pane registered for its remote daemon session (Phase 5 spec R8); null for none.</summary>
        private ActiveSshSessionDescriptor? _remoteMuxRegistration;

        /// <summary>
        /// UI thread, once <paramref name="mux"/>'s attach is through (Phase 5 spec R8 and its rulings): a session on a remote
        /// daemon, of a Native profile, registers in <see cref="ActiveSshSessionRegistry"/> under its daemon session id - the
        /// id the sidebar, path completion and transfers name it by - with its host's password scope, so each opens a
        /// connection of its own with the passwords typed for that host, as a plain native tab's do. A share registers as an
        /// owned session does: it is the user's own profile either way. An OpenSSH profile does not: its tab has no sidebar,
        /// and its transfers run scp, as a plain OpenSSH tab's do. Replaces this pane's previous registration, if any.
        /// </summary>
        /// <remarks>
        /// The registration follows the session, not the connection: it stays while the link is down and the host
        /// reconnects (those connections are their own), and goes when the session ends - its exit, a stopped daemon, a share
        /// that ended, a new spawn, a restart that let it go - and with the pane (a close, a detach).
        /// </remarks>
        private void RegisterRemoteMuxSession(MuxClientSession mux)
        {
            UnregisterRemoteMuxSession();
            if (Profile is not { Type: ConnectionType.SSH, SshBackendKind: SshBackendKind.Native } profile) return;
            if (_remoteHost?.Host.PasswordScopeId is not Guid scope) return;
            var registration = new ActiveSshSessionDescriptor(mux.Id, profile.Id, SshBackendKind.Native, scope);
            ActiveSshSessionRegistry.Instance.Register(registration);
            _remoteMuxRegistration = registration;
        }

        /// <summary>UI thread: lets go of this pane's own registration (<see cref="RegisterRemoteMuxSession"/>), never another pane's.</summary>
        private void UnregisterRemoteMuxSession()
        {
            if (_remoteMuxRegistration is not { } registration) return;
            _remoteMuxRegistration = null;
            ActiveSshSessionRegistry.Instance.Unregister(registration);
        }
    }
}
