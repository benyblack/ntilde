using System;
using System.Collections.Generic;
using Ntilde.Inference;

namespace Ntilde.AgentHost
{
    /// <summary>
    /// Per-session status state machine for the agent-host A2 milestone
    /// (docs/plans/2026-07-07-agent-host-a2-status-design.md).
    ///
    /// Signals arrive from the pane on the UI thread (PTY lifecycle, shell
    /// integration, alt-screen switches); the periodic <see cref="Sweep"/>
    /// contributes time-based transitions (idle, stall) and the PTY
    /// child-process heuristic. All state is guarded by one lock;
    /// <see cref="Snapshot"/> is safe from any thread. Events are collected
    /// under the lock but raised outside it.
    ///
    /// The clock is injectable (same pattern as ShellLifecycleTracker) so
    /// every threshold is deterministic in tests: stall = no output for
    /// <see cref="StallThresholdSeconds"/> while running; idle = at a prompt
    /// with no output for <see cref="IdleThresholdSeconds"/>.
    /// </summary>
    public sealed class AgentSessionStatusMachine
    {
        public const int StallThresholdSeconds = 30;
        public const int IdleThresholdSeconds = 60;

        /// <summary>Minimum Choice confidence for a screen observation to decide the kind.</summary>
        public const double ObservedOverrideThreshold = 0.85;

        private readonly object _gate = new();
        private readonly Func<DateTimeOffset> _now;

        // Signal state
        private bool _precise;            // shell integration observed
        private bool _commandInFlight;    // precise tier: between started/finished
        private bool _promptSeen;         // precise tier: a prompt has appeared
        private bool _altScreenActive;
        private bool _hasActiveChildren;  // heuristic tier: from last sweep
        private bool _exited;
        private int? _exitCode;
        private string? _currentCommand;
        private DateTimeOffset? _commandStartedAt;
        private long _outputSequence;
        private ScreenObservation? _observation;

        // Derived state
        private AgentSessionStatusKind _kind;
        private AgentSessionStatusConfidence _confidence = AgentSessionStatusConfidence.Heuristic;
        private DateTimeOffset _statusSince;
        private DateTimeOffset _lastOutputAt;
        private bool _stalled;

        /// <summary>Raised outside the lock, in emission order.</summary>
        public event Action<AgentSessionStatusEvent>? EventEmitted;

        public AgentSessionStatusMachine(Func<DateTimeOffset>? nowProvider = null)
        {
            _now = nowProvider ?? (() => DateTimeOffset.UtcNow);
            var now = _now();
            _statusSince = now;
            _lastOutputAt = now;
            // A fresh session has a live process and no known children yet.
            _kind = AgentSessionStatusKind.AwaitingInput;
        }

        // ── Signals (UI thread) ─────────────────────────────────────────────

        public void NotifyOutput()
        {
            RunUnderGate(now =>
            {
                _outputSequence++;
                _lastOutputAt = now;
                if (_stalled)
                {
                    // Output resumed: the stall is over. Re-announce the current
                    // status so event consumers see the recovery explicitly.
                    _stalled = false;
                    return new List<AgentSessionStatusEvent>
                    {
                        MakeEvent(AgentSessionEventType.StatusChanged, Compute(now), now),
                    };
                }
                return null;
            });
        }

        public void NotifyPromptReady()
        {
            RunUnderGate(_ =>
            {
                _precise = true;
                _promptSeen = true;
                _commandInFlight = false;
                _currentCommand = null;
                return null;
            });
        }

        public void NotifyCommandAccepted(string? commandText)
        {
            RunUnderGate(_ =>
            {
                _precise = true;
                _currentCommand = string.IsNullOrWhiteSpace(commandText) ? null : commandText.Trim();
                return null;
            });
        }

        public void NotifyCommandStarted()
        {
            RunUnderGate(now =>
            {
                _precise = true;
                _promptSeen = true;
                _commandInFlight = true;
                _commandStartedAt = now;
                // A new command starts a fresh silence episode: without this, a
                // stall from the previous command would leak into this one (an
                // immediate IsStalled=true and no fresh stalled event later).
                _lastOutputAt = now;
                _stalled = false;
                return null;
            });
        }

        public void NotifyCommandFinished(int? exitCode)
        {
            RunUnderGate(now =>
            {
                _precise = true;
                _commandInFlight = false;
                var duration = _commandStartedAt.HasValue ? now - _commandStartedAt.Value : (TimeSpan?)null;
                _commandStartedAt = null;
                var events = new List<AgentSessionStatusEvent>
                {
                    MakeEvent(AgentSessionEventType.CommandFinished, Compute(now), now, exitCode, duration),
                };
                _currentCommand = null;
                return events;
            });
        }

