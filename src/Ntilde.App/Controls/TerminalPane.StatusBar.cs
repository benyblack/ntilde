using Ntilde.Shell;
using Ntilde.Shell.Theming;
using Avalonia.Controls;
using System.Net.NetworkInformation;

namespace Ntilde.Controls
{
    /// <summary>
    /// The pane's status bar: SFTP transfer progress, port forwarding, and the agent segment.
    /// </summary>
    public partial class TerminalPane
    {
        // Whether UpdateStatusBarUI has rendered the SSH forwarding half of the
        // status bar at least once. See UpdateForwardingStatus.
        private bool _forwardingStatusUiBuilt;

        // Every dispatch in this class goes to this.Dispatcher - the pane's own, captured by
        // AvaloniaObject at construction - and never to the Dispatcher.UIThread static (#423).
        //
        // The static's getter binds UI-thread identity to *the calling thread* whenever its
        // backing field is null. Under the headless lane's PerTest isolation that field is nulled
        // at every test boundary, and again inside HeadlessUnitTestSession.EnsureIsolatedApplication
        // immediately before AppBuilder.SetupUnsafe() runs. A background thread that reads the
        // static inside that window becomes the UI thread, after which SetupUnsafe's
        // Compositor ctor -> DefaultRenderLoop.Add -> VerifyAccess() throws - outside the try in
        // DispatchCore, so it unwinds the one dispatcher loop the assembly shares and every
        // remaining test blocks forever. Two dumps caught exactly that, one local and one on CI's
        // ubuntu lane, both with five live RustPtySessions still running.
        //
        // Half this class's dispatches are raised on threads that are not the UI thread and say so
        // in their own comments: the agent-attention and actability handlers on the endpoint's IPC
        // or timer thread, the SFTP job handler on a transfer worker, the parser mark callbacks on
        // the PTY read thread, the exit paths on the PTY exit watcher. Any of them firing on a pane
        // that outlived its test is a candidate reader.
        //
        // this.Dispatcher costs nothing on either count. It is assigned by AvaloniaObject's own
        // field initializer (= Dispatcher.CurrentDispatcher), so the pane already holds it before
        // any of this runs: reading it binds nothing that `new TerminalPane()` had not already
        // bound. It is also the more correct reading in production, where a pane belongs to one
        // dispatcher for its whole life. PR #416 made this argument for the Command Assist
        // dispatch and it applies unchanged to the rest of the class.
        private void Sftp_JobUpdated(object? sender, TransferJob job)
        {
            if (job.SessionId != Session?.Id) return;

            this.Dispatcher.Post(() =>
            {
                var activeJobs = SftpService.Instance.Jobs
                    .Where(j => j.SessionId == Session?.Id && j.State == TransferState.Running)
                    .ToList();

                if (activeJobs.Count > 0)
                {
                    TransferJob primaryJob = activeJobs
                        .OrderByDescending(j => j.StartedAt)
                        .First();

                    SftpStatus.IsVisible = true;
                    SftpIcon.Text = activeJobs.Count > 1
                        ? "\u21C5"
                        : primaryJob.Direction == TransferDirection.Upload ? "\u2B06" : "\u2B07";
                    SftpText.Text = BuildRunningTransferStatus(primaryJob, activeJobs.Count);
                }
                else
                {
                    var lastJob = SftpService.Instance.Jobs
                        .Where(j => j.SessionId == Session?.Id)
                        .OrderByDescending(j => j.FinishedAt)
                        .FirstOrDefault();

                    if (lastJob != null && lastJob.FinishedAt > DateTime.Now.AddSeconds(-10))
                    {
                        SftpStatus.IsVisible = true;
                        SftpIcon.Text = lastJob.State switch
                        {
                            TransferState.Completed => "\u2705",
                            TransferState.Canceled => "\u23F9",
                            _ => "\u274C"
                        };
                        SftpText.Text = BuildCompletedTransferStatus(lastJob);
                    }
                    else
                    {
                        SftpStatus.IsVisible = false;
                    }
                }
            });
        }

        private static string BuildRunningTransferStatus(TransferJob job, int activeTransferCount)
        {
            string action = job.Direction == TransferDirection.Upload ? "Uploading" : "Downloading";
            string detail = job.BytesTotal > 0
                ? $" {Math.Round(job.Progress * 100)}%"
                : string.Empty;
            string prefix = activeTransferCount > 1 ? $"{activeTransferCount} transfers \u2022 " : string.Empty;
            return $"{prefix}{action} {job.DisplayName}{detail}";
        }

