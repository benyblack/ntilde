using System.Text;
using Ntilde.Replay;
using Ntilde.Pty;
using Xunit;

namespace Ntilde.Platform.Tests.Replay;

public sealed class RecorderFailureTests
{
    private static PtyRecorder Create(Func<Stream> open, int budget = 4096) => new("test", 80, 24, "test", null, new RecorderSinkOptions(budget, open));

    [Fact]
    public async Task OpenFailureStopsProducersAndReturnsTypedError()
    {
        using var recorder = Create(() => throw new IOException("open failed"));
        var error = await Assert.ThrowsAsync<RecordingException>(recorder.WaitUntilReadyAsync);
        Assert.Equal(RecordingFailureKind.Open, error.Failure.Kind);
        for (int i = 0; i < 1000; i++) recorder.RecordChunk(new byte[4096], 4096);
        Assert.Equal(0, recorder.PendingBytes);
        Assert.False(recorder.IsAccepting);
        await Assert.ThrowsAsync<RecordingException>(recorder.CompleteAsync);
    }

    [Fact]
    public async Task MidWriteFailureNotifiesOnceAndReleasesPendingEvents()
    {
        var sink = new ControlledStream { FailRecord = true };
        using var recorder = Create(() => sink);
        var failed = new TaskCompletionSource<RecordingFailure>(TaskCreationOptions.RunContinuationsAsynchronously);
        int notifications = 0;
        recorder.Failed += failure => { Interlocked.Increment(ref notifications); failed.TrySetResult(failure); };
        await recorder.WaitUntilReadyAsync();
        recorder.RecordChunk(Encoding.UTF8.GetBytes("one"), 3);
        Assert.Equal(RecordingFailureKind.Write, (await failed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).Kind);
        await recorder.Completion;
        recorder.RecordMarker("after failure");
        recorder.RecordResize(10, 10);
        Assert.Equal(0, recorder.PendingBytes);
        Assert.Equal(1, notifications);
        await Assert.ThrowsAsync<RecordingException>(recorder.CompleteAsync);
    }

    [Fact]
    public async Task SlowWriterCountsInflightBytesAndFailsWithoutWaitingOnDisk()
    {
        var sink = new ControlledStream { BlockRecord = true };
        using var recorder = Create(() => sink, 1024);
        try
        {
            await recorder.WaitUntilReadyAsync();
            recorder.RecordChunk(new byte[100], 100);
            await sink.RecordEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            long inflight = recorder.PendingBytes;
            Assert.InRange(inflight, 150, 1024);
            for (int i = 0; i < 1000 && recorder.Failure is null; i++)
            {
                recorder.RecordChunk(new byte[100], 100);
                Assert.InRange(recorder.PendingBytes, 1, 1024);
            }
            Assert.Equal(RecordingFailureKind.Overflow, recorder.Failure!.Kind);
            Assert.InRange(recorder.PendingBytes, 1, 1024);
            Assert.False(recorder.Completion.IsCompleted);
            recorder.RecordChunk(new byte[100000], 100000);
            Assert.InRange(recorder.PendingBytes, 1, 1024);
        }
        finally { sink.Release.TrySetResult(); }
        await Assert.ThrowsAsync<RecordingException>(recorder.CompleteAsync);
        Assert.Equal(0, recorder.PendingBytes);
    }

    [Fact]
    public async Task HugeSingleEventFailsBeforeEncodingAndDoesNotGrowQueue()
    {
        using var recorder = Create(() => new MemoryStream(), 1024);
        await recorder.WaitUntilReadyAsync();
        recorder.RecordChunk(new byte[100000], 100000);
        Assert.Equal(RecordingFailureKind.Overflow, recorder.Failure!.Kind);
        Assert.Equal(0, recorder.PendingBytes);
        await Assert.ThrowsAsync<RecordingException>(recorder.CompleteAsync);
    }

    [Fact]
    public async Task LargeMarkerAndCheckpointAreSubjectToSameBudget()
    {
        using var marker = Create(() => new MemoryStream(), 1024);
        await marker.WaitUntilReadyAsync();
        marker.RecordMarker(new string('x', 10000));
        Assert.Equal(RecordingFailureKind.Overflow, marker.Failure!.Kind);
        using var snapshot = Create(() => new MemoryStream(), 1024);
        await snapshot.WaitUntilReadyAsync();
        snapshot.RecordSnapshot(new Ntilde.VT.TerminalBuffer(80, 24));
        Assert.Equal(RecordingFailureKind.Overflow, snapshot.Failure!.Kind);
        Assert.Equal(0, snapshot.PendingBytes);
    }

