using Ntilde.Shell.Mux.Remote;

namespace Ntilde.Tests.Shell.Mux.Remote;

/// <summary>
/// <see cref="MuxReconnectLoop"/> (Phase 4 spec §7.3) on a clock the test advances: jittered backoff with a
/// 30 s ceiling, a 10-minute budget, and a user's attempt that goes at once and restarts the backoff.
/// </summary>
public sealed class MuxReconnectLoopTests
{
    private readonly FakeMuxTimerScheduler _clock = new();
    private int _attempts;
    private int _abandoned;
    private Func<Task<bool>> _outcome = () => Task.FromResult(false);

    private int Attempts => Volatile.Read(ref _attempts);

    private MuxReconnectLoop Loop(int seed = 7) => new(
        _clock,
        () =>
        {
            Interlocked.Increment(ref _attempts);
            return _outcome();
        },
        () => Interlocked.Increment(ref _abandoned),
        new Random(seed));

    /// <summary>Advances in 100 ms steps until the loop has made <paramref name="attempts"/> attempts.</summary>
    private void AdvanceUntilAttempt(int attempts)
    {
        for (int i = 0; i < 10_000 && Attempts < attempts; i++) _clock.Advance(100);
        Assert.Equal(attempts, Attempts);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(4, 16)]
    [InlineData(5, 30)]
    [InlineData(6, 30)]
    [InlineData(7, 30)]
    [InlineData(8, 30)]
    public void Delay_follows_the_backoff_and_stays_within_jitter(int attempt, int baseSeconds)
    {
        var jitter = new Random(attempt * 31 + 1);
        TimeSpan low = TimeSpan.FromSeconds(baseSeconds * 0.8);
        TimeSpan high = TimeSpan.FromSeconds(Math.Min(baseSeconds * 1.2, 30));

        TimeSpan[] delays = Enumerable.Range(0, 200).Select(_ => MuxReconnectLoop.Delay(attempt, jitter)).ToArray();

        Assert.All(delays, d => Assert.InRange(d, low, high));
        Assert.True(delays.Distinct().Count() > 1, "the delay is jittered, not fixed");
        Assert.All(delays, d => Assert.True(d <= TimeSpan.FromSeconds(30), $"{d} is over the 30 s ceiling"));
    }

    [Fact]
    public void The_first_attempt_waits_about_a_second_and_each_failure_backs_off()
    {
        using MuxReconnectLoop loop = Loop();

        loop.Start();

        Assert.True(loop.IsRunning);
        Assert.InRange(_clock.NextDueIn!.Value, TimeSpan.FromSeconds(0.8), TimeSpan.FromSeconds(1.2));
        _clock.Advance(_clock.NextDueIn!.Value);
        Assert.Equal(1, Attempts);
        Assert.InRange(_clock.NextDueIn!.Value, TimeSpan.FromSeconds(1.6), TimeSpan.FromSeconds(2.4));
        Assert.Equal(1, _clock.PendingCount);   // one timer at a time
    }

    [Fact]
    public void Loop_stops_after_the_budget_and_raises_abandoned()
    {
        using MuxReconnectLoop loop = Loop();
        loop.Start();

        _clock.Advance(MuxReconnectLoop.Budget - TimeSpan.FromMilliseconds(1));

        Assert.True(loop.IsRunning);
        Assert.Equal(0, Volatile.Read(ref _abandoned));
        int attemptsWithinBudget = Attempts;
        Assert.InRange(attemptsWithinBudget, 20, 30);   // 1+2+4+8+16 s, then every 24-30 s, for 10 minutes

        _clock.Advance(1);   // the last attempt lands on the budget's end, and fails

        Assert.Equal(1, Volatile.Read(ref _abandoned));
        Assert.False(loop.IsRunning);
        Assert.Equal(attemptsWithinBudget + 1, Attempts);
        Assert.Equal(0, _clock.PendingCount);

        _clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(attemptsWithinBudget + 1, Attempts);
        Assert.Equal(1, Volatile.Read(ref _abandoned));
    }

