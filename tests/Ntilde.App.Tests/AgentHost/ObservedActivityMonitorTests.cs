using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ntilde.AgentHost;
using Ntilde.CommandAssist.Domain;
using Ntilde.Inference;
using Ntilde.VT;

namespace Ntilde.AppTests.AgentHost;

/// <summary>
/// The monitor's policy (spec §2.2, §4), with a fake classifier, fake clock, fake capture and an
/// isolated registry. No timers run: tests call <see cref="ObservedActivityMonitor.TickAsync"/>.
/// </summary>
public class ObservedActivityMonitorTests
{
    private sealed class FakeClock
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan by) => Now += by;
        public Func<DateTimeOffset> Provider => () => Now;
    }

    private sealed class FakeClassifier : IScreenActivityClassifier
    {
        public bool HasCredentials { get; set; } = true;
        public List<ScreenSample> Samples { get; } = new();
        public Func<ScreenSample, ScreenClassificationResult> Respond { get; set; } = _ => Answered(ScreenActivity.WaitingForUser, 0.95, 0.9);
        public TaskCompletionSource<ScreenClassificationResult>? Hold { get; set; }

        public async Task<ScreenClassificationResult> ClassifyAsync(ScreenSample sample, CancellationToken cancellationToken)
        {
            Samples.Add(sample);
            if (Hold != null) return await Hold.Task;
            return Respond(sample);
        }

        public static ScreenClassificationResult Answered(ScreenActivity activity, double confidence, double attention)
            => new(ScreenClassificationOutcome.Answered, new ScreenActivityAnswer(activity, confidence, attention, 0.05, 900), null);
    }

    private sealed class MarkingFilter : ISecretsFilter
    {
        public RedactionResult Redact(string commandText) => new("[R]" + commandText, true);
    }

    private sealed class Harness
    {
        public FakeClock Clock { get; } = new();
        public FakeClassifier Classifier { get; } = new();
        public AgentSessionRegistry Registry { get; } = new();
        public Dictionary<Guid, string?> Screens { get; } = new();
        public List<string> Log { get; } = new();
        public ObservedActivityMonitor Monitor { get; }

        public Harness()
        {
            Monitor = new ObservedActivityMonitor(
                Registry,
                Classifier,
                new MarkingFilter(),
                reg => Screens.TryGetValue(reg.PaneId, out var text) && text != null ? new ScreenSample(text, 24, 80) : null,
                Clock.Provider,
                Log.Add);
        }

        public AgentSessionRegistration AddPane(string kind = "local", Guid? profileId = null, string screen = "user@host:~$ ")
        {
            var reg = new AgentSessionRegistration(
                paneId: Guid.NewGuid(),
                buffer: new TerminalBuffer(80, 24),
                title: "pane",
                profileName: "Terminal",
                kind: kind,
                isActive: false,
                nowProvider: Clock.Provider,
                profileId: profileId);
            Registry.Register(reg);
            Screens[reg.PaneId] = screen;
            return reg;
        }

        /// <summary>Output now, then advance past the quiet window so the pane is due.</summary>
        public void OutputThenQuiet(AgentSessionRegistration reg)
        {
            reg.StatusMachine.NotifyOutput();
            Clock.Advance(ObservedActivityMonitor.QuietWindow);
        }
    }

    [Fact]
    public async Task Quiet_local_pane_with_new_output_is_classified_and_observed()
    {
        var h = new Harness();
        var reg = h.AddPane();
        h.OutputThenQuiet(reg);

        await h.Monitor.TickAsync();

        var sample = Assert.Single(h.Classifier.Samples);
        Assert.Equal("[R]user@host:~$ ", sample.Text);
        Assert.Equal(24, sample.Rows);
        Assert.Equal(80, sample.Cols);
        var snapshot = reg.StatusMachine.Snapshot();
        Assert.NotNull(snapshot.Observation);
        Assert.Equal(ScreenActivity.WaitingForUser, snapshot.Observation!.Activity);
        Assert.Equal(AgentSessionStatusConfidence.Observed, snapshot.Confidence);
        Assert.Equal(1, h.Monitor.RequestCount);
    }

    [Fact]
    public async Task Not_due_before_the_quiet_window()
    {
        var h = new Harness();
        var reg = h.AddPane();
        reg.StatusMachine.NotifyOutput();
        h.Clock.Advance(ObservedActivityMonitor.QuietWindow - TimeSpan.FromMilliseconds(1));

        await h.Monitor.TickAsync();

        Assert.Empty(h.Classifier.Samples);
    }

    [Fact]
    public async Task Not_due_without_new_output_since_the_last_send()
    {
        var h = new Harness();
        var reg = h.AddPane();
        h.OutputThenQuiet(reg);
        await h.Monitor.TickAsync();
        h.Clock.Advance(TimeSpan.FromMinutes(1));

        await h.Monitor.TickAsync();

        Assert.Single(h.Classifier.Samples);
    }

    [Fact]
    public async Task Not_due_within_the_minimum_interval_even_with_new_output()
    {
        var h = new Harness();
        var reg = h.AddPane();
        h.OutputThenQuiet(reg);
        await h.Monitor.TickAsync();

        h.Screens[reg.PaneId] = "user@host:~$ ls\nfile\nuser@host:~$ ";
        h.OutputThenQuiet(reg); // +2 s: total 2 s since last send, under the 5 s minimum
        await h.Monitor.TickAsync();
        Assert.Single(h.Classifier.Samples);

        h.Clock.Advance(ObservedActivityMonitor.MinInterval);
        await h.Monitor.TickAsync();
        Assert.Equal(2, h.Classifier.Samples.Count);
    }

    [Fact]
    public async Task Unchanged_text_and_blank_text_are_not_sent()
    {
        var h = new Harness();
        var reg = h.AddPane();
        h.OutputThenQuiet(reg);
        await h.Monitor.TickAsync();

        h.Clock.Advance(ObservedActivityMonitor.MinInterval);
        h.OutputThenQuiet(reg); // same screen text
        await h.Monitor.TickAsync();
        Assert.Single(h.Classifier.Samples);

        var blank = h.AddPane(screen: "   \n\n  ");
        h.OutputThenQuiet(blank);
        await h.Monitor.TickAsync();
        Assert.Single(h.Classifier.Samples);
    }

    [Fact]
    public async Task Ssh_pane_needs_the_profile_allowlist()
    {
        var h = new Harness();
        var allowed = Guid.NewGuid();
        var denied = Guid.NewGuid();
        var a = h.AddPane(kind: "ssh", profileId: allowed, screen: "a$ ");
        var d = h.AddPane(kind: "ssh", profileId: denied, screen: "d$ ");
        var noProfile = h.AddPane(kind: "ssh", profileId: null, screen: "n$ ");
        foreach (var r in new[] { a, d, noProfile }) h.OutputThenQuiet(r);

        await h.Monitor.TickAsync();
        Assert.Empty(h.Classifier.Samples); // no probe published: fail closed

        h.Monitor.SetSshProfileAllowlist(id => id == allowed);
        await h.Monitor.TickAsync();

        var sample = Assert.Single(h.Classifier.Samples);
        Assert.Equal("[R]a$ ", sample.Text);
    }

    [Fact]
    public async Task No_credentials_means_no_capture_at_all()
    {
        var h = new Harness();
        h.Classifier.HasCredentials = false;
        var reg = h.AddPane();
        h.OutputThenQuiet(reg);
        int captures = 0;
        var monitor = new ObservedActivityMonitor(h.Registry, h.Classifier, new MarkingFilter(),
            _ => { captures++; return new ScreenSample("x", 1, 1); }, h.Clock.Provider, h.Log.Add);

        await monitor.TickAsync();

        Assert.Equal(0, captures);
        Assert.Empty(h.Classifier.Samples);
    }

    [Fact]
    public async Task Answer_arriving_after_new_output_is_dropped_as_stale()
    {
        var h = new Harness();
        var reg = h.AddPane();
        h.OutputThenQuiet(reg);
        h.Classifier.Hold = new TaskCompletionSource<ScreenClassificationResult>();

        var tick = h.Monitor.TickAsync();
        reg.StatusMachine.NotifyOutput(); // output while the request is in flight
        h.Classifier.Hold.SetResult(FakeClassifier.Answered(ScreenActivity.WaitingForUser, 0.95, 0.9));
        await tick;

        Assert.Null(reg.StatusMachine.Snapshot().Observation);
        Assert.Contains(h.Log, line => line.Contains("stale", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Only_one_request_in_flight_per_pane()
    {
        var h = new Harness();
        var reg = h.AddPane();
        h.OutputThenQuiet(reg);
        h.Classifier.Hold = new TaskCompletionSource<ScreenClassificationResult>();

        var first = h.Monitor.TickAsync();
        h.Screens[reg.PaneId] = "changed$ ";
        h.OutputThenQuiet(reg);
        h.Clock.Advance(ObservedActivityMonitor.MinInterval);
        await h.Monitor.TickAsync();

        Assert.Single(h.Classifier.Samples);
        h.Classifier.Hold.SetResult(FakeClassifier.Answered(ScreenActivity.IdleShellPrompt, 0.99, 0.1));
        await first;
    }

    [Fact]
    public async Task Log_line_never_contains_screen_text()
    {
        var h = new Harness();
        var reg = h.AddPane(screen: "SECRET-MARKER-TEXT$ ");
        h.OutputThenQuiet(reg);

        await h.Monitor.TickAsync();

        Assert.NotEmpty(h.Log);
        Assert.All(h.Log, line => Assert.DoesNotContain("SECRET-MARKER", line, StringComparison.Ordinal));
        Assert.Contains(h.Log, line => line.Contains("outcome=Answered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unregistered_pane_state_is_pruned()
    {
        var h = new Harness();
        var reg = h.AddPane();
        h.OutputThenQuiet(reg);
        await h.Monitor.TickAsync();
        Assert.Equal(1, h.Monitor.TrackedPaneCount);

        h.Registry.Unregister(reg.PaneId);
        await h.Monitor.TickAsync();

        Assert.Equal(0, h.Monitor.TrackedPaneCount);
    }
}
