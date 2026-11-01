using System.Text;
using System.Text.Json;
using Ntilde.Replay;
using Ntilde.VT;
using Xunit;

namespace Ntilde.Tests.ReplayTests;

public sealed class ReplayRunnerCancellationTests
{
    [Theory]
    [InlineData(false, "data")]
    [InlineData(true, "data")]
    [InlineData(false, "resize")]
    [InlineData(false, "marker")]
    [InlineData(false, "input")]
    [InlineData(false, "snapshot")]
    [InlineData(false, "sixelCellSize")]
    public async Task CancellationDuringRealtimeGap_ReturnsPartialResultAndReleasesReader(bool legacy, string eventKind)
    {
        string path = Path.GetTempFileName();
        var clock = new PausedTimeProvider();
        using var cancellation = new CancellationTokenSource();
        Task<ReplayRunResult>? run = null;
        PausedTimer? timer = null;
        try
        {
            var lines = new List<string>();
            if (!legacy) lines.Add(JsonSerializer.Serialize(new ReplayHeader { Type = ReplayHeader.TypeToken, Cols = 80, Rows = 24 }, ReplayJsonContext.Default.ReplayHeader));
            lines.Add("{\"t\":0,\"type\":\"data\",\"d\":\"QQ==\"}");
            lines.Add(JsonSerializer.Serialize(new ReplayEvent
            {
                TimeOffsetMs = 60000,
                Type = eventKind,
                Data = "Qg==",
                Input = "fallback",
                MarkerName = "later",
                Cols = 100,
                Rows = 30,
                Snapshot = new ReplaySnapshot { Cols = 80, Rows = 24 },
                SixelCellWidthPx = 8,
                SixelCellHeightPx = 16
            }, ReplayJsonContext.Default.ReplayEvent));
            await File.WriteAllLinesAsync(path, lines, TestContext.Current.CancellationToken);
            var data = new List<string>();
            var times = new List<long>();
            int laterCallbacks = 0;
            run = new ReplayRunner(path, clock).RunWithResultAsync(
                bytes => { data.Add(Encoding.UTF8.GetString(bytes)); return Task.CompletedTask; },
                onResizeCallback: (_, _) => { laterCallbacks++; return Task.CompletedTask; },
                onMarkerCallback: _ => { laterCallbacks++; return Task.CompletedTask; },
                onInputCallback: _ => { laterCallbacks++; return Task.CompletedTask; },
                onSnapshotCallback: _ => { laterCallbacks++; return Task.CompletedTask; },
                onSixelCellSizeCallback: (_, _) => { laterCallbacks++; return Task.CompletedTask; },
                onTimeUpdate: time => { times.Add(time); return Task.CompletedTask; },
                options: new ReplayRunOptions { PlaybackMode = ReplayPlaybackMode.Realtime, ApplyHeaderResize = false }, ct: cancellation.Token);
            // The timer has been scheduled, but never fires. Completion can only come
            // from cancellation, independently of machine speed or a wall-clock gap.
            timer = await clock.Scheduled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            cancellation.Cancel();
            ReplayRunResult result = await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(new[] { "A" }, data);
            Assert.Equal(0, laterCallbacks);
            Assert.Equal(new long[] { 0 }, times);
            Assert.Equal(1, result.EventsProcessed);
            Assert.Equal(0, result.LastGoodOffset);
            Assert.False(result.Truncated);
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally
        {
            cancellation.Cancel();
            timer?.Fire(); // Release a broken implementation too, so a failing test leaks no run.
            if (run is not null) await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(ReplayPlaybackMode.Virtual)]
    [InlineData(ReplayPlaybackMode.Realtime)]
    public async Task UncancelledPlayback_AdvancesAndCompletes(ReplayPlaybackMode mode)
    {
        string path = Path.GetTempFileName();
        var clock = new PausedTimeProvider();
        Task<ReplayRunResult>? run = null;
        PausedTimer? timer = null;
        try
        {
            await File.WriteAllLinesAsync(path, new[] { "{\"t\":0,\"d\":\"QQ==\"}", "{\"t\":60000,\"d\":\"Qg==\"}" }, TestContext.Current.CancellationToken);
            var data = new List<byte>();
            run = new ReplayRunner(path, clock).RunWithResultAsync(bytes => { data.AddRange(bytes); return Task.CompletedTask; },
                options: new ReplayRunOptions { PlaybackMode = mode }, ct: TestContext.Current.CancellationToken);
            if (mode == ReplayPlaybackMode.Realtime)
            {
                timer = await clock.Scheduled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                timer.Fire();
            }
            ReplayRunResult result = await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal("AB", Encoding.UTF8.GetString(data.ToArray()));
            Assert.Equal(2, result.EventsProcessed);
            Assert.Equal(60000, result.LastGoodOffset);
            if (mode == ReplayPlaybackMode.Virtual) Assert.False(clock.Scheduled.Task.IsCompleted);
        }
        finally
        {
            timer?.Fire();
            if (run is not null) await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationFromData_DoesNotPublishLaterTime(bool legacy)
    {
        string path = Path.GetTempFileName();
        using var cancellation = new CancellationTokenSource();
        try
        {
            string header = legacy ? "" : JsonSerializer.Serialize(new ReplayHeader { Type = ReplayHeader.TypeToken, Cols = 80, Rows = 24 }, ReplayJsonContext.Default.ReplayHeader) + "\n";
            await File.WriteAllTextAsync(path, header + "{\"t\":0,\"type\":\"data\",\"d\":\"QQ==\"}\n", TestContext.Current.CancellationToken);
            int times = 0;
            ReplayRunResult result = await new ReplayRunner(path).RunWithResultAsync(_ => { cancellation.Cancel(); return Task.CompletedTask; },
                onTimeUpdate: _ => { times++; return Task.CompletedTask; }, ct: cancellation.Token);
            Assert.Equal(1, result.EventsProcessed);
            Assert.Equal(0, times);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task CancelledInputCallback_DoesNotInvokeLegacyFallback()
    {
        string path = Path.GetTempFileName();
        using var cancellation = new CancellationTokenSource();
        try
        {
            await File.WriteAllLinesAsync(path, new[]
            {
                JsonSerializer.Serialize(new ReplayHeader { Type = ReplayHeader.TypeToken, Cols = 80, Rows = 24 }, ReplayJsonContext.Default.ReplayHeader),
                "{\"t\":0,\"type\":\"input\",\"d\":\"QQ==\",\"i\":\"fallback\"}"
            }, TestContext.Current.CancellationToken);
            int callbacks = 0;
            ReplayRunResult result = await new ReplayRunner(path).RunWithResultAsync(_ => Task.CompletedTask,
                onInputCallback: _ => { callbacks++; cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); }, ct: cancellation.Token);
            Assert.Equal(1, callbacks);
            Assert.Equal(0, result.EventsProcessed);
            Assert.False(result.Truncated);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task UnrelatedCancellationException_StillPropagates()
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "{\"t\":0,\"d\":\"QQ==\"}\n", TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<OperationCanceledException>(() => new ReplayRunner(path).RunWithResultAsync(
                _ => throw new OperationCanceledException("callback failure"), ct: TestContext.Current.CancellationToken));
        }
        finally { File.Delete(path); }
    }

    private sealed class PausedTimeProvider : TimeProvider
    {
        public TaskCompletionSource<PausedTimer> Scheduled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new PausedTimer(callback, state);
            Scheduled.TrySetResult(timer);
            return timer;
        }
    }

    private sealed class PausedTimer(TimerCallback callback, object? state) : ITimer
    {
        public bool IsDisposed { get; private set; }
        public bool Change(TimeSpan dueTime, TimeSpan period) => !IsDisposed;
        public void Fire() { if (!IsDisposed) callback(state); }
        public void Dispose() => IsDisposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
