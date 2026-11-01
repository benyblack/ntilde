using System.Threading;
using Ntilde.AgentOutput;

namespace Ntilde.Tests.AgentOutput;

public sealed partial class AgentOutputRegionTrackerTests
{
    [Fact]
    public void ContinuousOutput_RefreshesBeforeTheStreamBecomesQuiet()
    {
        var clock = new ManualRefreshTimeProvider();
        using var h = new Harness(timeProvider: clock).Accept("agent");
        for (int i = 0; i < 9; i++)
        {
            h.Output($"chunk {i}");
            h.Tracker.NotifyInvalidate();
            clock.Advance(TimeSpan.FromMilliseconds(100));
            Assert.Equal((i + 1) / 3, h.UpdateCount);
        }
        Assert.Contains("chunk 8", h.LastText, StringComparison.Ordinal);
        Assert.True(h.LastStreaming);
    }

    [Fact]
    public void ClosedPanel_RecognizesMarkdownWhileOutputContinues()
    {
        var clock = new ManualRefreshTimeProvider();
        using var h = new Harness(enabled: false, timeProvider: clock);
        clock.Advance(AgentOutputRegionTracker.PresenceSoonDelay);
        h.PresenceChanges.Clear();
        h.Output("## Heading", "", "- first", "- second");
        for (int i = 0; i < 15; i++)
        {
            h.Output($"chunk {i}");
            h.Tracker.NotifyInvalidate();
            clock.Advance(TimeSpan.FromMilliseconds(100));
        }
        Assert.Contains(true, h.PresenceChanges);
        Assert.Equal(0, h.UpdateCount);
    }

    [Fact]
    public void FlushNow_IsNotPostponedByTheNextInvalidation()
    {
        var clock = new ManualRefreshTimeProvider();
        using var h = new Harness(timeProvider: clock).Accept("agent").Output("ready");
        h.Tracker.NotifyInvalidate();
        clock.Advance(TimeSpan.FromMilliseconds(100));
        h.Tracker.FlushNow();
        h.Tracker.NotifyInvalidate();
        clock.Advance(TimeSpan.Zero);
        Assert.Equal(1, h.UpdateCount);
    }

    [Fact]
    public void CompletionPresence_IsNotPostponedByFollowingOutput()
    {
        var clock = new ManualRefreshTimeProvider();
        using var h = new Harness(enabled: false, timeProvider: clock).Accept("agent")
            .Output("## Heading", "", "- first", "- second");
        h.Finish();
        h.Tracker.NotifyInvalidate();
        clock.Advance(AgentOutputRegionTracker.PresenceSoonDelay);
        Assert.Contains(true, h.PresenceChanges);
    }

    [Fact]
    public void Reset_CancelsThePendingReadRatherThanOnlyDroppingItsResult()
    {
        var clock = new ManualRefreshTimeProvider();
        using var h = new Harness(timeProvider: clock).Accept("agent").Output("old");
        int reads = 0;
        h.BeforeRead = () => reads++;
        h.Tracker.NotifyInvalidate();
        h.Tracker.Reset();
        clock.Advance(AgentOutputRegionTracker.DebounceDelay);
        Assert.Equal(0, reads);
        Assert.Equal(0, h.UpdateCount);
    }

