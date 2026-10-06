using System.IO.Pipes;
using Ntilde.Platform.Ssh.Exec;

namespace Ntilde.Platform.Tests.Ssh.Exec;

/// <summary>An <see cref="ISshExecTransport"/> that hands out one scripted channel per <see cref="Start"/>.</summary>
internal sealed class FakeExecTransport : ISshExecTransport
{
    private readonly Func<FakeExecChannel> _createChannel;

    public FakeExecTransport(Func<FakeExecChannel> createChannel) => _createChannel = createChannel;

    public string DisplayName => "fake@host";
    public string? LastCommand { get; private set; }
    public FakeExecChannel? Channel { get; private set; }

    public ISshExecChannel Start(string remoteCommand, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        LastCommand = remoteCommand;
        return Channel = _createChannel();
    }
}

/// <summary>
/// A scripted exec channel. Stdout is a real anonymous pipe where a test needs reads to block (as a
/// process's stdout does), so <see cref="SshExec"/> is exercised against the same blocking reads it
/// meets in production.
/// </summary>
internal sealed class FakeExecChannel : ISshExecChannel
{
    private readonly TaskCompletionSource<int?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Stream? _stdoutWriter;

    private FakeExecChannel(Stream stdout, Stream? stdoutWriter, Func<FakeExecChannel, Stream> createStdin, string stderr)
    {
        Stdout = stdout;
        _stdoutWriter = stdoutWriter;
        StderrTail = stderr;
        Stdin = createStdin(this);
    }

    public Stream Stdout { get; }
    public Stream Stdin { get; }
    public string StderrTail { get; }
    public Task<int?> Completion => _completion.Task;

    public bool StdinClosed { get; private set; }
    public long StdinBytes { get; private set; }
    public bool Disposed { get; private set; }

    /// <summary>Echoes stdin to stdout; stdin's EOF closes stdout and exits with <paramref name="exitCode"/>.</summary>
    public static FakeExecChannel Echo(int exitCode)
    {
        (Stream reader, Stream writer) = CreatePipe();
        return new FakeExecChannel(reader, writer, self => new ScriptedStdin(
            onWrite: (channel, data) => { channel.StdinBytes += data.Length; writer.Write(data.Span); },
            onClose: channel =>
            {
                channel.StdinClosed = true;
                writer.Dispose();
                channel._completion.TrySetResult(exitCode);
            },
            self), stderr: string.Empty);
    }

    /// <summary>Never prints, never exits: only <see cref="Dispose"/> ends it.</summary>
    public static FakeExecChannel Hung()
    {
        (Stream reader, Stream writer) = CreatePipe();
        return new FakeExecChannel(reader, writer, self => new ScriptedStdin(
            onWrite: (channel, data) => channel.StdinBytes += data.Length,
            onClose: channel => channel.StdinClosed = true,
            self), stderr: string.Empty);
    }

    /// <summary>Prints <paramref name="stdout"/> and exits with <paramref name="exitCode"/> without reading stdin.</summary>
    public static FakeExecChannel Printing(byte[] stdout, int? exitCode, string stderr = "")
    {
        FakeExecChannel channel = new(new MemoryStream(stdout, writable: false), null, self => new ScriptedStdin(
            onWrite: (c, data) => c.StdinBytes += data.Length,
            onClose: c => c.StdinClosed = true,
            self), stderr);
        channel._completion.TrySetResult(exitCode);
        return channel;
    }

    /// <summary>The remote command exited before reading its stdin: every write fails as a broken pipe does.</summary>
    public static FakeExecChannel RefusingStdin(int exitCode, string stderr)
    {
        FakeExecChannel channel = new(new MemoryStream([], writable: false), null, self => new ScriptedStdin(
            onWrite: (_, _) => throw new IOException("The pipe is being closed."),
            onClose: c => c.StdinClosed = true,
            self), stderr);
        channel._completion.TrySetResult(exitCode);
        return channel;
    }

    public bool Aborted { get; private set; }

    /// <summary>As <see cref="Dispose"/>, at once: the fake has no grace period to skip.</summary>
    public void Abort()
    {
        Aborted = true;
        Dispose();
    }

    public void Dispose()
    {
        Disposed = true;
        _stdoutWriter?.Dispose();
        _completion.TrySetResult(null);
        Stdout.Dispose();
    }

    private static (Stream Reader, Stream Writer) CreatePipe()
    {
        var writer = new AnonymousPipeServerStream(PipeDirection.Out);
        var reader = new AnonymousPipeClientStream(PipeDirection.In, writer.ClientSafePipeHandle);
        return (reader, writer);
    }

    private sealed class ScriptedStdin : Stream
    {
        private readonly Action<FakeExecChannel, ReadOnlyMemory<byte>> _onWrite;
        private readonly Action<FakeExecChannel> _onClose;
        private readonly FakeExecChannel _channel;
        private bool _closed;

        public ScriptedStdin(Action<FakeExecChannel, ReadOnlyMemory<byte>> onWrite, Action<FakeExecChannel> onClose, FakeExecChannel channel)
        {
            _onWrite = onWrite;
            _onClose = onClose;
            _channel = channel;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !_closed;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            _onWrite(_channel, buffer.AsMemory(offset, count).ToArray());
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_closed)
            {
                _closed = true;
                _onClose(_channel);
            }

            base.Dispose(disposing);
        }
    }
}

/// <summary>An <see cref="IProgress{T}"/> that records synchronously (<see cref="Progress{T}"/> posts, so it races the assertions).</summary>
internal sealed class RecordingProgress : IProgress<long>
{
    private readonly List<long> _reports = [];

    public IReadOnlyList<long> Reports
    {
        get { lock (_reports) return _reports.ToArray(); }
    }

    public void Report(long value)
    {
        lock (_reports) _reports.Add(value);
    }
}
