namespace Ntilde.Mux.Transport;

/// <summary>
/// A connected pair of blocking, bounded, in-memory streams. Synchronous by design: the mux reads
/// and writes on dedicated threads, and a bounded pipe is what lets a test stand in for a client
/// that stops reading. Disposing an end closes both of its directions and unblocks any thread
/// parked in that end's Read or Write.
/// </summary>
public static class InMemoryDuplexPipe
{
    public static (Stream A, Stream B) Create(int capacityBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityBytes);
        var aToB = new ByteRingPipe(capacityBytes);
        var bToA = new ByteRingPipe(capacityBytes);
        return (new DuplexEnd(read: bToA, write: aToB), new DuplexEnd(read: aToB, write: bToA));
    }

    private sealed class ByteRingPipe
    {
        private readonly object _gate = new();
        private readonly byte[] _ring;
        private int _head;
        private int _count;
        private bool _writerClosed;
        private bool _readerClosed;

        public ByteRingPipe(int capacity) => _ring = new byte[capacity];

        public int Read(Span<byte> destination)
        {
            if (destination.IsEmpty) return 0;
            lock (_gate)
            {
                while (_count == 0)
                {
                    if (_writerClosed || _readerClosed) return 0;
                    Monitor.Wait(_gate);
                }

                if (_readerClosed) return 0;
                int n = Math.Min(destination.Length, _count);
                int first = Math.Min(n, _ring.Length - _head);
                _ring.AsSpan(_head, first).CopyTo(destination);
                if (n > first) _ring.AsSpan(0, n - first).CopyTo(destination[first..]);
                _head = (_head + n) % _ring.Length;
                _count -= n;
                Monitor.PulseAll(_gate);
                return n;
            }
        }

        public void Write(ReadOnlySpan<byte> source)
        {
            while (!source.IsEmpty)
            {
                lock (_gate)
                {
                    while (_count == _ring.Length && !_readerClosed && !_writerClosed)
                    {
                        Monitor.Wait(_gate);
                    }

                    if (_readerClosed || _writerClosed) throw new IOException("The in-memory pipe is closed.");
                    int tail = (_head + _count) % _ring.Length;
                    int n = Math.Min(source.Length, _ring.Length - _count);
                    int first = Math.Min(n, _ring.Length - tail);
                    source[..first].CopyTo(_ring.AsSpan(tail));
                    if (n > first) source[first..n].CopyTo(_ring);
                    _count += n;
                    source = source[n..];
                    Monitor.PulseAll(_gate);
                }
            }
        }

        public void CloseWriter() { lock (_gate) { _writerClosed = true; Monitor.PulseAll(_gate); } }

        public void CloseReader() { lock (_gate) { _readerClosed = true; Monitor.PulseAll(_gate); } }
    }

    private sealed class DuplexEnd : Stream
    {
        private readonly ByteRingPipe _read;
        private readonly ByteRingPipe _write;
        private int _disposed;

        public DuplexEnd(ByteRingPipe read, ByteRingPipe write)
        {
            _read = read;
            _write = write;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer) => _read.Read(buffer);

        public override void Write(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);
            Write(buffer.AsSpan(offset, count));
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Volatile.Read(ref _disposed) != 0) throw new IOException("The in-memory pipe end is disposed.");
            _write.Write(buffer);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _write.CloseWriter();
                _read.CloseReader();
            }

            base.Dispose(disposing);
        }
    }
}