    [Fact]
    public async Task InvalidationsDuringARead_CoalesceIntoOneSerializedFollowup()
    {
        var clock = new ManualRefreshTimeProvider();
        using var h = new Harness(timeProvider: clock).Accept("agent").Output("first");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int reads = 0, dispatches = 0;
        h.BeforeRead = () => Interlocked.Increment(ref reads);
        h.BeforeDispatch = () =>
        {
            if (Interlocked.Increment(ref dispatches) != 1) return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)) throw new TimeoutException("Read was not released.");
        };
        h.Tracker.NotifyInvalidate();
        var first = Task.Run(() => clock.Advance(AgentOutputRegionTracker.DebounceDelay), TestContext.Current.CancellationToken);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken), "Read never reached dispatch.");
            h.Output("latest");
            for (int i = 0; i < 20; i++) h.Tracker.NotifyInvalidate();
            clock.Advance(AgentOutputRegionTracker.DebounceDelay);
            Assert.Equal(1, Volatile.Read(ref reads));
            Assert.Equal(0, h.UpdateCount);
        }
        finally
        {
            release.Set();
            await first.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        clock.Advance(TimeSpan.Zero);
        Assert.Equal(2, reads);
        Assert.Equal(2, h.UpdateCount);
        Assert.Contains("latest", h.LastText, StringComparison.Ordinal);
        clock.Advance(AgentOutputRegionTracker.DebounceDelay);
        Assert.Equal(2, reads);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StalePresenceDispatch_IsDroppedWithoutPoisoningTheNextGeneration(bool dispose)
    {
        var clock = new ManualRefreshTimeProvider();
        using var h = new Harness(enabled: false, deferDispatch: true, timeProvider: clock);
        clock.Advance(AgentOutputRegionTracker.PresenceSoonDelay);
        h.DeliverPending();
        h.PresenceChanges.Clear();
        h.Output("## Heading", "", "- first", "- second");
        h.Tracker.NotifyInvalidate();
        clock.Advance(AgentOutputRegionTracker.PresenceDebounceDelay);
        Assert.Equal(1, h.PendingCount);
        if (dispose) h.Tracker.Dispose();
        else h.Tracker.Reset();
        h.DeliverPending();
        Assert.Empty(h.PresenceChanges);
        if (!dispose)
        {
            h.Tracker.NotifyInvalidate();
            clock.Advance(AgentOutputRegionTracker.PresenceDebounceDelay);
            h.DeliverPending();
            Assert.Contains(true, h.PresenceChanges);
        }
    }

    [Fact]
    public void FailedRead_StillRunsTheDirtyFollowup()
    {
        var clock = new ManualRefreshTimeProvider();
        using var h = new Harness(timeProvider: clock).Accept("agent").Output("ready");
        int reads = 0;
        h.BeforeRead = () =>
        {
            if (++reads != 1) return;
            h.Tracker.NotifyInvalidate();
            throw new InvalidOperationException("synthetic read failure");
        };
        h.Tracker.NotifyInvalidate();
        clock.Advance(AgentOutputRegionTracker.DebounceDelay);
        Assert.Equal(0, h.UpdateCount);
        clock.Advance(AgentOutputRegionTracker.DebounceDelay);
        Assert.Equal(2, reads);
        Assert.Equal(1, h.UpdateCount);
    }

    [Fact]
    public async Task Completion_DoesNotWaitForAnInFlightBackgroundRead()
    {
        var clock = new ManualRefreshTimeProvider();
        using var h = new Harness(timeProvider: clock).Accept("agent").Output("partial");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int reads = 0;
        h.BeforeRead = () =>
        {
            if (Interlocked.Increment(ref reads) != 1) return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken))
                throw new TimeoutException("Read was not released.");
        };
        h.Tracker.NotifyInvalidate();
        var first = Task.Run(() => clock.Advance(AgentOutputRegionTracker.DebounceDelay), TestContext.Current.CancellationToken);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            h.Output("final");
            h.Finish();
            Assert.Equal(1, h.UpdateCount);
            Assert.False(h.LastStreaming);
            Assert.Contains("final", h.LastText, StringComparison.Ordinal);
        }
        finally
        {
            release.Set();
            await first.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        Assert.Equal(1, h.UpdateCount);
    }

    [Fact]
    public void Dispose_CancelsPendingReadsAndIgnoresLateSchedulingRequests()
    {
        var clock = new ManualRefreshTimeProvider();
        using var h = new Harness(timeProvider: clock).Accept("agent").Output("old");
        int reads = 0;
        h.BeforeRead = () => reads++;
        h.Tracker.NotifyInvalidate();
        h.Tracker.Dispose();
        h.Tracker.NotifyInvalidate();
        h.Tracker.FlushNow();
        h.Tracker.SetEnabled(false);
        clock.Advance(AgentOutputRegionTracker.PresenceDebounceDelay);
        Assert.Equal(0, reads);
        Assert.Equal(0, h.UpdateCount);
    }
}
