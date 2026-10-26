using Ntilde.Shell;
using Ntilde.VT;
using Ntilde.CommandAssist.ShellIntegration.Contracts;
using Ntilde.CommandAssist.ShellIntegration.PowerShell;
using Ntilde.CommandAssist.ShellIntegration.Runtime;

namespace Ntilde.Controls
{
    /// <summary>
    /// Shell integration: the launch plan that injects it, the trackers that read its marks, and the events
    /// they raise.
    /// </summary>
    public partial class TerminalPane
    {
        /// <summary>
        /// Records that this session emitted an OSC 133 mark, and - the first time only - republishes
        /// the session context so Command Assist learns the session is instrumented.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Called from the four parser mark callbacks, i.e. on the PTY read thread. The context
        /// update itself has to happen on the UI thread (it reads <see cref="Session"/> and
        /// <c>CurrentWorkingDirectory</c> and pokes the controller), so it is posted rather than
        /// called.
        /// </para>
        /// <para>
        /// The posted update is not the only thing that closes the loop, and must not be: it lands
        /// asynchronously, so a shell whose <c>A</c>, <c>B</c> and <c>C</c> all arrive in one parse
        /// chunk could reach the capture pipeline before it. <c>AssistSessionContext</c> makes the
        /// same deduction independently from the event stream
        /// (<c>AssistSessionContext.IsShellIntegrationLive</c>). What the post is <em>necessary</em>
        /// for is durability: <c>UpdateSession</c> forgets observed markers whenever it is told
        /// integration is off, so without feeding the observation back, the next ordinary directory
        /// change would demote an instrumented remote back to markless.
        /// </para>
        /// </remarks>
        /// <summary>
        /// Whether Ntilde participates in the OSC 133 contract at all for this pane: the same switch
        /// <see cref="ApplyShellIntegrationLaunchPlan"/> and
        /// <see cref="ArmRemoteShellIntegrationTracker"/> honour, read the same way (a pane with no
        /// settings object yet is treated as enabled, which is what the arming paths do).
        /// </summary>
        /// <remarks>
        /// Consulted by the three consumption paths that hang off the raw parser callbacks rather
        /// than off the tracker - the integrated-session latch, the <c>133;C</c> payload, and the
        /// command-input window the grid read is gated on - because those callbacks are wired
        /// unconditionally and would otherwise keep consuming remote marks with the setting off. The callbacks themselves stay wired: they also feed the agent status
        /// machine and the overlay anchor, neither of which this switch governs.
        /// </remarks>
        private bool IsShellIntegrationConsumptionEnabled =>
            _settings?.CommandAssistShellIntegrationEnabled ?? true;

        private void NoteShellIntegrationMarkObserved()
        {
            if (_hasObservedShellIntegrationMark || !IsShellIntegrationConsumptionEnabled)
            {
                return;
            }

            _hasObservedShellIntegrationMark = true;
            this.Dispatcher.Post(UpdateCommandAssistContext);
        }

        private void ApplyShellIntegrationLaunchPlan(
            TerminalProfile? profile,
            ref string effectiveShell,
            ref string args,
            string startingDirectory)
        {
            if (_settings == null || !_settings.CommandAssistShellIntegrationEnabled)
            {
                return;
            }

            string shellKind = DetermineShellKind(effectiveShell);
            ShellIntegrationRegistry registry = RequireCommandAssistServices().ShellIntegrationRegistry;
            IShellIntegrationProvider? provider = registry.GetProvider(shellKind, profile?.Command);
            if (provider == null)
            {
                return;
            }

            if (!_settings.CommandAssistPowerShellIntegrationEnabled &&
                provider is PowerShellShellIntegrationProvider)
            {
                return;
            }

            ShellIntegrationLaunchPlan plan;
            try
            {
                plan = provider.CreateLaunchPlan(effectiveShell, args, startingDirectory);
            }
            catch
            {
                return;
            }

            if (!plan.IsIntegrated)
            {
                return;
            }

            effectiveShell = plan.ShellCommand;
            args = plan.ShellArguments ?? string.Empty;
            _isShellIntegrationActive = true;
            _shellIntegrationEnvOverrides = plan.EnvironmentOverrides;
            ArmShellIntegrationTracker();
        }

        /// <summary>
        /// Arms the translator that turns raw OSC 133 parser callbacks into the ordered
        /// <see cref="ShellIntegrationEvent"/> stream Command Assist consumes.
        /// </summary>
        /// <remarks>
        /// Deliberately separate from <c>_isShellIntegrationActive</c>, which the injection caller
        /// sets and which means something narrower: that <em>we</em> injected a bootstrap into this
        /// shell. Arming the translator only says the events will be delivered if they arrive - it
        /// is inert on a shell that emits no marks, since every tracker entry point is reached only
        /// from a parser mark callback. V2 Phase 2b is where a session we did not instrument arms it
        /// too; see <see cref="ArmRemoteShellIntegrationTracker"/>. Internal so a headless test can
        /// drive the real parser to tracker to dispatcher to controller path without spawning a
        /// shell.
        /// </remarks>
        internal void ArmShellIntegrationTracker()
        {
            _shellLifecycleTracker = new ShellLifecycleTracker();
            _shellLifecycleTracker.EventObserved += OnShellIntegrationEventObserved;
        }