    [Fact]
    public async Task SuccessfulCompletionIsLosslessAndIdempotent()
    {
        var sink = new ControlledStream();
        using var recorder = Create(() => sink, 65536);
        await recorder.WaitUntilReadyAsync();
        for (int i = 0; i < 50; i++) recorder.RecordChunkAt(i, Encoding.UTF8.GetBytes("line"), 4);
        await Task.WhenAll(recorder.CompleteAsync(), recorder.CompleteAsync());
        Assert.Null(recorder.Failure);
        Assert.Equal(0, recorder.PendingBytes);
        Assert.Equal(51, Encoding.UTF8.GetString(sink.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public async Task HeaderFailureAndFinalFlushFailureAreVisible()
    {
        using var header = Create(() => new ControlledStream { FailHeader = true });
        Assert.Equal(RecordingFailureKind.Open, (await Assert.ThrowsAsync<RecordingException>(header.WaitUntilReadyAsync)).Failure.Kind);
        using var final = Create(() => new ControlledStream { FailFinalFlush = true });
        await final.WaitUntilReadyAsync();
        Assert.Equal(RecordingFailureKind.Write, (await Assert.ThrowsAsync<RecordingException>(final.CompleteAsync)).Failure.Kind);
    }

    [Fact]
    public async Task FlushDeadlineFailsVisiblyAndConcurrentCompletionDoesNotDisposeLiveToken()
    {
        var sink = new ControlledStream { BlockRecord = true };
        using var recorder = new PtyRecorder("test", 80, 24, "test", null, new RecorderSinkOptions(4096, () => sink, FlushTimeoutMs: 50));
        try
        {
            await recorder.WaitUntilReadyAsync();
            recorder.RecordMarker("wait");
            await sink.RecordEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var errors = await Task.WhenAll(
                Assert.ThrowsAsync<RecordingException>(recorder.CompleteAsync),
                Assert.ThrowsAsync<RecordingException>(recorder.CompleteAsync));
            Assert.All(errors, ex => Assert.Equal(RecordingFailureKind.FlushTimeout, ex.Failure.Kind));
            Assert.False(recorder.IsAccepting);
        }
        finally { sink.Release.TrySetResult(); }
        await recorder.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(0, recorder.PendingBytes);
    }

    [Fact]
    public void FrozenFlightExportLargerThanLiveQueueIsComplete()
    {
        string path = Path.GetTempFileName();
        try
        {
            var ring = new FlightRecordingBuffer(32 * 1024 * 1024, 80, 24);
            byte[] data = new byte[1024 * 1024];
            for (int i = 0; i < 20; i++) ring.RecordChunk(data, data.Length);
            FlightExportInfo info = ring.ExportTo(path, "test");
            Assert.Equal(20, info.EventCount);
            Assert.Equal(21, File.ReadLines(path).Count());
            Assert.True(new FileInfo(path).Length > PtyRecorder.DefaultMaxPendingBytes);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task SharedSessionLifecycleConfirmsReadinessAndReturnsTypedStopFailure()
    {
        using var state = new SessionRecording();
        int creations = 0;
        using PtyRecorder recorder = state.Begin(() => { creations++; return Create(() => new ControlledStream { FailFinalFlush = true }); }, _ => { });
        await recorder.WaitUntilReadyAsync();
        Assert.Equal(recorder, state.Begin(() => throw new InvalidOperationException("already active"), _ => { }));
        Assert.Equal(1, creations);
        await Assert.ThrowsAsync<RecordingException>(state.StopAsync);
        Assert.NotNull(recorder.Failure);
        Assert.Null(state.Current);
        state.Dispose();
    }

    [Fact]
    public async Task SharedSessionLifecycleRejectsDelayedFailureFromReplacedRecorder()
    {
        using var state = new SessionRecording();
        using var release = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int notifications = 0;
        using PtyRecorder first = state.Begin(() =>
        {
            var recorder = Create(() => new MemoryStream(), 1024);
            recorder.Failed += _ => { entered.TrySetResult(); release.Wait(TestContext.Current.CancellationToken); };
            return recorder;
        }, _ => Interlocked.Increment(ref notifications));
        first.Failed += _ => delivered.TrySetResult();
        PtyRecorder? second = null;
        try
        {
            await first.WaitUntilReadyAsync();
            first.RecordChunk(new byte[10000], 10000);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            second = state.Begin(() => Create(() => new MemoryStream()), _ => Interlocked.Increment(ref notifications));
            await second.WaitUntilReadyAsync();
            release.Set();
            await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(0, notifications);
            Assert.True(state.Current!.IsReady);
            await state.StopAsync();
        }
        finally { release.Set(); second?.Dispose(); }
    }

    private sealed class ControlledStream : MemoryStream
    {
        public bool FailRecord { get; init; }
        public bool FailHeader { get; init; }
        public bool FailFinalFlush { get; init; }
        private int _flushes;
        public override Task FlushAsync(CancellationToken token)
        {
            if (Interlocked.Increment(ref _flushes) > 1 && FailFinalFlush) throw new IOException("flush failed");
            return base.FlushAsync(token);
        }
        public bool BlockRecord { get; init; }
        public TaskCompletionSource RecordEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writes;
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token = default)
        {
            int write = Interlocked.Increment(ref _writes);
            if (write == 1 && FailHeader) throw new IOException("header failed");
            if (write > 1)
            {
                RecordEntered.TrySetResult();
                if (BlockRecord)
                {
                    // Model a sink that cannot cancel an in-flight write. Its retained bytes stay charged.
                    await Release.Task;
                    token.ThrowIfCancellationRequested();
                }
                if (FailRecord) throw new IOException("disk full");
            }
            await base.WriteAsync(bytes, token);
        }
    }
}