        private static string BuildCompletedTransferStatus(TransferJob job)
        {
            return job.State switch
            {
                TransferState.Completed => $"{(job.Direction == TransferDirection.Upload ? "Uploaded" : "Downloaded")} {job.DisplayName}",
                TransferState.Canceled => $"{(job.Direction == TransferDirection.Upload ? "Upload" : "Download")} canceled",
                TransferState.Failed when !string.IsNullOrWhiteSpace(job.LastError) => $"Transfer failed: {job.LastError}",
                TransferState.Failed => "Transfer failed",
                _ => "Transfer updated"
            };
        }

        /// <summary>
        /// Single owner of StatusBar visibility. Two independent features want
        /// the bar (SSH port forwards, agent access), so neither writes
        /// IsVisible directly. Only persistent conditions appear here: the bar
        /// appearing or disappearing resizes the terminal, so agent *activity*
        /// must never reach this.
        ///
        /// It owns the agent segment's own visibility too — both the clickable
        /// wrapper and the panel inside it, so an invisible segment leaves no
        /// button padding behind in the bar.
        /// </summary>
        internal void UpdateStatusBarVisibility()
        {
            bool sshForwards = Profile != null && Profile.Forwards.Count > 0;
            StatusBar.IsVisible = (sshForwards || _agentActable) && !FocusChromeHidden;
            AgentStatusButton.IsVisible = _agentActable;
            AgentStatusSegment.IsVisible = _agentActable;
        }

        /// <summary>
        /// Renders the pane's agent attention tier. Called on the UI thread from
        /// the registration's <c>AttentionMachine.Changed</c> event (a tier
        /// transition) and from its <c>ActabilityChanged</c> event (act-reachability
        /// republished with no tier transition — the global act toggle, or an
        /// SSH allowlist edit, on an otherwise-idle pane).
        /// </summary>
        internal void ApplyAgentAttention(Ntilde.AgentHost.AgentAttentionSnapshot snapshot, bool isActable)
        {
            _agentActable = isActable;
            UpdateStatusBarVisibility();

            if (!_agentActable) return;

            switch (snapshot.Tier)
            {
                case Ntilde.AgentHost.AgentAttentionTier.Wrote:
                    BindToken(AgentStatusDot, Avalonia.Controls.Shapes.Shape.FillProperty, ThemeKeys.AgentWrote);
                    AgentStatusText.Text = "agent typed";
                    BindToken(AgentStatusText, TextBlock.ForegroundProperty, ThemeKeys.AgentWrote);
                    break;
                case Ntilde.AgentHost.AgentAttentionTier.Watched:
                    BindToken(AgentStatusDot, Avalonia.Controls.Shapes.Shape.FillProperty, ThemeKeys.AgentWatched);
                    AgentStatusText.Text = "agent reading";
                    BindToken(AgentStatusText, TextBlock.ForegroundProperty, ThemeKeys.AgentWatched);
                    break;
                default:
                    BindToken(AgentStatusDot, Avalonia.Controls.Shapes.Shape.FillProperty, ThemeKeys.AgentIdle);
                    AgentStatusText.Text = "agent access";
                    BindToken(AgentStatusText, TextBlock.ForegroundProperty, ThemeKeys.Fg3);
                    break;
            }

            ToolTip.SetTip(AgentStatusButton, BuildAgentSegmentTooltip(snapshot));
        }

