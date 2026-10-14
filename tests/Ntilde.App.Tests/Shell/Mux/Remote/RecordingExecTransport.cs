using System.Text;
using Ntilde.Platform.Ssh.Exec;

namespace Ntilde.Tests.Shell.Mux.Remote;

/// <summary>What a <see cref="RecordingExecTransport"/> command prints, and how it exits.</summary>
internal sealed record FakeExecReply(string Stdout = "", string Stderr = "", int? ExitCode = 0);

/// <summary>One command a <see cref="RecordingExecTransport"/> ran, with every byte its stdin received before EOF.</summary>
internal sealed record RecordedExec(string Command, byte[] Stdin);

/// <summary>
/// An <see cref="ISshExecTransport"/> for the install flow's one-shot commands (Phase 4 spec §9): each
/// channel takes its stdin to EOF, records the command with those bytes, and only then answers with
/// <c>respond</c>'s reply - as the upload's <c>cat</c> does, which prints nothing until its input ends.
/// </summary>
internal sealed class RecordingExecTransport(Func<string, byte[], FakeExecReply> respond) : ISshExecTransport
{
    private readonly object _gate = new();
    private readonly List<RecordedExec> _runs = [];

    public string DisplayName => "nova@fake-host";

    /// <summary>Every command that reached EOF, in order.</summary>
    public IReadOnlyList<RecordedExec> Runs
    {
        get { lock (_gate) return _runs.ToArray(); }
    }

    public IReadOnlyList<string> Commands => Runs.Select(r => r.Command).ToArray();

    /// <summary>Runs first inside <see cref="Start"/>: a test throws here when ssh could not even start.</summary>
    public Action<string>? OnStart { get; init; }

    public ISshExecChannel Start(string remoteCommand, CancellationToken ct)
    {
        OnStart?.Invoke(remoteCommand);
        ct.ThrowIfCancellationRequested();
        return new Channel(this, remoteCommand);
    }

    private void Record(RecordedExec run)
    {
        lock (_gate) _runs.Add(run);
    }

    private sealed class Channel : ISshExecChannel
    {
        private readonly RecordingExecTransport _transport;
        private readonly string _command;
        private readonly TaskCompletionSource<FakeExecReply> _reply = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Channel(RecordingExecTransport transport, string command)
        {
            _transport = transport;
            _command = command;
            Stdin = new EofStdin(this);
            Stdout = new ReplyStdout(_reply.Task);
            Completion = ExitCodeAsync();
        }

        public Stream Stdout { get; }

        public Stream Stdin { get; }

        public string StderrTail => _reply.Task.IsCompletedSuccessfully ? _reply.Task.Result.Stderr : string.Empty;

        public Task<int?> Completion { get; }

        /// <summary>A channel ended before its stdin did: the command was killed, so no exit status.</summary>
        public void Dispose() => _reply.TrySetResult(new FakeExecReply(ExitCode: null));

        /// <summary>As <see cref="Dispose"/>: the command is killed, with no exit status.</summary>
        public void Abort() => Dispose();

        private async Task<int?> ExitCodeAsync() => (await _reply.Task.ConfigureAwait(false)).ExitCode;

        private void OnEof(byte[] stdin)
        {
            if (_reply.Task.IsCompleted) return;
            _transport.Record(new RecordedExec(_command, stdin));
            try
            {
                _reply.TrySetResult(_transport.Respond(_command, stdin));
            }
            catch (Exception ex)
            {
                _reply.TrySetException(ex);
            }
        }

        private sealed class EofStdin(Channel channel) : Stream
        {
            private readonly MemoryStream _received = new();
            private int _closed;

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => Volatile.Read(ref _closed) == 0;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override void Write(byte[] buffer, int offset, int count)
            {
                ValidateBufferArguments(buffer, offset, count);
                Write(buffer.AsSpan(offset, count));
            }

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
                lock (_received) _received.Write(buffer);
            }

            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing && Interlocked.Exchange(ref _closed, 1) == 0)
                {
                    byte[] bytes;
                    lock (_received) bytes = _received.ToArray();
                    channel.OnEof(bytes);
                }

                base.Dispose(disposing);
            }
        }

        /// <summary>Stdout: nothing until the reply exists (stdin's EOF), then the reply's text.</summary>
        private sealed class ReplyStdout(Task<FakeExecReply> reply) : Stream
        {
            private MemoryStream? _text;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                FakeExecReply answer = await reply.WaitAsync(cancellationToken).ConfigureAwait(false);
                _text ??= new MemoryStream(Encoding.UTF8.GetBytes(answer.Stdout));
                return _text.Read(buffer.Span);
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    private FakeExecReply Respond(string command, byte[] stdin) => respond(command, stdin);
}