    [Fact]
    public void TryNow_attempts_immediately_and_resets_the_backoff()
    {
        using MuxReconnectLoop loop = Loop();
        loop.Start();
        AdvanceUntilAttempt(4);   // the next wait is now 16 s, jittered
        Assert.True(_clock.NextDueIn > TimeSpan.FromSeconds(12));

        loop.TryNow();

        Assert.Equal(5, Attempts);   // at once: no time passed
        Assert.Equal(1, _clock.PendingCount);   // the 16 s wait was cancelled
        Assert.InRange(_clock.NextDueIn!.Value, TimeSpan.FromSeconds(0.8), TimeSpan.FromSeconds(1.2));
        _clock.Advance(1200);
        Assert.Equal(6, Attempts);
    }

    [Fact]
    public void TryNow_during_an_attempt_does_not_start_another_but_still_resets_the_backoff()
    {
        var running = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using MuxReconnectLoop loop = Loop();
        loop.Start();
        AdvanceUntilAttempt(4);
        _outcome = () => running.Task;
        _clock.Advance(_clock.NextDueIn!.Value);
        Assert.Equal(5, Attempts);

        loop.TryNow();
        Assert.Equal(5, Attempts);

        _outcome = () => Task.FromResult(false);
        running.SetResult(false);
        SpinWait.SpinUntil(() => _clock.PendingCount == 1, TimeSpan.FromSeconds(10));
        Assert.InRange(_clock.NextDueIn!.Value, TimeSpan.FromSeconds(0.8), TimeSpan.FromSeconds(1.2));
    }

    [Fact]
    public void A_successful_attempt_stops_the_loop_without_abandoning()
    {
        using MuxReconnectLoop loop = Loop();
        loop.Start();
        AdvanceUntilAttempt(2);

        _outcome = () => Task.FromResult(true);
        _clock.Advance(_clock.NextDueIn!.Value);

        Assert.Equal(3, Attempts);
        Assert.False(loop.IsRunning);
        Assert.Equal(0, _clock.PendingCount);
        _clock.Advance(MuxReconnectLoop.Budget);
        Assert.Equal(0, Volatile.Read(ref _abandoned));
    }

    [Fact]
    public void Start_is_idempotent_while_running_and_restarts_the_budget_after_a_stop()
    {
        using MuxReconnectLoop loop = Loop();
        loop.Start();
        AdvanceUntilAttempt(3);

        loop.Start();   // already running: nothing changes
        Assert.Equal(1, _clock.PendingCount);
        Assert.True(_clock.NextDueIn > TimeSpan.FromSeconds(3));

        loop.Stop();
        Assert.False(loop.IsRunning);
        Assert.Equal(0, _clock.PendingCount);
        loop.Start();
        Assert.InRange(_clock.NextDueIn!.Value, TimeSpan.FromSeconds(0.8), TimeSpan.FromSeconds(1.2));
        Assert.Equal(0, Volatile.Read(ref _abandoned));
    }

    [Fact]
    public void A_throwing_attempt_counts_as_a_failure()
    {
        using MuxReconnectLoop loop = Loop();
        _outcome = () => throw new InvalidOperationException("boom");
        loop.Start();

        AdvanceUntilAttempt(2);

        Assert.True(loop.IsRunning);
    }

    [Fact]
    public void TryNow_while_stopped_and_anything_after_Dispose_does_nothing()
    {
        MuxReconnectLoop loop = Loop();
        loop.TryNow();
        Assert.Equal(0, Attempts);

        loop.Start();
        loop.Dispose();
        loop.Start();
        loop.TryNow();
        _clock.Advance(TimeSpan.FromHours(1));

        Assert.Equal(0, Attempts);
        Assert.False(loop.IsRunning);
        Assert.Equal(0, _clock.PendingCount);
        Assert.Equal(0, Volatile.Read(ref _abandoned));
    }
}
