using Ntilde.VT;

namespace Ntilde.VT.Tests;

/// <summary>
/// #658: a batch writer gives way to readers already waiting before it takes the write lock again.
/// </summary>
/// <remarks>
/// During an output flood the parser takes batch after batch, and ReaderWriterLockSlim does not hand a released lock to
/// a waiting reader: the next batch re-took it before the woken render thread ran, so rendering got about one frame in
/// five. These pin the handoff itself; the frame rate under a flood is RenderFrameTimeBenchmarks' to measure.
/// </remarks>
public class ReaderHandoffTests
{
    [Fact]
    public void AReaderWaitingThroughABatch_GetsInBeforeTheNextBatch()
    {
        var buffer = new TerminalBuffer(10, 3)
        {
            // Long enough that a loaded runner's scheduling cannot decide the outcome: the handoff ends when the
            // reader is in, not at the limit.
            ReaderHandoffLimit = TimeSpan.FromSeconds(10),
        };
        using var readerIn = new ManualResetEventSlim();

        buffer.EnterBatchWrite();
        var reader = new Thread(() =>
        {
            // The flag is set inside the read lock, and the next batch takes the write lock only once the reader is
            // out: if the batch gave way, the flag is set by the time it holds the lock.
            buffer.Lock.EnterReadLock();
            readerIn.Set();
            buffer.Lock.ExitReadLock();
        })
        { IsBackground = true };
        reader.Start();
        Assert.True(SpinWait.SpinUntil(() => buffer.Lock.WaitingReadCount == 1, TimeSpan.FromSeconds(5)), "setup: the reader never waited");

        buffer.ExitBatchWrite();
        buffer.EnterBatchWrite(); // the next batch, at once, as the parser's flood loop takes it
        bool readerWentFirst = readerIn.IsSet;
        buffer.ExitBatchWrite();
        reader.Join();

        Assert.True(readerWentFirst, "the next batch re-took the write lock before the waiting reader got in");
    }

    [Fact]
    public void WithNoReaderWaiting_ABatchTakesTheLockAtOnce()
    {
        var buffer = new TerminalBuffer(10, 3) { ReaderHandoffLimit = TimeSpan.FromSeconds(10) };
        var clock = System.Diagnostics.Stopwatch.StartNew();

        for (int i = 0; i < 1000; i++)
        {
            buffer.EnterBatchWrite();
            buffer.ExitBatchWrite();
        }

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"1000 uncontended batches took {clock.Elapsed}");
    }

    [Fact]
    public void AReaderThatNeverGetsIn_HoldsABatchBackOnlyForTheLimit()
    {
        // A reader that cannot enter (another writer holds the lock) must not keep the batch from even queueing for it.
        var buffer = new TerminalBuffer(10, 3) { ReaderHandoffLimit = TimeSpan.FromMilliseconds(50) };
        CancellationToken cancel = TestContext.Current.CancellationToken;
        using var holderIn = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            buffer.Lock.EnterWriteLock();
            holderIn.Set();
            try
            {
                release.Wait(cancel);
            }
            catch (OperationCanceledException)
            {
                // The run was cancelled: let go anyway. Thrown out of a thread of its own, this would end the process.
            }
            finally
            {
                buffer.Lock.ExitWriteLock();
            }
        })
        { IsBackground = true };
        holder.Start();
        holderIn.Wait(cancel);

        Thread? reader = null;
        Thread? batch = null;
        try
        {
            reader = new Thread(() =>
            {
                buffer.Lock.EnterReadLock();
                buffer.Lock.ExitReadLock();
            })
            { IsBackground = true };
            reader.Start();
            Assert.True(SpinWait.SpinUntil(() => buffer.Lock.WaitingReadCount == 1, TimeSpan.FromSeconds(5)), "setup: the reader never waited");

            batch = new Thread(() =>
            {
                buffer.EnterBatchWrite();
                buffer.ExitBatchWrite();
            })
            { IsBackground = true };
            batch.Start();

            // Past the limit the batch queues for the lock as a waiting writer.
            Assert.True(SpinWait.SpinUntil(() => buffer.Lock.WaitingWriteCount == 1, TimeSpan.FromSeconds(5)), "the batch never queued for the write lock");
        }
        finally
        {
            // Whichever assertion failed, the holder lets go, so no thread is left blocked on the lock (CodeRabbit, #662).
            release.Set();
        }

        Assert.True(batch.Join(TimeSpan.FromSeconds(5)));
        Assert.True(reader.Join(TimeSpan.FromSeconds(5)));
        Assert.True(holder.Join(TimeSpan.FromSeconds(5)));
    }
}
