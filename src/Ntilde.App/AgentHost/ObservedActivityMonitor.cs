using System;
using System.Collections.Generic;
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
                _timer = new Timer(_ => { try { _ = TickAsync(); } catch { } }, null, TickInterval, TickInterval);
            }
            RaiseStateChanged();
            return true;
        }

        public void Stop()
        {
            bool changed;
            lock (_gate)
            {
                changed = _timer != null;
                _timer?.Dispose();
                _timer = null;
                _panes.Clear();
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
                lock (_gate)
                {
                    if (_disabledUnauthorized || now < _backoffUntil) return Task.CompletedTask;
                }
                if (!_classifier.HasCredentials) return Task.CompletedTask;

                var registrations = _registry.GetRegistrations();
                foreach (var registration in registrations)
                {
                    try
                    {
                        var send = TryObserve(registration, now);
                        if (send != null) sends.Add(send);
                    }
                    catch (Exception ex)
                    {
                        SafeLog($"[ScreenInference] pane={registration.PaneId} tick failed: {ex.GetType().Name}: {ex.Message}");
                    }
                }
                Prune(registrations);
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

        private Task? TryObserve(AgentSessionRegistration registration, DateTimeOffset now)
        {
            if (!IsEligible(registration)) return null;

            var snapshot = registration.StatusMachine.Snapshot();
            // Read the sequence BEFORE capturing: output that lands between the two makes the
            // answer look stale (dropped), never fresh for a screen it did not see.
            long sequence = snapshot.OutputSequence;

            PaneState state;
            lock (_gate)
            {
                if (!_panes.TryGetValue(registration, out state!))
                {
                    state = new PaneState();
                    _panes[registration] = state;
                }
                if (state.InFlight) return null;
                if (sequence == state.LastSequenceSent) return null;
                if (now - snapshot.LastOutputAt < QuietWindow) return null;
                if (now - state.LastRequestAt < MinInterval) return null;
            }

            var sample = _capture(registration);
            if (sample == null || string.IsNullOrWhiteSpace(sample.Text))
            {
                lock (_gate) { state.LastSequenceSent = sequence; }
                return null;
            }

            // Captured before the commit below so a failed send (anything but Answered) can put
            // the pane back exactly as it was: otherwise the commit permanently marks this
            // sequence/text as already sent, and a quiet pane that never outputs again would
            // never be re-judged after one transient failure.
            long previousSequence;
            string? previousText;
            lock (_gate)
            {
                if (string.Equals(sample.Text, state.LastTextSent, StringComparison.Ordinal))
                {
                    state.LastSequenceSent = sequence;
                    state.LastRequestAt = now;
                    return null;
                }
                previousSequence = state.LastSequenceSent;
                previousText = state.LastTextSent;
                state.InFlight = true;
                state.LastRequestAt = now;
                state.LastSequenceSent = sequence;
                state.LastTextSent = sample.Text;
            }

            var redacted = _secretsFilter.Redact(sample.Text).RedactedText;
            Interlocked.Increment(ref _requestCount);
            RaiseStateChanged();
            return SendAsync(registration, state, sample with { Text = redacted }, sequence, now, previousSequence, previousText);
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
            string? previousText)
        {
            ScreenClassificationResult result;
            try
            {
                result = await _classifier.ClassifyAsync(sample, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result = new ScreenClassificationResult(ScreenClassificationOutcome.TransportFailure, null, $"{ex.GetType().Name}: {ex.Message}");
            }

            lock (_gate)
            {
                state.InFlight = false;
                if (result.Outcome != ScreenClassificationOutcome.Answered)
                {
                    // The round trip did not produce a usable answer: undo the commit TryObserve
                    // made before the request, so this pane is due again once MinInterval passes
                    // rather than being silently skipped forever (LastRequestAt is left alone —
                    // it still spaces out the retry).
                    state.LastSequenceSent = previousSequence;
                    state.LastTextSent = previousText;
                }
            }

            var latency = _now() - startedAt;
            string note = string.Empty;
            switch (result.Outcome)
            {
                case ScreenClassificationOutcome.Answered:
                    var a = result.Answer!;
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
                var dead = new List<AgentSessionRegistration>();
                foreach (var key in _panes.Keys)
                {
                    if (!liveSet.Contains(key)) dead.Add(key);
                }
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
