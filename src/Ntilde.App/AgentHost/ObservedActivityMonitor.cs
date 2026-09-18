using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ntilde.CommandAssist.Domain;
using Ntilde.Inference;

namespace Ntilde.AgentHost
{
    /// <summary>
    /// Feeds <see cref="AgentSessionStatusMachine.NotifyObserved"/> from one screen judgment per
    /// quiet pane (docs/superpowers/specs/2026-09-17-screen-inference-observed-status-design.md §2.2).
    /// Own 1 s timer, independent of the agent-host IPC endpoint, so the tab strip benefits with
    /// agent access off. Every dependency is injected; tests drive <see cref="TickAsync"/> directly.
    /// </summary>
    public sealed class ObservedActivityMonitor : IDisposable
    {
        public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
        /// <summary>A pane must be silent this long after output before its screen is judged.</summary>
        public static readonly TimeSpan QuietWindow = TimeSpan.FromSeconds(2);
        /// <summary>Per-pane floor between requests; bounds cost on a pane that streams continuously.</summary>
        public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
        public const string LocalKind = "local";

        private sealed class PaneState
        {
            public DateTimeOffset LastRequestAt = DateTimeOffset.MinValue;
            public long LastSequenceSent = -1;
            public string? LastTextSent;
            public bool InFlight;
        }

        private readonly AgentSessionRegistry _registry;
        private readonly IScreenActivityClassifier _classifier;
        private readonly ISecretsFilter _secretsFilter;
        private readonly Func<AgentSessionRegistration, ScreenSample?> _capture;
        private readonly Func<DateTimeOffset> _now;
        private readonly Action<string> _log;

        private readonly object _gate = new();
        private readonly Dictionary<AgentSessionRegistration, PaneState> _panes = new(ReferenceEqualityComparer.Instance);
        private volatile Func<Guid, bool>? _sshAllowlist;
        private Timer? _timer;
        /// <summary>Cancels every request in flight when the monitor stops; a screen must not leave the process after the user turned the feature off.</summary>
        private CancellationTokenSource? _lifetime;
        /// <summary>
        /// Set by <see cref="Stop"/>, cleared by <see cref="Start"/>. A timer callback already queued
        /// when Stop() ran can still enter <see cref="TryObserve"/> (Timer.Dispose does not wait for
        /// it), and a monitor that has been stopped must abort the observation rather than send
        /// with an uncancellable token. A monitor that was never started (tests drive TickAsync
        /// directly) is not stopped and proceeds.
        /// </summary>
        private bool _stopped;
        /// <summary>
        /// Bumped by <see cref="Start"/> and <see cref="Stop"/>. A send captures the generation it
        /// was issued under and, after its answer arrives, publishes only if the generation is
        /// unchanged (checked under the gate). The token alone leaves a window: an answer that
        /// arrived just before Stop() would pass the token check and then publish an observation
        /// into a stopped monitor, or disable/back off a monitor that has since restarted.
        /// </summary>
        private int _generation;
        private int _tickRunning;
        private int _requestCount;
        private bool _disabledUnauthorized;
        private TimeSpan _currentBackoff = TimeSpan.Zero;
        private DateTimeOffset _backoffUntil = DateTimeOffset.MinValue;

        public ObservedActivityMonitor(
            AgentSessionRegistry registry,
            IScreenActivityClassifier classifier,
            ISecretsFilter secretsFilter,
            Func<AgentSessionRegistration, ScreenSample?> capture,
            Func<DateTimeOffset>? nowProvider = null,
            Action<string>? log = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
            _secretsFilter = secretsFilter ?? throw new ArgumentNullException(nameof(secretsFilter));
            _capture = capture ?? throw new ArgumentNullException(nameof(capture));
            _now = nowProvider ?? (() => DateTimeOffset.UtcNow);
            _log = log ?? (_ => { });
        }

        /// <summary>Raised (any thread) when running/disabled state or the request count changes.</summary>
        public event Action? StateChanged;

        public bool IsRunning { get { lock (_gate) { return _timer != null; } } }
        public int RequestCount => Volatile.Read(ref _requestCount);
        public bool IsDisabledUnauthorized { get { lock (_gate) { return _disabledUnauthorized; } } }
        internal TimeSpan CurrentBackoff { get { lock (_gate) { return _currentBackoff; } } }
        internal DateTimeOffset BackoffUntil { get { lock (_gate) { return _backoffUntil; } } }
        internal int TrackedPaneCount { get { lock (_gate) { return _panes.Count; } } }

        /// <summary>Per-profile SSH opt-in probe (fail closed when null). UI thread publishes it.</summary>
        public void SetSshProfileAllowlist(Func<Guid, bool>? probe) => _sshAllowlist = probe;

