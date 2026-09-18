using System;
using System.Collections.Generic;
using System.Linq;
using Ntilde.AgentHost;
using Ntilde.Inference;

namespace Ntilde.AppTests.AgentHost;

/// <summary>
/// The observed tier (docs/superpowers/specs/2026-09-17-screen-inference-observed-status-design.md §1.3).
/// A fake clock drives thresholds; observations are handed in directly, no network.
/// </summary>
public class AgentSessionStatusMachineObservedTests
{
    private sealed class FakeClock
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan by) => Now += by;
        public Func<DateTimeOffset> Provider => () => Now;
    }

    private static (AgentSessionStatusMachine Machine, FakeClock Clock, List<AgentSessionStatusEvent> Events) Make()
    {
        var clock = new FakeClock();
        var machine = new AgentSessionStatusMachine(clock.Provider);
        var events = new List<AgentSessionStatusEvent>();
        machine.EventEmitted += events.Add;
        return (machine, clock, events);
    }

    private static ScreenObservation Obs(AgentSessionStatusMachine machine, ScreenActivity activity, double confidence = 0.95, double attention = 0.5)
        => new()
        {
            Activity = activity,
            Confidence = confidence,
            NeedsAttention = attention,
            LastCommandFailed = 0.05,
            ObservedAt = machine.Snapshot().LastOutputAt,
            OutputSequence = machine.Snapshot().OutputSequence,
        };

    [Fact]
    public void Output_increments_the_sequence()
    {
        var (machine, _, _) = Make();
        var before = machine.Snapshot().OutputSequence;
        machine.NotifyOutput();
        Assert.Equal(before + 1, machine.Snapshot().OutputSequence);
    }

    [Fact]
    public void Heuristic_tier_takes_a_confident_running_observation()
    {
        var (machine, _, _) = Make();
        machine.Sweep(hasActiveChildProcesses: false); // heuristic says nothing is running

        Assert.True(machine.NotifyObserved(Obs(machine, ScreenActivity.CommandRunning)));

        var s = machine.Snapshot();
        Assert.Equal(AgentSessionStatusKind.Running, s.Kind);
        Assert.Equal(AgentSessionStatusConfidence.Observed, s.Confidence);
        Assert.NotNull(s.Observation);
    }

    [Fact]
    public void Heuristic_tier_takes_waiting_and_idle_promotion_still_applies()
    {
        var (machine, clock, _) = Make();
        machine.Sweep(hasActiveChildProcesses: true); // heuristic would say running

        machine.NotifyObserved(Obs(machine, ScreenActivity.WaitingForUser));
        Assert.Equal(AgentSessionStatusKind.AwaitingInput, machine.Snapshot().Kind);
        Assert.Equal(AgentSessionStatusConfidence.Observed, machine.Snapshot().Confidence);

        clock.Advance(TimeSpan.FromSeconds(AgentSessionStatusMachine.IdleThresholdSeconds));
        Assert.Equal(AgentSessionStatusKind.Idle, machine.Snapshot().Kind);
        Assert.Equal(AgentSessionStatusConfidence.Observed, machine.Snapshot().Confidence);
    }

    [Fact]
    public void Below_threshold_observation_is_ignored()
    {
        var (machine, _, _) = Make();
        machine.Sweep(hasActiveChildProcesses: true);

        machine.NotifyObserved(Obs(machine, ScreenActivity.WaitingForUser, confidence: AgentSessionStatusMachine.ObservedOverrideThreshold - 0.01));

        Assert.Equal(AgentSessionStatusKind.Running, machine.Snapshot().Kind);
        Assert.Equal(AgentSessionStatusConfidence.Heuristic, machine.Snapshot().Confidence);
    }

    [Fact]
    public void Threshold_is_inclusive()
    {
        var (machine, _, _) = Make();
        machine.Sweep(hasActiveChildProcesses: true);

        machine.NotifyObserved(Obs(machine, ScreenActivity.WaitingForUser, confidence: AgentSessionStatusMachine.ObservedOverrideThreshold));

        Assert.Equal(AgentSessionStatusConfidence.Observed, machine.Snapshot().Confidence);
    }

    [Fact]
    public void Unknown_blank_is_ignored()
    {
        var (machine, _, _) = Make();
        machine.Sweep(hasActiveChildProcesses: true);

        machine.NotifyObserved(Obs(machine, ScreenActivity.UnknownBlank));

        Assert.Equal(AgentSessionStatusKind.Running, machine.Snapshot().Kind);
        Assert.Equal(AgentSessionStatusConfidence.Heuristic, machine.Snapshot().Confidence);
    }

    [Fact]
    public void New_output_makes_the_observation_stale()
    {
        var (machine, _, _) = Make();
        machine.Sweep(hasActiveChildProcesses: true);
        machine.NotifyObserved(Obs(machine, ScreenActivity.WaitingForUser));
        Assert.Equal(AgentSessionStatusKind.AwaitingInput, machine.Snapshot().Kind);

        machine.NotifyOutput();

        var s = machine.Snapshot();
        Assert.Equal(AgentSessionStatusKind.Running, s.Kind);
        Assert.Equal(AgentSessionStatusConfidence.Heuristic, s.Confidence);
        Assert.Null(s.Observation);
    }

    [Fact]
    public void Observation_from_an_older_sequence_is_rejected()
    {
        var (machine, _, _) = Make();
        var stale = Obs(machine, ScreenActivity.WaitingForUser);
        machine.NotifyOutput();

        Assert.False(machine.NotifyObserved(stale));
        Assert.Null(machine.Snapshot().Observation);
    }

    [Fact]
    public void Precise_running_command_is_refined_to_awaiting_input_only_for_waiting_for_user()
    {
        var (machine, _, _) = Make();
        machine.NotifyPromptReady();
        machine.NotifyCommandStarted();

        machine.NotifyObserved(Obs(machine, ScreenActivity.CommandRunning));
        Assert.Equal(AgentSessionStatusKind.Running, machine.Snapshot().Kind);
        Assert.Equal(AgentSessionStatusConfidence.Precise, machine.Snapshot().Confidence);

        machine.NotifyObserved(Obs(machine, ScreenActivity.WaitingForUser));
        Assert.Equal(AgentSessionStatusKind.AwaitingInput, machine.Snapshot().Kind);
        Assert.Equal(AgentSessionStatusConfidence.Observed, machine.Snapshot().Confidence);
    }

    [Fact]
    public void Precise_prompt_is_never_overridden()
    {
        var (machine, _, _) = Make();
        machine.NotifyPromptReady();

        machine.NotifyObserved(Obs(machine, ScreenActivity.CommandRunning));

        Assert.Equal(AgentSessionStatusKind.AwaitingInput, machine.Snapshot().Kind);
        Assert.Equal(AgentSessionStatusConfidence.Precise, machine.Snapshot().Confidence);
    }

    [Fact]
    public void Alt_screen_and_exit_beat_any_observation()
    {
        var (machine, _, _) = Make();
        machine.NotifyAltScreenChanged(true);
        machine.NotifyObserved(Obs(machine, ScreenActivity.WaitingForUser));
        Assert.Equal(AgentSessionStatusKind.Running, machine.Snapshot().Kind);
        Assert.Equal(AgentSessionStatusConfidence.Heuristic, machine.Snapshot().Confidence);

        machine.NotifyAltScreenChanged(false);
        machine.NotifyExited(0);
        machine.NotifyObserved(Obs(machine, ScreenActivity.CommandRunning));
        Assert.Equal(AgentSessionStatusKind.Exited, machine.Snapshot().Kind);
    }

    [Fact]
    public void Observed_running_still_stalls_after_the_stall_threshold()
    {
        var (machine, clock, events) = Make();
        machine.Sweep(hasActiveChildProcesses: false);
        machine.NotifyObserved(Obs(machine, ScreenActivity.AgentWorking));
        Assert.Equal(AgentSessionStatusKind.Running, machine.Snapshot().Kind);

        clock.Advance(TimeSpan.FromSeconds(AgentSessionStatusMachine.StallThresholdSeconds));
        machine.Sweep(hasActiveChildProcesses: false);

        Assert.Contains(events, e => e.Type == AgentSessionEventType.Stalled);
        Assert.True(machine.Snapshot().IsStalled);
    }

    [Fact]
    public void Status_changed_event_fires_when_an_observation_changes_the_kind()
    {
        var (machine, _, events) = Make();
        machine.Sweep(hasActiveChildProcesses: false);
        events.Clear();

        machine.NotifyObserved(Obs(machine, ScreenActivity.CommandRunning));

        var evt = Assert.Single(events, e => e.Type == AgentSessionEventType.StatusChanged);
        Assert.Equal(AgentSessionStatusKind.Running, evt.Status);
    }

    [Fact]
    public void Snapshot_reports_age_and_threshold()
    {
        var (machine, clock, _) = Make();
        machine.NotifyObserved(Obs(machine, ScreenActivity.IdleShellPrompt));
        clock.Advance(TimeSpan.FromMilliseconds(1500));

        var s = machine.Snapshot();
        Assert.Equal(1500, s.ObservationAgeMs);
        Assert.Equal(85, s.ObservedOverrideThresholdPercent);
    }
}