        public void NotifyBell()
        {
            RunUnderGate(now => new List<AgentSessionStatusEvent>
            {
                MakeEvent(AgentSessionEventType.Bell, Compute(now), now),
            });
        }

        public void NotifyAltScreenChanged(bool isAltScreenActive)
        {
            RunUnderGate(_ =>
            {
                _altScreenActive = isAltScreenActive;
                return null;
            });
        }

        public void NotifyExited(int exitCode)
        {
            RunUnderGate(_ =>
            {
                _exited = true;
                _exitCode = exitCode;
                _commandInFlight = false;
                _currentCommand = null;
                return null;
            });
        }

        /// <summary>
        /// Stores a screen observation. Returns false and stores nothing when output has
        /// arrived since the screen was captured (the observation is already stale).
        /// Any thread.
        /// </summary>
        public bool NotifyObserved(ScreenObservation observation)
        {
            ArgumentNullException.ThrowIfNull(observation);
            bool accepted = false;
            RunUnderGate(_ =>
            {
                if (observation.OutputSequence != _outputSequence) return null;
                _observation = observation;
                accepted = true;
                return null;
            });
            return accepted;
        }

        // ── Sweep (periodic; endpoint-owned in PR2) ─────────────────────────

        /// <summary>
        /// Contributes the time-based transitions and the child-process
        /// heuristic. Idle and stall thresholds only ever fire here, so the
        /// cadence of the caller bounds their latency (1 s in production).
        /// Null means the probe couldn't answer (session initializing or being
        /// swapped): the last known value is kept rather than flapping the
        /// heuristic status through a transient false.
        /// </summary>
        public void Sweep(bool? hasActiveChildProcesses)
        {
            RunUnderGate(now =>
            {
                if (hasActiveChildProcesses.HasValue)
                {
                    _hasActiveChildren = hasActiveChildProcesses.Value;
                }

                if (!_exited
                    && ComputeKind(now) == AgentSessionStatusKind.Running
                    && !_stalled
                    && now - _lastOutputAt >= TimeSpan.FromSeconds(StallThresholdSeconds))
                {
                    _stalled = true;
                    return new List<AgentSessionStatusEvent>
                    {
                        MakeEvent(AgentSessionEventType.Stalled, Compute(now), now),
                    };
                }
                return null;
            });
        }

        // ── Reads ───────────────────────────────────────────────────────────

        public AgentSessionStatusSnapshot Snapshot()
        {
            lock (_gate)
            {
                var now = _now();
                var (kind, confidence) = Compute(now);
                var fresh = FreshObservation();
                return new AgentSessionStatusSnapshot
                {
                    Kind = kind,
                    Confidence = confidence,
                    ExitCode = _exitCode,
                    CurrentCommand = _currentCommand,
                    StatusSince = _statusSince,
                    LastOutputAt = _lastOutputAt,
                    IsStalled = _stalled,
                    OutputSequence = _outputSequence,
                    Observation = fresh,
                    ObservationAgeMs = fresh == null ? null : (long)(now - fresh.ObservedAt).TotalMilliseconds,
                };
            }
        }

        // ── Internals ───────────────────────────────────────────────────────

        // Pending events plus a single-drainer flag: signals arrive from the UI
        // thread while Sweep runs on the endpoint's timer thread, so releasing
        // the gate before invoking handlers could deliver events out of the
        // order they were generated. Events are enqueued under the gate and
        // drained by exactly one thread at a time, preserving global order
        // without ever invoking handlers while holding the gate.
        private readonly Queue<AgentSessionStatusEvent> _pendingEvents = new();
        private bool _draining;

