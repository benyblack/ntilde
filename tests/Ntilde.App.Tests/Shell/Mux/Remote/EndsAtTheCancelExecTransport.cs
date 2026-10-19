using Ntilde.Platform.Ssh.Exec;

namespace Ntilde.Tests.Shell.Mux.Remote;

/// <summary>
/// An <see cref="ISshExecTransport"/> whose one channel prints <c>output</c> and then ends, and whose read that first
/// meets the end runs <c>atEnd</c> before returning it: a test cancels the connect there. That is the order a cancel
/// can reach a reader in (#541): the abort the cancel runs ends the remote command's stdout, and the read returns that
/// end before anything has looked at the token. Stdin takes every write and drops it.
/// </summary>
internal sealed class EndsAtTheCancelExecTransport(byte[] output, Action atEnd) : ISshExecTransport
{
    public string DisplayName => "nova@fake-host";

    /// <summary>The channel the start made, once it has.</summary>
    public Channel? Started { get; private set; }

    public ISshExecChannel Start(string remoteCommand, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Started = new Channel(output, atEnd);
    }

    internal sealed class Channel(byte[] output, Action atEnd) : ISshExecChannel
    {
        private readonly TaskCompletionSource<int?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _aborts;

        public Stream Stdout { get; } = new EndingStdout(output, atEnd);
        public Stream Stdin { get; } = Stream.Null;
        public string StderrTail => string.Empty;
        public Task<int?> Completion => _completion.Task;

        /// <summary>How many times <see cref="Abort"/> was called.</summary>
        public int AbortCount => Volatile.Read(ref _aborts);

        public void Abort()
        {
            Interlocked.Increment(ref _aborts);
            _completion.TrySetResult(null);
        }

        public void Dispose() => _completion.TrySetResult(null);
    }

    /// <summary>Reads <c>output</c>, then the end; it ignores the token, as a synchronous pipe's read does.</summary>
    private sealed class EndingStdout(byte[] output, Action atEnd) : Stream
    {
        private int _position;
        private int _ended;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            int count = Math.Min(buffer.Length, output.Length - _position);
            if (count > 0)
            {
                output.AsSpan(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }

            if (Interlocked.Exchange(ref _ended, 1) == 0) atEnd();
            return 0;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer, offset, count));

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
