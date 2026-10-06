using System.Text;
using Ntilde.Platform.Ssh.Exec;

namespace Ntilde.Platform.Tests.Ssh.Exec;

/// <summary>
/// <see cref="BoundedChunkQueue"/>, the native exec channel's stdout (Phase 4 spec §8.3): every way one
/// side wakes the other, each driven from a real blocked thread.
/// </summary>
public sealed class BoundedChunkQueueTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    /// <summary>Runs <paramref name="work"/> on its own thread (never the pool), as the poll and mux read threads do.</summary>
    private static (Thread Thread, Task<T> Result) OnThread<T>(Func<T> work)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                result.TrySetResult(work());
            }
            catch (Exception ex)
            {
                result.TrySetException(ex);
            }
        })
        { IsBackground = true };
        thread.Start();
        return (thread, result.Task);
    }

    private static async Task UntilWaitingAsync(Thread thread)
    {
        DateTime deadline = DateTime.UtcNow + Bound;
        while (!thread.ThreadState.HasFlag(ThreadState.WaitSleepJoin))
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the thread to block.");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    private static string ReadText(BoundedChunkQueue queue, int count)
    {
        byte[] buffer = new byte[count];
        int read = queue.Read(buffer, CancellationToken.None);
        return Encoding.ASCII.GetString(buffer, 0, read);
    }

    [Fact]
    public void Reads_cross_chunk_boundaries_and_resume_inside_a_chunk()
    {
        var queue = new BoundedChunkQueue(100, 50);
        queue.Enqueue("abc"u8.ToArray(), CancellationToken.None);
        queue.Enqueue("de"u8.ToArray(), CancellationToken.None);

        Assert.Equal("abcd", ReadText(queue, 4));
        Assert.Equal("e", ReadText(queue, 4));
        Assert.Equal(0, queue.BufferedBytes);
    }

    [Fact]
    public async Task Enqueue_wakes_a_waiting_read()
    {
        var queue = new BoundedChunkQueue(100, 50);
        (Thread reader, Task<string> read) = OnThread(() => ReadText(queue, 16));
        await UntilWaitingAsync(reader);

        queue.Enqueue("hello"u8.ToArray(), CancellationToken.None);

        Assert.Equal("hello", await read.WaitAsync(Bound, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Complete_wakes_a_waiting_read_with_the_end_after_the_queue_drains()
    {
        var queue = new BoundedChunkQueue(100, 50);
        queue.Enqueue("tail"u8.ToArray(), CancellationToken.None);
        Assert.Equal("tail", ReadText(queue, 16));
        (Thread reader, Task<string> read) = OnThread(() => ReadText(queue, 16));
        await UntilWaitingAsync(reader);

        queue.Complete();

        Assert.Equal(string.Empty, await read.WaitAsync(Bound, TestContext.Current.CancellationToken));
        Assert.True(queue.TryRead(new byte[4], out int more));
        Assert.Equal(0, more);
    }

    [Fact]
    public async Task A_paused_writer_resumes_only_once_reads_reach_the_resume_threshold()
    {
        var queue = new BoundedChunkQueue(10, 4);
        (Thread writer, Task<bool> enqueue) = OnThread(() => queue.Enqueue(new byte[10], CancellationToken.None));
        await UntilWaitingAsync(writer);
        Assert.Equal(10, queue.BufferedBytes);

        Assert.Equal(5, queue.Read(new byte[5], CancellationToken.None));
        Assert.False(enqueue.IsCompleted, "5 unread is above the resume threshold of 4");

        Assert.Equal(1, queue.Read(new byte[1], CancellationToken.None));

        Assert.True(await enqueue.WaitAsync(Bound, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Abandon_frees_a_paused_writer_drops_later_chunks_and_keeps_queued_ones_readable()
    {
        var queue = new BoundedChunkQueue(4, 0);
        (Thread writer, Task<bool> enqueue) = OnThread(() => queue.Enqueue("full"u8.ToArray(), CancellationToken.None));
        await UntilWaitingAsync(writer);

        queue.Abandon();

        Assert.True(await enqueue.WaitAsync(Bound, TestContext.Current.CancellationToken));
        Assert.False(queue.Enqueue("late"u8.ToArray(), CancellationToken.None));
        Assert.Equal("full", ReadText(queue, 16));
    }

    [Fact]
    public async Task Release_frees_a_waiting_read_and_a_paused_writer_and_drops_what_is_queued()
    {
        var reading = new BoundedChunkQueue(4, 0);
        (Thread reader, Task<string> read) = OnThread(() => ReadText(reading, 16));
        await UntilWaitingAsync(reader);
        reading.Release();
        Assert.Equal(string.Empty, await read.WaitAsync(Bound, TestContext.Current.CancellationToken));
        Assert.True(reading.IsReleased);

        var writing = new BoundedChunkQueue(4, 0);
        (Thread writer, Task<bool> enqueue) = OnThread(() => writing.Enqueue("full"u8.ToArray(), CancellationToken.None));
        await UntilWaitingAsync(writer);
        writing.Release();
        Assert.True(await enqueue.WaitAsync(Bound, TestContext.Current.CancellationToken));
        Assert.Equal(0, writing.BufferedBytes);
        Assert.Equal(string.Empty, ReadText(writing, 16));
        Assert.False(writing.Enqueue("late"u8.ToArray(), CancellationToken.None));
    }

    [Fact]
    public async Task A_paused_writer_sees_its_stop_token_without_an_abandon()
    {
        var queue = new BoundedChunkQueue(4, 0);
        using var stop = new CancellationTokenSource();
        (Thread writer, Task<bool> enqueue) = OnThread(() => queue.Enqueue("full"u8.ToArray(), stop.Token));
        await UntilWaitingAsync(writer);

        await stop.CancelAsync();

        Assert.True(await enqueue.WaitAsync(Bound, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cancelling_a_waiting_read_wakes_it_with_OperationCanceledException()
    {
        var queue = new BoundedChunkQueue(100, 50);
        using var cancel = new CancellationTokenSource();
        (Thread reader, Task<int> read) = OnThread(() => queue.Read(new byte[16], cancel.Token));
        await UntilWaitingAsync(reader);

        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(Bound, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TryRead_reports_a_read_that_would_wait()
    {
        var queue = new BoundedChunkQueue(100, 50);

        Assert.False(queue.TryRead(new byte[4], out _));

        queue.Enqueue("x"u8.ToArray(), CancellationToken.None);
        Assert.True(queue.TryRead(new byte[4], out int read));
        Assert.Equal(1, read);
    }
}