        /// <summary>
        /// Mutates under the lock, recomputes the derived status, and raises
        /// any events (mutation-specific ones plus StatusChanged) outside the
        /// lock, in generation order.
        /// </summary>
        private void RunUnderGate(Func<DateTimeOffset, List<AgentSessionStatusEvent>?> mutate)
        {
            lock (_gate)
            {
                var now = _now();
                var beforeKind = _kind;
                var beforeConfidence = _confidence;
                var produced = mutate(now);
                bool alreadyAnnounced = false;
                if (produced != null)
                {
                    foreach (var evt in produced)
                    {
                        _pendingEvents.Enqueue(evt);
                        alreadyAnnounced |= evt.Type == AgentSessionEventType.StatusChanged;
                    }
                }

                var after = Compute(now);
                bool kindChanged = after.Kind != beforeKind;
                bool confidenceChanged = after.Confidence != beforeConfidence;
                if (kindChanged)
                {
                    _kind = after.Kind;
                    _statusSince = now;
                    if (after.Kind != AgentSessionStatusKind.Running)
                    {
                        _stalled = false; // stall is a running-only condition
                    }
                }
                _confidence = after.Confidence;
                // A tier change with the same kind (heuristic awaitingInput becoming observed
                // awaitingInput, or an observation going stale) is a status change to a caller
                // reading the tier, so it emits too. StatusSince is about the kind and stays put.
                // A mutation that already announced the new status itself (stall recovery in
                // NotifyOutput, whose event is computed after the mutation and so already carries
                // this kind and tier) is not announced a second time.
                if ((kindChanged || confidenceChanged) && !alreadyAnnounced)
                {
                    _pendingEvents.Enqueue(
                        MakeEvent(AgentSessionEventType.StatusChanged, after, now, _exited ? _exitCode : null));
                }

                if (_draining || _pendingEvents.Count == 0)
                {
                    return; // another thread is already delivering, or nothing to deliver
                }
                _draining = true;
            }

            DrainPendingEvents();
        }

        private void DrainPendingEvents()
        {
            while (true)
            {
                AgentSessionStatusEvent next;
                lock (_gate)
                {
                    if (_pendingEvents.Count == 0)
                    {
                        _draining = false;
                        return;
                    }
                    next = _pendingEvents.Dequeue();
                }

                try
                {
                    EventEmitted?.Invoke(next);
                }
                catch
                {
                    lock (_gate) { _draining = false; }
                    throw;
                }
            }
        }

        private AgentSessionStatusKind ComputeKind(DateTimeOffset now) => Compute(now).Kind;

        private (AgentSessionStatusKind Kind, AgentSessionStatusConfidence Confidence) Compute(DateTimeOffset now)
        {
            var baseConfidence = _precise ? AgentSessionStatusConfidence.Precise : AgentSessionStatusConfidence.Heuristic;
            if (_exited) return (AgentSessionStatusKind.Exited, baseConfidence);
            if (_altScreenActive) return (AgentSessionStatusKind.Running, baseConfidence);

            var fresh = FreshObservation();

            if (_precise)
            {
                if (!_commandInFlight) return (PromptKind(now), AgentSessionStatusConfidence.Precise);
                // The one precise override: a program inside the running command is waiting on the user.
                if (fresh is { } f && f.Confidence >= ObservedOverrideThreshold && f.Activity == ScreenActivity.WaitingForUser)
                {
                    return (PromptKind(now), AgentSessionStatusConfidence.Observed);
                }
                return (AgentSessionStatusKind.Running, AgentSessionStatusConfidence.Precise);
            }

            if (ObservedHeuristicKind(fresh, now) is { } observed)
            {
                return observed;
            }

            return _hasActiveChildren
                ? (AgentSessionStatusKind.Running, AgentSessionStatusConfidence.Heuristic)
                : (PromptKind(now), AgentSessionStatusConfidence.Heuristic);
        }

        /// <summary>
        /// The heuristic tier's screen-observation override: a confident, non-blank fresh
        /// observation picks the status outright. Returns null when no such observation applies,
        /// so the caller falls back to the active-children heuristic.
        /// </summary>
        private (AgentSessionStatusKind Kind, AgentSessionStatusConfidence Confidence)? ObservedHeuristicKind(
            ScreenObservation? fresh, DateTimeOffset now)
        {
            if (fresh is not { } f || f.Confidence < ObservedOverrideThreshold || f.Activity == ScreenActivity.UnknownBlank)
            {
                return null;
            }

            return f.Activity is ScreenActivity.CommandRunning or ScreenActivity.AgentWorking
                ? (AgentSessionStatusKind.Running, AgentSessionStatusConfidence.Observed)
                : (PromptKind(now), AgentSessionStatusConfidence.Observed);
        }

        private AgentSessionStatusKind PromptKind(DateTimeOffset now)
            => now - _lastOutputAt >= TimeSpan.FromSeconds(IdleThresholdSeconds)
                ? AgentSessionStatusKind.Idle
                : AgentSessionStatusKind.AwaitingInput;

        private ScreenObservation? FreshObservation()
            => _observation is { } o && o.OutputSequence == _outputSequence ? o : null;

        private static AgentSessionStatusEvent MakeEvent(
            AgentSessionEventType type,
            (AgentSessionStatusKind Kind, AgentSessionStatusConfidence Confidence) status,
            DateTimeOffset timestamp,
            int? exitCode = null,
            TimeSpan? duration = null) => new()
            {
                Type = type,
                Status = status.Kind,
                Confidence = status.Confidence,
                Timestamp = timestamp,
                ExitCode = exitCode,
                Duration = duration,
            };
    }
}
