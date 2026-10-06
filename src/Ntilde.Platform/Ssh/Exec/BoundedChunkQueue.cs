namespace Ntilde.Platform.Ssh.Exec;

/// <summary>
/// A native exec channel's stdout between its poll thread (the one writer) and the stream's reader
/// (Phase 4 spec §8.3): a FIFO of byte chunks with a byte budget, under one lock. Each side wakes the
/// other directly with <see cref="Monitor.PulseAll(object)"/>. No thread-pool work is involved, so a
/// reader blocked in <see cref="Read"/> (the mux client's read thread) gets each chunk when the poll
/// thread queues it, even while the pool is starved.
/// </summary>
/// <remarks>
/// <para>
/// Backpressure: once the queue holds the pause threshold, <see cref="Enqueue"/> waits and the poll
/// thread stops polling. It resumes when the reader has brought the queue down to the resume
/// threshold. The gap between the two keeps the writer from waking on every read.
/// </para>
/// <para>
/// Wake-ups. A state change wakes both sides, and each re-checks its own condition:
/// <list type="bullet">
///   <item><description><see cref="Enqueue"/> wakes a reader waiting for data.</description></item>
///   <item><description>A read that brings the queue to the resume threshold wakes a paused writer.</description></item>
///   <item><description><see cref="Complete"/> wakes a reader, which then drains the queue and ends.</description></item>
///   <item><description><see cref="Abandon"/> wakes a paused writer, which then drops every later chunk.</description></item>
///   <item><description><see cref="Release"/> wakes both: reads return 0 and the writer drops its chunks.</description></item>
///   <item><description>Cancelling a read's token wakes that read, which then throws.</description></item>
/// </list>
/// A paused writer also wakes every <see cref="WriterWaitSlice"/> to check its stop token, so a stop
/// that comes without <see cref="Abandon"/> still ends the wait.
/// </para>
/// </remarks>
internal sealed class BoundedChunkQueue
{
    /// <summary>How often a paused writer checks its stop token.</summary>
    internal static readonly TimeSpan WriterWaitSlice = TimeSpan.FromMilliseconds(50);

    private readonly object _gate = new();
    private readonly Queue<byte[]> _chunks = new();
    private readonly long _pauseThresholdBytes;
    private readonly long _resumeThresholdBytes;
    private long _bytes;        // unread bytes, the head's consumed part excluded
    private int _headOffset;    // bytes of the head chunk already read
    private bool _paused;       // the writer waits for the reader to reach the resume threshold
    private bool _completed;    // the writer is done
    private bool _abandoned;    // the writer drops what it is given
    private volatile bool _released;

    public BoundedChunkQueue(long pauseThresholdBytes, long resumeThresholdBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pauseThresholdBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(resumeThresholdBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(resumeThresholdBytes, pauseThresholdBytes);

        _pauseThresholdBytes = pauseThresholdBytes;
        _resumeThresholdBytes = resumeThresholdBytes;
    }

    /// <summary>Reads end at once and return 0: <see cref="Release"/> was called.</summary>
    public bool IsReleased => _released;

    /// <summary>Unread bytes, for tests.</summary>
    internal long BufferedBytes
    {
        get { lock (_gate) return _bytes; }
    }

    /// <summary>
    /// Writer: queues <paramref name="chunk"/>, which the queue now owns, and wakes the reader. Once
    /// the pause threshold is reached, it waits until the reader catches up, the queue is abandoned
    /// or released, or <paramref name="stop"/> is cancelled.
    /// </summary>
    /// <returns><see langword="false"/> when the chunk was dropped because nobody will read it.</returns>
    public bool Enqueue(byte[] chunk, CancellationToken stop)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        lock (_gate)
        {
            if (_abandoned)
            {
                return false;
            }

            if (chunk.Length > 0)
            {
                _chunks.Enqueue(chunk);
                _bytes += chunk.Length;
                Monitor.PulseAll(_gate);
            }

            if (_bytes >= _pauseThresholdBytes)
            {
                _paused = true;
            }

            while (_paused && !_abandoned && !stop.IsCancellationRequested)
            {
                Monitor.Wait(_gate, WriterWaitSlice);
            }

            _paused = false;
            return true;
        }
    }

    /// <summary>Writer: there is nothing more. A reader drains what is queued, then reads end.</summary>
    public void Complete()
    {
        lock (_gate)
        {
            _completed = true;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// Nobody will read what comes next: the writer stops waiting and drops later chunks. What is
    /// already queued stays readable.
    /// </summary>
    public void Abandon()
    {
        lock (_gate)
        {
            _abandoned = true;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// The reader is gone: the queued chunks are dropped, a read in progress and every later read
    /// return 0, and the writer stops waiting and drops later chunks.
    /// </summary>
    public void Release()
    {
        lock (_gate)
        {
            _released = true;
            _abandoned = true;
            _chunks.Clear();
            _bytes = 0;
            _headOffset = 0;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// Reader: copies up to <paramref name="destination"/>'s length of queued bytes and returns the
    /// count, waiting for a chunk when the queue is empty. Returns 0 once the queue is completed and
    /// drained, or released.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled while it waited.</exception>
    public int Read(Span<byte> destination, CancellationToken cancellationToken)
    {
        if (destination.IsEmpty)
        {
            return 0;
        }

        CancellationTokenRegistration registration = cancellationToken.CanBeCanceled
            ? cancellationToken.UnsafeRegister(static state => ((BoundedChunkQueue)state!).WakeAll(), this)
            : default;
        try
        {
            lock (_gate)
            {
                while (true)
                {
                    if (_released)
                    {
                        return 0;
                    }

                    if (_bytes > 0)
                    {
                        return Take(destination);
                    }

                    if (_completed)
                    {
                        return 0;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    Monitor.Wait(_gate);
                }
            }
        }
        finally
        {
            // Outside the lock: disposing waits for a callback in flight, and that callback takes the lock.
            registration.Dispose();
        }
    }

    /// <summary>
    /// Reader: <see cref="Read"/> without the wait. It returns <see langword="false"/> when a read
    /// would have to wait, and otherwise <see langword="true"/> with the count, which is 0 at the end.
    /// </summary>
    public bool TryRead(Span<byte> destination, out int read)
    {
        lock (_gate)
        {
            if (destination.IsEmpty || _released)
            {
                read = 0;
                return true;
            }

            if (_bytes > 0)
            {
                read = Take(destination);
                return true;
            }

            read = 0;
            return _completed;
        }
    }

    /// <summary>Under the lock, with bytes queued: copies across chunks and wakes a paused writer at the resume threshold.</summary>
    private int Take(Span<byte> destination)
    {
        int copied = 0;
        while (copied < destination.Length && _chunks.Count > 0)
        {
            byte[] head = _chunks.Peek();
            int count = Math.Min(head.Length - _headOffset, destination.Length - copied);
            head.AsSpan(_headOffset, count).CopyTo(destination[copied..]);
            copied += count;
            _headOffset += count;
            if (_headOffset == head.Length)
            {
                _chunks.Dequeue();
                _headOffset = 0;
            }
        }

        _bytes -= copied;
        if (_paused && _bytes <= _resumeThresholdBytes)
        {
            _paused = false;
            Monitor.PulseAll(_gate);
        }

        return copied;
    }

    private void WakeAll()
    {
        lock (_gate)
        {
            Monitor.PulseAll(_gate);
        }
    }
}
