using System;
using System.Collections.Generic;
using System.Linq;
using Ntilde.AgentHost;

namespace Ntilde.AppTests.AgentHost;

/// <summary>
/// Deterministic tests for the A2 status state machine
/// (docs/plans/2026-07-07-agent-host-a2-status-design.md). A fake clock
/// drives every time-based threshold; no UI, no timers, no PTY.
/// </summary>
public class AgentSessionStatusMachineTests
{
    private sealed class FakeClock
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 7, 7, 12, 0, 0, TimeSpan.Zero);
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

    [Fact]
    public void Fresh_session_is_awaiting_input_with_heuristic_confidence()
    {
        var (machine, _, _) = Make();
        var snapshot = machine.Snapshot();

        Assert.Equal(AgentSessionStatusKind.AwaitingInput, snapshot.Kind);
        Assert.Equal(AgentSessionStatusConfidence.Heuristic, snapshot.Confidence);
        Assert.False(snapshot.IsStalled);
    }

    [Fact]
    public void Precise_command_lifecycle_drives_running_then_awaiting_input()
    {
        var (machine, _, events) = Make();

        machine.NotifyPromptReady();
        Assert.Equal(AgentSessionStatusKind.AwaitingInput, machine.Snapshot().Kind);
        Assert.Equal(AgentSessionStatusConfidence.Precise, machine.Snapshot().Confidence);

        machine.NotifyCommandAccepted("cargo build");
        machine.NotifyCommandStarted();
        var running = machine.Snapshot();
        Assert.Equal(AgentSessionStatusKind.Running, running.Kind);
        Assert.Equal("cargo build", running.CurrentCommand);

        machine.NotifyCommandFinished(exitCode: 0);
        Assert.Equal(AgentSessionStatusKind.AwaitingInput, machine.Snapshot().Kind);
        Assert.Null(machine.Snapshot().CurrentCommand);

        Assert.Contains(events, e => e.Type == AgentSessionEventType.StatusChanged && e.Status == AgentSessionStatusKind.Running);
        Assert.Contains(events, e => e.Type == AgentSessionEventType.CommandFinished && e.ExitCode == 0);
    }

    [Fact]
    public void CommandFinished_reports_duration_from_the_injected_clock()
    {
        var (machine, clock, events) = Make();
        machine.NotifyCommandStarted();
        clock.Advance(TimeSpan.FromSeconds(42));
        machine.NotifyCommandFinished(exitCode: 1);

        var finished = Assert.Single(events, e => e.Type == AgentSessionEventType.CommandFinished);
        Assert.Equal(TimeSpan.FromSeconds(42), finished.Duration);
        Assert.Equal(1, finished.ExitCode);
    }

    /// <summary>
    /// The snapshot forgets the command the moment it finishes, so the event is the only place an agent can pair the
    /// exit code with what ran - and it must never pair it with a command that did not run.
    /// </summary>
    [Fact]
    public void CommandFinished_carries_the_command_it_finishes_and_never_a_stale_one()
    {
        var (machine, _, events) = Make();

        machine.NotifyCommandAccepted("  scripts/build.sh test  ");
        machine.NotifyCommandStarted();
        machine.NotifyCommandFinished(exitCode: 0);

        var first = Assert.Single(events, e => e.Type == AgentSessionEventType.CommandFinished);
        Assert.Equal("scripts/build.sh test", first.Command);

        // A D with no C before it (a bare D, or one written by the command's own output) names nothing.
        machine.NotifyCommandFinished(exitCode: 0);
        Assert.Null(events.Last(e => e.Type == AgentSessionEventType.CommandFinished).Command);

        // A bare 133;C carries no text: the finish reports none rather than the previous command.
        machine.NotifyCommandAccepted(null);
        machine.NotifyCommandStarted();
        machine.NotifyCommandFinished(exitCode: 2);
        var bare = events.Last(e => e.Type == AgentSessionEventType.CommandFinished);
        Assert.Equal(2, bare.ExitCode);
        Assert.Null(bare.Command);

        // Other events never carry a command.
        Assert.All(events.Where(e => e.Type != AgentSessionEventType.CommandFinished), e => Assert.Null(e.Command));
    }

    [Fact]
    public void Heuristic_tier_uses_child_processes_from_the_sweep()
    {
        var (machine, _, _) = Make();

        machine.Sweep(hasActiveChildProcesses: true);
        Assert.Equal(AgentSessionStatusKind.Running, machine.Snapshot().Kind);
        Assert.Equal(AgentSessionStatusConfidence.Heuristic, machine.Snapshot().Confidence);

        machine.Sweep(hasActiveChildProcesses: false);
        Assert.Equal(AgentSessionStatusKind.AwaitingInput, machine.Snapshot().Kind);
    }

    [Fact]
    public void Registration_probe_is_unknown_without_a_published_lifecycle()
    {
        // No session yet → the probe answers "unknown", never a hard false.
        var registration = new AgentSessionRegistration(
            Guid.NewGuid(), new Ntilde.VT.TerminalBuffer(80, 24), "t", "p", "local", isActive: false);
        Assert.Null(registration.ProbeHasActiveChildProcesses());
    }

    [Fact]
    public void Sweep_with_unknown_child_state_keeps_the_last_known_value()
    {
        // The probe returns null while the session is initializing or being
        // swapped; a transient null must not flap Running → AwaitingInput.
        var (machine, _, _) = Make();
        machine.Sweep(hasActiveChildProcesses: true);
        Assert.Equal(AgentSessionStatusKind.Running, machine.Snapshot().Kind);

        machine.Sweep(hasActiveChildProcesses: null);
        Assert.Equal(AgentSessionStatusKind.Running, machine.Snapshot().Kind);

        machine.Sweep(hasActiveChildProcesses: false);
        Assert.Equal(AgentSessionStatusKind.AwaitingInput, machine.Snapshot().Kind);
    }

    [Fact]
    public void Alt_screen_forces_running_in_both_tiers()
    {
        var (machine, _, _) = Make();
        machine.NotifyAltScreenChanged(true);
        Assert.Equal(AgentSessionStatusKind.Running, machine.Snapshot().Kind);

        // Even precise "at prompt" state is overridden while a TUI owns the screen.
        machine.NotifyPromptReady();
        Assert.Equal(AgentSessionStatusKind.Running, machine.Snapshot().Kind);

        machine.NotifyAltScreenChanged(false);
        Assert.Equal(AgentSessionStatusKind.AwaitingInput, machine.Snapshot().Kind);
    }

    [Fact]
    public void Idle_requires_the_documented_threshold_without_output()
    {
        var (machine, clock, events) = Make();
        machine.NotifyPromptReady();

        clock.Advance(TimeSpan.FromSeconds(AgentSessionStatusMachine.IdleThresholdSeconds - 1));
        machine.Sweep(hasActiveChildProcesses: false);
        Assert.Equal(AgentSessionStatusKind.AwaitingInput, machine.Snapshot().Kind);

        clock.Advance(TimeSpan.FromSeconds(2));
        machine.Sweep(hasActiveChildProcesses: false);
        Assert.Equal(AgentSessionStatusKind.Idle, machine.Snapshot().Kind);
        Assert.Contains(events, e => e.Type == AgentSessionEventType.StatusChanged && e.Status == AgentSessionStatusKind.Idle);

        // Output wakes it back up.
        machine.NotifyOutput();
        Assert.Equal(AgentSessionStatusKind.AwaitingInput, machine.Snapshot().Kind);
    }

    [Fact]
    public void Stall_fires_once_while_running_and_recovers_on_output()
    {
        var (machine, clock, events) = Make();
        machine.NotifyCommandStarted();

        clock.Advance(TimeSpan.FromSeconds(AgentSessionStatusMachine.StallThresholdSeconds));
        machine.Sweep(hasActiveChildProcesses: true);
        machine.Sweep(hasActiveChildProcesses: true); // second sweep must not re-emit

        Assert.Single(events, e => e.Type == AgentSessionEventType.Stalled);
        Assert.True(machine.Snapshot().IsStalled);
        Assert.Equal(AgentSessionStatusKind.Running, machine.Snapshot().Kind);

        machine.NotifyOutput();
        Assert.False(machine.Snapshot().IsStalled);
        Assert.Contains(events, e => e.Type == AgentSessionEventType.StatusChanged && e.Status == AgentSessionStatusKind.Running);

        // A fresh silence stalls again — it's a per-episode event, not one-shot.
        clock.Advance(TimeSpan.FromSeconds(AgentSessionStatusMachine.StallThresholdSeconds));
        machine.Sweep(hasActiveChildProcesses: true);
        Assert.Equal(2, events.Count(e => e.Type == AgentSessionEventType.Stalled));
    }

    [Fact]
    public void Stall_state_does_not_leak_into_the_next_command()
    {
        var (machine, clock, events) = Make();
        machine.NotifyCommandStarted();
        clock.Advance(TimeSpan.FromSeconds(AgentSessionStatusMachine.StallThresholdSeconds));
        machine.Sweep(hasActiveChildProcesses: true);
        Assert.True(machine.Snapshot().IsStalled);

        // Next command begins before any output arrives (e.g. re-run from
        // history): it starts a fresh silence episode, not pre-stalled.
        machine.NotifyCommandFinished(exitCode: 124);
        machine.NotifyCommandStarted();
        Assert.False(machine.Snapshot().IsStalled);

        // And the new command can stall again with its own event.
        clock.Advance(TimeSpan.FromSeconds(AgentSessionStatusMachine.StallThresholdSeconds));
        machine.Sweep(hasActiveChildProcesses: true);
        Assert.Equal(2, events.Count(e => e.Type == AgentSessionEventType.Stalled));
    }

    [Fact]
    public void Stall_does_not_fire_at_a_prompt()
    {
        var (machine, clock, events) = Make();
        machine.NotifyPromptReady();

        clock.Advance(TimeSpan.FromSeconds(AgentSessionStatusMachine.StallThresholdSeconds + 5));
        machine.Sweep(hasActiveChildProcesses: false);

        Assert.DoesNotContain(events, e => e.Type == AgentSessionEventType.Stalled);
        Assert.False(machine.Snapshot().IsStalled);
    }

    [Fact]
    public void Exit_is_terminal_and_carries_the_exit_code()
    {
        var (machine, _, events) = Make();
        machine.NotifyCommandStarted();
        machine.NotifyExited(137);

        var snapshot = machine.Snapshot();
        Assert.Equal(AgentSessionStatusKind.Exited, snapshot.Kind);
        Assert.Equal(137, snapshot.ExitCode);

        // Nothing moves it off Exited.
        machine.NotifyOutput();
        machine.Sweep(hasActiveChildProcesses: true);
        machine.NotifyPromptReady();
        Assert.Equal(AgentSessionStatusKind.Exited, machine.Snapshot().Kind);

        var exitEvent = Assert.Single(events, e => e.Type == AgentSessionEventType.StatusChanged && e.Status == AgentSessionStatusKind.Exited);
        Assert.Equal(137, exitEvent.ExitCode);
        Assert.Null(exitEvent.NeedsUser);
        Assert.Null(snapshot.NeedsUser);
    }

    /// <summary>
    /// #610: exited is final for a session, not for its pane. When the pane starts a new one - the user signing in to an
    /// agent's SSH tab that stopped for them - the status starts over and says so, so the agent can carry on in that tab.
    /// </summary>
    [Fact]
    public void A_new_session_after_an_exit_starts_the_status_over_and_announces_it()
    {
        var (machine, clock, events) = Make();
        machine.NotifyPromptReady();
        machine.NotifyExited(-1, AgentNeedsUserReason.Password);
        clock.Advance(TimeSpan.FromSeconds(5));
        events.Clear();

        machine.NotifySessionStarted();

        var snapshot = machine.Snapshot();
        Assert.Equal(AgentSessionStatusKind.AwaitingInput, snapshot.Kind);
        Assert.Equal(AgentSessionStatusConfidence.Heuristic, snapshot.Confidence); // the new shell has to show its integration again
        Assert.Null(snapshot.ExitCode);
        Assert.Null(snapshot.NeedsUser);
        Assert.Equal(clock.Now, snapshot.StatusSince);
        var restarted = Assert.Single(events);
        Assert.Equal(AgentSessionEventType.StatusChanged, restarted.Type);
        Assert.Equal(AgentSessionStatusKind.AwaitingInput, restarted.Status);
    }

    /// <summary>A session swapped under a live shell (a mux reattach) is the same session: nothing starts over.</summary>
    [Fact]
    public void A_new_session_while_the_last_one_is_live_changes_nothing()
    {
        var (machine, _, events) = Make();
        machine.NotifyCommandAccepted("make");
        machine.NotifyCommandStarted();
        events.Clear();

        machine.NotifySessionStarted();

        Assert.Equal(AgentSessionStatusKind.Running, machine.Snapshot().Kind);
        Assert.Equal("make", machine.Snapshot().CurrentCommand);
        Assert.Empty(events);
    }

    /// <summary>#610: an SSH connection an agent opened that stopped for the user says what it needed, in both places an agent reads.</summary>
    [Fact]
    public void Exit_that_needs_the_user_says_why_in_the_snapshot_and_the_exit_event()
    {
        var (machine, _, events) = Make();
        machine.NotifyExited(-1, AgentNeedsUserReason.Password);

        var snapshot = machine.Snapshot();
        Assert.Equal(AgentSessionStatusKind.Exited, snapshot.Kind);
        Assert.Equal(AgentNeedsUserReason.Password, snapshot.NeedsUser);

        var exitEvent = Assert.Single(events, e => e.Type == AgentSessionEventType.StatusChanged && e.Status == AgentSessionStatusKind.Exited);
        Assert.Equal(AgentNeedsUserReason.Password, exitEvent.NeedsUser);
    }

    [Fact]
    public void Bell_emits_an_event_without_changing_status()
    {
        var (machine, _, events) = Make();
        machine.NotifyBell();

        Assert.Single(events, e => e.Type == AgentSessionEventType.Bell);
        Assert.Equal(AgentSessionStatusKind.AwaitingInput, machine.Snapshot().Kind);
    }

    [Fact]
    public void StatusSince_tracks_the_last_transition_not_the_last_signal()
    {
        var (machine, clock, _) = Make();
        machine.NotifyCommandStarted();
        var since = machine.Snapshot().StatusSince;

        clock.Advance(TimeSpan.FromSeconds(5));
        machine.NotifyOutput(); // no transition
        Assert.Equal(since, machine.Snapshot().StatusSince);

        clock.Advance(TimeSpan.FromSeconds(5));
        machine.NotifyCommandFinished(0); // Running → AwaitingInput
        Assert.NotEqual(since, machine.Snapshot().StatusSince);
    }

    [Fact]
    public void Registration_exposes_a_status_machine()
    {
        var registration = new AgentSessionRegistration(
            Guid.NewGuid(), new Ntilde.VT.TerminalBuffer(80, 24), "t", "p", "local", isActive: false);
        Assert.NotNull(registration.StatusMachine);
        Assert.Equal(AgentSessionStatusKind.AwaitingInput, registration.StatusMachine.Snapshot().Kind);
    }
}