        /// <summary>
        /// Starts or stops the timer to match the setting. Safe to call repeatedly. Critically,
        /// enabling always clears an unauthorized disable and any pending backoff even when the
        /// timer is already running: a re-saved API key must re-enable inference without the
        /// caller having to Stop() first, and <see cref="Start"/> alone cannot do that because it
        /// returns before touching state when the timer already exists.
        /// </summary>
        public void Apply(bool enabled)
        {
            if (!enabled)
            {
                Stop();
                return;
            }

            bool wasDisabled;
            lock (_gate)
            {
                wasDisabled = _disabledUnauthorized;
                _disabledUnauthorized = false;
                _currentBackoff = TimeSpan.Zero;
                _backoffUntil = DateTimeOffset.MinValue;
            }
            bool started = Start();
            // Start() already raised StateChanged when it actually created the timer; only raise
            // here for the case Start() was a no-op (already running) but the flag still changed.
            if (wasDisabled && !started)
            {
                RaiseStateChanged();
            }
        }

        /// <summary>Returns true when this call actually created the timer (false when already running).</summary>
        private bool Start()
        {
            lock (_gate)
            {
                if (_timer != null) return false;
                _disabledUnauthorized = false;
                _currentBackoff = TimeSpan.Zero;
                _backoffUntil = DateTimeOffset.MinValue;
                _lifetime = new CancellationTokenSource();
                _stopped = false;
                _generation++;
                _timer = new Timer(OnTimerTick, null, TickInterval, TickInterval);
            }
            RaiseStateChanged();
            return true;
        }