        /// <summary>
        /// Arms the OSC 133 translator for an SSH session, so that a remote shell the user has
        /// instrumented themselves (see <c>docs/command-assist/RemoteShellIntegration.md</c>) gets
        /// the same Command Assist treatment a local integrated shell does.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why unconditionally, before any mark has been seen.</b> The alternative - arm lazily on
        /// the first observed mark - loses that mark. <c>133;A</c> and the first <c>133;B</c> arrive
        /// with the very first remote prompt, and the <c>B</c> is what opens the command-input window
        /// the grid reader is gated on; a tracker armed after it would leave the first command line
        /// unreadable for no gain. Arming costs one object and one event subscription on a session
        /// that may never emit a mark, and produces no events until one does.
        /// </para>
        /// <para>
        /// <b>Why it cannot regress markless SSH.</b> Every path into
        /// <see cref="ShellLifecycleTracker"/> is a parser mark callback. A remote host with no
        /// snippet installed emits no OSC 133, so no event is ever dispatched, no
        /// <c>HasObservedShellIntegrationMarker</c> is set, and the heuristic Enter-time capture and
        /// the conservative markless anchoring stack behave exactly as they did before. The
        /// agent-status machinery is not affected either way: it hangs off the parser callbacks
        /// directly, never off the tracker.
        /// </para>
        /// <para>
        /// <b>Gated on the shell-integration setting</b>, which is the same switch that decides
        /// whether we inject locally. It is the user's "do not participate in the OSC 133 contract"
        /// control, and a remote host is exactly where they cannot simply uninstall the emitter.
        /// (Mark-based overlay <em>anchoring</em> is deliberately not gated on it - that path reads
        /// the parser's mark directly and predates this switch's remote meaning.)
        /// </para>
        /// <param name="profile">
        /// The profile being connected. Passed explicitly because
        /// <see cref="InitializeSessionCore"/> runs before <see cref="Profile"/> has necessarily
        /// caught up with the session being built; falls back to <see cref="Profile"/> for callers
        /// (and tests) that have already published it.
        /// </param>
        /// </remarks>
        internal void ArmRemoteShellIntegrationTracker(TerminalProfile? profile = null)
        {
            profile ??= Profile;
            if (profile?.Type != ConnectionType.SSH)
            {
                return;
            }

            if (_settings != null && !_settings.CommandAssistShellIntegrationEnabled)
            {
                return;
            }

            ArmShellIntegrationTracker();
        }

        private void OnShellIntegrationEventObserved(ShellIntegrationEvent shellEvent)
        {
            if (shellEvent.Type == ShellIntegrationEventType.CommandAccepted &&
                !string.IsNullOrWhiteSpace(shellEvent.CommandText))
            {
                _lastRelevantCommandText = shellEvent.CommandText.Trim();
            }

            // OSC 133;B (CommandStarted) used to be dropped here as a no-op: nothing consumed it,
            // and B fires once per prompt AND once per prompt repaint, so forwarding it only
            // queued dead work onto the serialized dispatcher ahead of events that did something.
            //
            // Phase 1c gave it a job. B is what opens Command Assist's command-input window
            // (AssistSessionContext.IsAcceptingCommandInput), and that window is the lifecycle gate
            // on reading the command line out of the grid: with the event dropped, the gate would
            // never open and grid-truth query state would be dead on arrival. The early-out is
            // therefore gone, and the repaint cost -- one dispatcher hop per prompt paint, doing an
            // idempotent bool set and a marker observation -- is the price of the gate.
            //
            // Faults are logged rather than dropped. This dispatch is fire-and-forget, so an exception
            // anywhere in the handler used to vanish into an unobserved Task - which is how a
            // cross-thread Avalonia write inside Command Assist initialization could break the assist
            // overlay's content with no error anywhere (the post-3a blank-overlay regression, fixed in
            // BindCommandAssistViews). One log line is the difference between that and a mystery.
            _ = _shellIntegrationEventDispatcher
                .EnqueueAsync(() => HandleShellIntegrationEventAsync(shellEvent))
                .ContinueWith(
                    static task => TerminalLogger.Log(
                        LogLevel.Error,
                        "Shell integration event dispatch failed: " + task.Exception),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
        }

        private async Task HandleShellIntegrationEventAsync(ShellIntegrationEvent shellEvent)
        {
            if (!EnsureCommandAssistInitialized())
            {
                return;
            }

            try
            {
                await _commandAssistController.HandleShellIntegrationEventAsync(shellEvent);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TerminalPane] Shell integration event handling failed: {ex.Message}");
            }
        }
    }
}