        /// <summary>
        /// The segment's hover text. The two-word label in the bar cannot say
        /// *when* an agent typed, and the write tier is sticky for at least ten
        /// seconds, so "agent typed" alone leaves the user unable to tell a
        /// write from a moment ago from one they already saw. Names the method
        /// too, since sendInput and closeSession are very different events.
        /// </summary>
        private static string BuildAgentSegmentTooltip(Ntilde.AgentHost.AgentAttentionSnapshot snapshot)
        {
            const string Suffix = " Click to open the agent activity journal.";
            switch (snapshot.Tier)
            {
                case Ntilde.AgentHost.AgentAttentionTier.Wrote:
                    string when = snapshot.LastWriteUtc.HasValue
                        ? snapshot.LastWriteUtc.Value.ToLocalTime().ToString(
                            "HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
                        : "just now";
                    string method = string.IsNullOrEmpty(snapshot.LastWriteMethod)
                        ? "an agent"
                        : snapshot.LastWriteMethod;
                    return $"An agent typed into this pane at {when} ({method})." + Suffix;
                case Ntilde.AgentHost.AgentAttentionTier.Watched:
                    return "An agent is reading this pane." + Suffix;
                default:
                    return "An agent can read this pane and type into it." + Suffix;
            }
        }

        // Raised on the endpoint's IPC or timer thread; Avalonia controls are
        // UI-thread only, so hop before touching the status bar. The handler
        // body stays a bare Dispatcher.UIThread.Post with no logic in it:
        // AgentAttentionMachine.DrainPendingEvents rethrows subscriber
        // exceptions on the calling thread, and that thread is serving a live
        // agent-host request. Guard the Post itself too — during teardown the
        // dispatcher can be gone while the endpoint is still serving requests.
        private void OnAgentAttentionChanged(Ntilde.AgentHost.AgentAttentionSnapshot snapshot)
        {
            var registration = _agentRegistration;
            if (registration == null) return;
            try
            {
                this.Dispatcher.Post(() => ApplyAgentAttention(snapshot, registration.IsAgentActable));
            }
            catch (Exception)
            {
                // Dispatcher unavailable during app teardown; nothing to render to.
                // Never let this escape: it runs on the endpoint's IPC/timer
                // thread, and AgentAttentionMachine.Changed rethrows subscriber
                // exceptions on that same thread.
            }
        }

        // Raised on AgentSessionRegistration.IsAgentActable's setter thread —
        // AgentHostService.RefreshActability runs it from the 1 s sweep and
        // from the act-toggle setter, neither of which is the UI thread — so
        // hop the same way OnAgentAttentionChanged does. Kept to a bare guarded
        // Dispatcher.UIThread.Post with no logic in the body for the same
        // reason: a throw here must never propagate back into whatever caller
        // flipped actability (the endpoint's request handling, in the toggle
        // case). The registration only raises this on an actual value change,
        // so this cannot become a once-per-second post storm from the sweep.
        //
        // ActabilityChanged carries no payload on purpose, and this reads
        // IsAgentActable *inside* the posted lambda rather than capturing it.
        // RefreshActability can run concurrently (sweep thread, act-toggle
        // setter, allowlist edit) and two racing writers can compute different
        // values for the same SSH pane; the one that stores first may raise
        // last, so a captured bool can render the value that lost the race.
        // Because the setter raises only on an actual change, no later sweep
        // would ever correct that — the pane would stay unmarked forever while
        // agents could still type into it. Reading through the registration
        // makes the stale channel structurally impossible: whichever post runs
        // last renders whatever the registration currently holds.
        private void OnAgentActabilityChanged()
        {
            var registration = _agentRegistration;
            if (registration == null) return;
            try
            {
                this.Dispatcher.Post(() =>
                    ApplyAgentAttention(registration.AttentionMachine.Snapshot(), registration.IsAgentActable));
            }
            catch (Exception)
            {
                // Dispatcher unavailable during app teardown; nothing to render to.
            }
        }

        /// <summary>
        /// Test-only trigger for the SSH forwarding refresh that normally runs off
        /// <see cref="_statusTimer"/>'s 2-second tick. Tests construct a pane with
        /// forwards but never wait out a real timer, so without this hook the SSH
        /// half of the status bar (label, per-rule rows) never renders.
        /// </summary>
        internal void UpdateForwardingStatusForTesting() => UpdateForwardingStatus();

        /// <summary>
        /// Test-only access to the pane's agent-host registration, so tests can
        /// flip <see cref="Ntilde.AgentHost.AgentSessionRegistration.IsAgentActable"/>
        /// through the exact setter <c>AgentHostService.RefreshActability</c>
        /// uses, without a live endpoint or settings service.
        /// </summary>
        internal Ntilde.AgentHost.AgentSessionRegistration? AgentRegistrationForTesting => _agentRegistration;

        private void UpdateForwardingStatus()
        {
            if (Profile == null || Profile.Forwards.Count == 0)
            {
                ClearForwardingStatusUi();
                UpdateStatusBarVisibility();
                return;
            }

            bool anyChanges = false;
            foreach (var rule in Profile.Forwards)
            {
                var oldStatus = rule.Status;

                if (rule.Type == ForwardingType.Remote)
                {
                    // For now, assume remote is active if session is alive
                    rule.Status = (Session != null) ? ForwardingStatus.Active : ForwardingStatus.Stopped;
                }
                else
                {
                    bool isListening = CheckIfPortIsListening(rule);
                    if (isListening) rule.Status = ForwardingStatus.Active;
                    else if (Session != null) rule.Status = ForwardingStatus.Starting;
                    else rule.Status = ForwardingStatus.Stopped;
                }

                if (oldStatus != rule.Status) anyChanges = true;
            }

            // "Never rendered yet" used to be inferred from !StatusBar.IsVisible,
            // on the assumption that the forwards were the only thing that could
            // have raised the bar. They are not: the agent segment shares it, and
            // an allowlisted SSH pane can now be born actable (SetupCommon seeds
            // act-reachability so the bar never reflows the PTY a second later),
            // which would raise the bar before this ever ran and leave the
            // forwards half blank until some rule's status happened to change.
            // Track the render explicitly instead of inferring it.
            if (anyChanges || !_forwardingStatusUiBuilt)
            {
                UpdateStatusBarUI();
                (VisualRoot as MainWindow)?.UpdateTabVisuals();
            }
        }

        /// <summary>
        /// Drops whatever the forwarding half of the status bar last rendered.
        ///
        /// Before the agent segment existed, "no forwards" meant the bar itself
        /// was hidden, so nothing stale could be on screen and
        /// <see cref="UpdateForwardingStatus"/> could simply return. It cannot
        /// any more: the bar now also stays up for an agent-actable pane, so an
        /// SSH profile edited down to zero forwarding rules would keep
        /// advertising the rules it used to have, indefinitely.
        ///
        /// Deliberately does not touch <c>StatusBar.IsVisible</c> —
        /// <see cref="UpdateStatusBarVisibility"/> is its sole writer, because
        /// the bar appearing or disappearing reflows the PTY.
        ///
        /// The built flag is reset alongside the controls so that a profile
        /// which regains forwards renders them on the next pass instead of
        /// waiting for some rule's status to change. The early return keeps
        /// this off the hot path: the 2 s tick reaches it on every local pane,
        /// which never built anything to clear.
        /// </summary>
        private void ClearForwardingStatusUi()
        {
            if (!_forwardingStatusUiBuilt) return;

            _forwardingStatusUiBuilt = false;
            StatusBarLabel.Text = string.Empty;
            StatusBarRules.Children.Clear();
        }

        private void UpdateStatusBarUI()
        {
            if (Profile == null) return;
            UpdateStatusBarVisibility();
            _forwardingStatusUiBuilt = true;
            StatusBarLabel.Text = $"SSH \u25B8 {Profile.Name} \u25B8";
            StatusBarRules.Children.Clear();

            foreach (var rule in Profile.Forwards)
            {
                var container = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };

                var icon = new TextBlock
                {
                    Text = "\U0001F501",
                    FontSize = 10,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                };
                BindToken(icon, TextBlock.ForegroundProperty, rule.Status switch
                {
                    ForwardingStatus.Active => ThemeKeys.Success,
                    ForwardingStatus.Starting => ThemeKeys.Warning,
                    ForwardingStatus.Failed => ThemeKeys.Error,
                    _ => ThemeKeys.Fg4,
                });

                var txt = new TextBlock
                {
                    Text = rule.Type switch
                    {
                        ForwardingType.Local => $"L:{rule.LocalAddress}\u2192{rule.RemoteAddress}",
                        ForwardingType.Remote => $"R:{rule.RemoteAddress}\u2192{rule.LocalAddress}",
                        ForwardingType.Dynamic => $"D:{rule.LocalAddress}",
                        _ => ""
                    },
                    FontSize = 10,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                };
                BindToken(txt, TextBlock.ForegroundProperty, ThemeKeys.Fg);

                container.Children.Add(icon);
                container.Children.Add(txt);
                StatusBarRules.Children.Add(container);
            }
        }

        private static bool CheckIfPortIsListening(ForwardingRule rule)
        {
            try
            {
                string portStr = rule.LocalAddress;
                if (portStr.Contains(':')) portStr = portStr.Split(':').Last();
                if (!int.TryParse(portStr, out int port)) return false;

                var properties = IPGlobalProperties.GetIPGlobalProperties();
                var listeners = properties.GetActiveTcpListeners();
                return listeners.Any(l => l.Port == port);
            }
            catch { return false; }
        }
    }
}