        private void OnTimerTick(object? state)
        {
            try
            {
                // TickAsync never throws synchronously (its body is guarded), but a faulted
                // send task must still be observed so the pool never sees an unobserved exception.
                TickAsync().ContinueWith(
                    t => SafeLog($"[ScreenInference] send faulted: {t.Exception?.GetBaseException().GetType().Name}: {t.Exception?.GetBaseException().Message}"),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                SafeLog($"[ScreenInference] timer tick failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        public void Stop()
        {
            bool changed;
            CancellationTokenSource? lifetime;
            lock (_gate)
            {
                changed = _timer != null;
                _timer?.Dispose();
                _timer = null;
                lifetime = _lifetime;
                _lifetime = null;
                _stopped = true;
                _generation++;
                _panes.Clear();
            }
            // Cancel outside the gate: continuations may run synchronously on Cancel() and
            // must be free to take the gate themselves.
            if (lifetime != null)
            {
                try { lifetime.Cancel(); } catch (ObjectDisposedException) { /* already gone */ }
                lifetime.Dispose();
            }
            if (changed) RaiseStateChanged();
        }

        public void Dispose() => Stop();

        /// <summary>Logs without ever throwing back into a caller on the send/timer path.</summary>
        private void SafeLog(string line)
        {
            try { _log(line); } catch { /* a throwing logger must not fault the caller */ }
        }

        /// <summary>Raises <see cref="StateChanged"/> without a throwing subscriber faulting the caller.</summary>
        private void RaiseStateChanged()
        {
            try { StateChanged?.Invoke(); } catch { /* a throwing subscriber must not fault the caller */ }
        }

        /// <summary>
        /// One sweep. Returns a task that completes when every request this sweep started has
        /// completed; the timer discards it, tests await it. Re-entrant calls while a sweep is
        /// walking the registry return immediately (requests in flight do not block sweeps).
        /// </summary>
        internal Task TickAsync()
        {
            if (Interlocked.Exchange(ref _tickRunning, 1) == 1) return Task.CompletedTask;
            try
            {
                var sends = new List<Task>();
                var now = _now();
                // Prune first, before any early return: a disabled or backed-off monitor must
                // still forget panes that were closed, or their registrations stay referenced
                // for the length of the backoff.
                var registrations = _registry.GetRegistrations();
                Prune(registrations);
                lock (_gate)
                {
                    if (_disabledUnauthorized || now < _backoffUntil) return Task.CompletedTask;
                }
                if (!_classifier.HasCredentials) return Task.CompletedTask;

                foreach (var registration in registrations)
                {
                    try
                    {
                        sends.Add(TryObserve(registration, now));
                    }
                    catch (Exception ex)
                    {
                        SafeLog($"[ScreenInference] pane={registration.PaneId} tick failed: {ex.GetType().Name}: {ex.Message}");
                    }
                }
                return Task.WhenAll(sends);
            }
            catch (Exception ex)
            {
                // _now(), _classifier.HasCredentials, and _registry.GetRegistrations() run outside
                // any inner try; a throw here would otherwise be an unhandled exception on the
                // Timer's pool thread, which kills the process.
                SafeLog($"[ScreenInference] sweep failed: {ex.GetType().Name}: {ex.Message}");
                return Task.CompletedTask;
            }
            finally
            {
                Volatile.Write(ref _tickRunning, 0);
            }
        }

        private Task TryObserve(AgentSessionRegistration registration, DateTimeOffset now)
        {
            if (!IsEligible(registration)) return Task.CompletedTask;

            var snapshot = registration.StatusMachine.Snapshot();
            // Read the sequence BEFORE capturing: output that lands between the two makes the
            // answer look stale (dropped), never fresh for a screen it did not see.
            long sequence = snapshot.OutputSequence;

            PaneState state;
            lock (_gate)
            {
                if (!_panes.TryGetValue(registration, out var existing))
                {
                    existing = new PaneState();
                    _panes[registration] = existing;
                }
                state = existing;
                if (state.InFlight) return Task.CompletedTask;
                if (sequence == state.LastSequenceSent) return Task.CompletedTask;
                if (now - snapshot.LastOutputAt < QuietWindow) return Task.CompletedTask;
                if (now - state.LastRequestAt < MinInterval) return Task.CompletedTask;
            }

            var sample = _capture(registration);
            if (sample == null || string.IsNullOrWhiteSpace(sample.Text))
            {
                lock (_gate) { state.LastSequenceSent = sequence; }
                return Task.CompletedTask;
            }

            // Captured before the commit below so a failed send (anything but Answered) can put
            // the pane back exactly as it was: otherwise the commit permanently marks this
            // sequence/text as already sent, and a quiet pane that never outputs again would
            // never be re-judged after one transient failure.
            long previousSequence;
            string? previousText;
            CancellationToken token;
            int generation;
            lock (_gate)
            {
                // Stop() may have run since the due checks above (a queued timer callback, or
                // between the capture and this commit). Nothing has been committed yet, and the
                // captured text must not go anywhere: abort instead of sending with a token that
                // no longer exists.
                if (_stopped) return Task.CompletedTask;
                if (string.Equals(sample.Text, state.LastTextSent, StringComparison.Ordinal))
                {
                    state.LastSequenceSent = sequence;
                    state.LastRequestAt = now;
                    return Task.CompletedTask;
                }
                previousSequence = state.LastSequenceSent;
                previousText = state.LastTextSent;
                state.InFlight = true;
                state.LastRequestAt = now;
                state.LastSequenceSent = sequence;
                state.LastTextSent = sample.Text;
                token = _lifetime?.Token ?? CancellationToken.None;
                generation = _generation;
            }

            var redacted = _secretsFilter.Redact(sample.Text).RedactedText;
            Interlocked.Increment(ref _requestCount);
            RaiseStateChanged();
            return SendAsync(registration, state, sample with { Text = redacted }, sequence, now, previousSequence, previousText, token, generation);
        }

        private bool IsEligible(AgentSessionRegistration registration)
        {
            if (string.Equals(registration.Kind, LocalKind, StringComparison.Ordinal)) return true;
            var probe = _sshAllowlist;
            if (probe == null) return false; // fail closed
            if (registration.ProfileId is not { } profileId) return false;
            try { return probe(profileId); } catch { return false; }
        }

        private async Task SendAsync(
            AgentSessionRegistration registration,
            PaneState state,
            ScreenSample sample,
            long sequence,
            DateTimeOffset startedAt,
            long previousSequence,
            string? previousText,
            CancellationToken token,
            int generation)
        {
            ScreenClassificationResult result;
            try
            {
                result = await _classifier.ClassifyAsync(sample, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result = new ScreenClassificationResult(ScreenClassificationOutcome.TransportFailure, null, $"{ex.GetType().Name}: {ex.Message}");
            }

            bool current;
            lock (_gate)
            {
                state.InFlight = false;
                // Same lifetime generation as when the request was issued, and not cancelled: only
                // then may this answer touch the monitor or the status machine. Stop() bumps the
                // generation under the same gate, so an answer that raced Stop() (arrived just
                // before it, checked just after) is fenced out here, and a restarted monitor is
                // never disabled or backed off by its predecessor's answer.
                current = generation == _generation && !token.IsCancellationRequested;
                if (current
                    && result.Outcome != ScreenClassificationOutcome.Answered
                    && result.Outcome != ScreenClassificationOutcome.Rejected)
                {
                    // Rejected is a deterministic 422 on this exact request body: identical content
                    // can never succeed on a retry, so the commit stays and this screen is never
                    // resent. Every other non-Answered outcome is transient, so undo the commit
                    // TryObserve made before the request, so this pane is due again once MinInterval
                    // passes rather than being silently skipped forever (LastRequestAt is left alone —
                    // it still spaces out the retry).
                    state.LastSequenceSent = previousSequence;
                    state.LastTextSent = previousText;
                }
            }

            if (!current)
            {
                // The monitor was stopped (or stopped and restarted) while this request was out.
                // Whatever came back is not applied: the user turned the feature off. Nothing is
                // restored or backed off either; Stop() already cleared the pane bookkeeping.
                SafeLog($"[ScreenInference] pane={registration.PaneId} bytes={sample.Text.Length} outcome=Cancelled (monitor stopped)");
                return;
            }

            var latency = _now() - startedAt;
            string note = string.Empty;
            switch (result.Outcome)
            {
                case ScreenClassificationOutcome.Answered when result.Answer is { } a:
                    // A throwing EventEmitted subscriber on the status machine must not propagate
                    // out of the send path: treat it the same as a stale/dropped answer and record
                    // the failure in the log note instead.
                    try
                    {
                        var accepted = registration.StatusMachine.NotifyObserved(new ScreenObservation
                        {
                            Activity = a.Activity,
                            Confidence = a.Confidence,
                            NeedsAttention = a.NeedsAttention,
                            LastCommandFailed = a.LastCommandFailed,
                            ObservedAt = startedAt,
                            OutputSequence = sequence,
                        });
                        note = accepted
                            ? $"activity={a.Activity} conf={a.Confidence:0.00} tokens={a.InputTokens}"
                            : "dropped: stale (output arrived during the request)";
                    }
                    catch (Exception ex)
                    {
                        note = $"{ex.GetType().Name}: {ex.Message}";
                    }
                    // A stale drop is still a successful round trip with the service: the request
                    // itself succeeded, so the failure backoff has nothing to do with it.
                    ResetBackoff();
                    break;
                case ScreenClassificationOutcome.Unauthorized:
                    DisableUnauthorized();
                    note = "API key rejected; screen inference disabled until a key is saved again";
                    break;
                case ScreenClassificationOutcome.RateLimited:
                case ScreenClassificationOutcome.Overloaded:
                    note = $"backing off {ApplyBackoff().TotalSeconds:0}s";
                    break;
                case ScreenClassificationOutcome.Rejected:
                    // Deterministic 422: never retried (see the lock above), so it must not follow
                    // the 5s -> 5min retry ladder either - a differently-worded screen from the
                    // same pane next tick is unaffected.
                    note = $"rejected, not retried: {SanitizeDetail(result.Detail)}";
                    break;
                case ScreenClassificationOutcome.TransportFailure:
                case ScreenClassificationOutcome.Malformed:
                    // Transient failures: keep the retry (restored above) on the same 5s -> 5min
                    // backoff ladder as rate-limit/overload so a flaky transport does not hammer
                    // the service every tick.
                    note = $"{SanitizeDetail(result.Detail)}; backing off {ApplyBackoff().TotalSeconds:0}s";
                    break;
                default:
                    note = SanitizeDetail(result.Detail);
                    break;
            }
            SafeLog($"[ScreenInference] pane={registration.PaneId} bytes={sample.Text.Length} latency={latency.TotalMilliseconds:0}ms outcome={result.Outcome} {note}");
        }

        /// <summary>
        /// Flattens newlines and caps length so a classifier/transport error message can never
        /// carry enough of an echoed request body to leak screen text into the log.
        /// </summary>
        private static string SanitizeDetail(string? detail)
        {
            if (string.IsNullOrEmpty(detail)) return string.Empty;
            var flattened = detail.Replace('\r', ' ').Replace('\n', ' ');
            return flattened.Length > 120 ? flattened[..120] : flattened;
        }

        private void Prune(AgentSessionRegistration[] live)
        {
            lock (_gate)
            {
                if (_panes.Count == 0) return;
                var liveSet = new HashSet<AgentSessionRegistration>(live, ReferenceEqualityComparer.Instance);
                var dead = _panes.Keys.Where(k => !liveSet.Contains(k)).ToList();
                foreach (var key in dead) _panes.Remove(key);
            }
        }

        private TimeSpan ApplyBackoff()
        {
            lock (_gate)
            {
                _currentBackoff = _currentBackoff == TimeSpan.Zero
                    ? InitialBackoff
                    : TimeSpan.FromTicks(Math.Min(_currentBackoff.Ticks * 2, MaxBackoff.Ticks));
                _backoffUntil = _now() + _currentBackoff;
                return _currentBackoff;
            }
        }

        private void ResetBackoff()
        {
            lock (_gate)
            {
                _currentBackoff = TimeSpan.Zero;
                _backoffUntil = DateTimeOffset.MinValue;
            }
        }

        private void DisableUnauthorized()
        {
            lock (_gate) { _disabledUnauthorized = true; }
            RaiseStateChanged();
        }
    }
}
