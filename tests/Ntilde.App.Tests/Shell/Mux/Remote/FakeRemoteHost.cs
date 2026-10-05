using System.Text;
using Ntilde.Mux;
using Ntilde.Mux.Cli;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Mux.Transport;
using Ntilde.Platform.Ssh.Exec;

namespace Ntilde.Tests.Shell.Mux.Remote;

/// <summary>
/// A remote host for tests (Phase 4 spec §7.1, §8.1): an <see cref="ISshExecTransport"/> whose every
/// channel runs <see cref="MuxProxyCommand.Run"/> in-process, on a thread of its own, against one
/// in-memory <see cref="MuxServer"/> - the remote daemon - as sshd runs <c>ntilde-mux proxy --stdio</c>.
/// Its controls stand in for what goes wrong on a real link: noise before the greeting
/// (<see cref="Noise"/>), a command that prints and exits instead (<see cref="Script"/>: a missing
/// binary, ssh's own failure, a native transport error, a hang), a blocked start (<see cref="OnStart"/>),
/// a dropped link (<see cref="CutLink"/>), a silent one (<see cref="StallLink"/>) and a daemon that
/// exits (<see cref="StopDaemon"/>).
/// </summary>
/// <remarks>
/// A channel's stdout behaves like an anonymous pipe on Windows, the hardest case: disposing the read
/// end does not wake a read pending on it. Only the remote side closing its end (the proxy exiting) or
/// the channel's own <see cref="FakeRemoteChannel.Dispose"/> (killing it, as the real channels kill ssh
/// or close the native session) ends that read.
/// </remarks>
internal sealed class FakeRemoteHost : ISshExecTransport, IDisposable
{
    /// <summary>What OpenSSH reports for a connection it lost: its own failure, exit 255.</summary>
    public const int LinkLostExitCode = 255;

    private const int PipeCapacityBytes = 1 << 20;

    private readonly object _gate = new();
    private readonly MuxServerOptions _serverOptions;
    private readonly List<FakeRemoteChannel> _channels = [];
    private readonly List<string> _commands = [];
    private MuxServer? _server;
    private InMemoryMuxListener? _listener;
    private MuxEndpointDescriptor? _descriptor;
    private int _daemonStarts;
    private int _startCount;
    private bool _disposed;

    /// <param name="displayName">What <see cref="DisplayName"/> reports, as a transport's <c>user@host</c>.</param>
    /// <param name="serverOptions">The daemon's options: another protocol range, say.</param>
    public FakeRemoteHost(string displayName = "nova@fake-host", MuxServerOptions? serverOptions = null)
    {
        DisplayName = displayName;
        _serverOptions = serverOptions ?? new MuxServerOptions { ForceConPtyFiltering = false };
    }

    public string DisplayName { get; }

    /// <summary>The shells the daemon spawns, across daemon restarts.</summary>
    public ScriptedSessionFactory Shells { get; } = new();

    /// <summary>The running daemon, started now if none is (after <see cref="StopDaemon"/>, a new one).</summary>
    public MuxServer Server
    {
        get { lock (_gate) return EnsureDaemonLocked(); }
    }

    /// <summary>The pid the current daemon's proxies greet with; a new one after each restart.</summary>
    public int DaemonPid
    {
        get { lock (_gate) { EnsureDaemonLocked(); return _descriptor!.Pid; } }
    }

    /// <summary>How many times <see cref="Start"/> was called, a start that blocked or failed included.</summary>
    public int StartCount => Volatile.Read(ref _startCount);

    /// <summary>Every command started, in order.</summary>
    public IReadOnlyList<string> Commands
    {
        get { lock (_gate) return _commands.ToArray(); }
    }

    /// <summary>Every channel handed out, in order.</summary>
    public IReadOnlyList<FakeRemoteChannel> Channels
    {
        get { lock (_gate) return _channels.ToArray(); }
    }

    public FakeRemoteChannel? LastChannel
    {
        get { lock (_gate) return _channels.Count == 0 ? null : _channels[^1]; }
    }

    /// <summary>What the next channels print on stdout before the proxy's greeting: rc files, the MOTD.</summary>
    public string Noise { get; set; } = string.Empty;

    /// <summary>When set, the next channels run this instead of the proxy.</summary>
    public FakeRemoteScript? Script { get; set; }

    /// <summary>
    /// Runs first inside <see cref="Start"/>, with its token, after <see cref="StartCount"/> counts it: a
    /// test can block there (an unanswered prompt, until cancelled) or throw (ssh could not start).
    /// </summary>
    public Action<CancellationToken>? OnStart { get; set; }

    public ISshExecChannel Start(string remoteCommand, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteCommand);
        Interlocked.Increment(ref _startCount);
        lock (_gate) _commands.Add(remoteCommand);
        OnStart?.Invoke(ct);
        ct.ThrowIfCancellationRequested();

        var channel = new FakeRemoteChannel(this, remoteCommand, Script, Noise, PipeCapacityBytes);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _channels.Add(channel);
        }

        channel.Begin();
        return channel;
    }

    /// <summary>
    /// The link drops under every open channel. Its stdout ends and its stdin no longer reaches the remote
    /// side; the proxy there sees EOF and exits, so the daemon keeps its sessions. The channel then reports
    /// <paramref name="exitCode"/> (OpenSSH's 255 by default), or, with <paramref name="transportError"/>,
    /// fails its stdout read the way the native transport does (exit status null). Channels started later
    /// work: the link is back.
    /// </summary>
    public void CutLink(int? exitCode = LinkLostExitCode, string? transportError = null)
    {
        foreach (FakeRemoteChannel channel in Channels) channel.Cut(exitCode, transportError);
    }

    /// <summary>
    /// The link goes silent under every open channel, as one does before TCP notices: nothing either side
    /// sends arrives, and no EOF either. Only disposing the channel (killing ssh) ends it.
    /// </summary>
    public void StallLink()
    {
        foreach (FakeRemoteChannel channel in Channels) channel.Stall();
    }

    /// <summary>
    /// The daemon exits: its sessions end, and every proxy connected to it exits 3 (spec §8.1). The next
    /// proxy starts a new daemon, as the real one spawns <c>ntilde-mux serve</c> on demand.
    /// </summary>
    public void StopDaemon()
    {
        MuxServer? server;
        lock (_gate)
        {
            server = _server;
            _server = null;
            _listener = null;
            _descriptor = null;
        }

        server?.Dispose();
    }

    public void Dispose()
    {
        FakeRemoteChannel[] channels;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            channels = _channels.ToArray();
        }

        foreach (FakeRemoteChannel channel in channels) channel.Dispose();
        StopDaemon();
    }

    /// <summary>The proxy's <c>MuxDaemonLauncher.EnsureEndpointStreamAsync</c>: this host's daemon, started on demand.</summary>
    internal Task<(Stream Stream, MuxEndpointDescriptor Descriptor)> ConnectDaemonAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_disposed) throw new IOException("The fake remote host is gone.");
            EnsureDaemonLocked();
            return Task.FromResult((_listener!.Connect(), _descriptor!));
        }
    }

    private MuxServer EnsureDaemonLocked()
    {
        if (_server is not null) return _server;

        int pid = 4200 + ++_daemonStarts;
        _listener = new InMemoryMuxListener(PipeCapacityBytes);
        _server = new MuxServer(Shells, _serverOptions);
        _server.Start(_listener);
        _descriptor = new MuxEndpointDescriptor
        {
            Endpoint = "fake-daemon",
            ProcessName = "ntilde-mux",
            Pid = pid,
            MinVersion = _serverOptions.MinProtocolVersion,
            MaxVersion = _serverOptions.MaxProtocolVersion,
        };
        return _server;
    }
}

/// <summary>
/// What a <see cref="FakeRemoteHost"/> channel runs instead of the proxy: it prints <paramref name="Stdout"/>
/// and <paramref name="Stderr"/>, then exits with <paramref name="ExitCode"/>; or, with
/// <paramref name="TransportError"/>, its stdout read fails as the native transport's does; or, with
/// <paramref name="Hang"/>, it prints and then runs until the channel stops it.
/// </summary>
internal sealed record FakeRemoteScript(string Stdout = "", string Stderr = "", int? ExitCode = null, string? TransportError = null, bool Hang = false)
{
    /// <summary>dash, when the binary is not there: <c>sh: 1: …: not found</c>, exit 127.</summary>
    public static FakeRemoteScript NotInstalledDash { get; } =
        new(Stderr: "sh: 1: /home/nova/.local/share/ntilde/bin/ntilde-mux: not found\n", ExitCode: 127);

    /// <summary>OpenSSH, when nothing listens: exit 255 and its reason on stderr.</summary>
    public static FakeRemoteScript ConnectionRefused { get; } =
        new(Stderr: "ssh: connect to host fake-host port 22: Connection refused\r\n", ExitCode: FakeRemoteHost.LinkLostExitCode);

    /// <summary>The native transport failing (connect, host key, auth): stdout's read throws.</summary>
    public static FakeRemoteScript NativeFailure(string message) => new(TransportError: message);

    /// <summary>Prints nothing and never ends on its own: a prompt nobody answers, a wedged shell.</summary>
    public static FakeRemoteScript Silent { get; } = new(Hang: true);
}

/// <summary>One <see cref="FakeRemoteHost"/> exec channel: the remote side runs on a thread of its own.</summary>
internal sealed class FakeRemoteChannel : ISshExecChannel
{
    /// <summary>How long <see cref="Dispose"/> lets the remote side end on stdin's EOF before killing it.</summary>
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(2);

    private readonly FakeRemoteHost _host;
    private readonly FakeRemoteScript? _script;
    private readonly string _noise;
    private readonly Stream _stdinWriter;   // the channel's stdin: writes reach _proxyStdin
    private readonly Stream _proxyStdin;    // what the remote side reads
    private readonly Stream _proxyStdoutPipe; // what the remote side writes: _stdoutReader reads it
    private readonly Stream _stdoutReader;
    private readonly ChannelStdin _stdin;
    private readonly ChannelStdout _stdout;
    private readonly RemoteStdout _remoteStdout;
    private readonly StringWriter _stderrText = new();
    private readonly TextWriter _stderr;
    private readonly TaskCompletionSource<int?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim _killed = new();
    private readonly Thread _remote;
    private volatile bool _stalled;
    private volatile bool _cut;
    private int? _cutExitCode;
    private volatile string? _transportError;
    private volatile bool _killedByChannel;
    private int _disposing;

    internal FakeRemoteChannel(FakeRemoteHost host, string command, FakeRemoteScript? script, string noise, int pipeCapacityBytes)
    {
        _host = host;
        Command = command;
        _script = script;
        _noise = noise;
        (_stdinWriter, _proxyStdin) = InMemoryDuplexPipe.Create(pipeCapacityBytes);
        (_proxyStdoutPipe, _stdoutReader) = InMemoryDuplexPipe.Create(pipeCapacityBytes);
        _stdin = new ChannelStdin(this);
        _stdout = new ChannelStdout(this);
        _remoteStdout = new RemoteStdout(this);
        _stderr = TextWriter.Synchronized(_stderrText);
        _remote = new Thread(RunRemote) { IsBackground = true, Name = "FakeRemoteChannel" };
    }

    public string Command { get; }
    public Stream Stdout => _stdout;
    public Stream Stdin => _stdin;

    public string StderrTail
    {
        // TextWriter.Synchronized locks the writer itself: reading under the same lock sees whole lines.
        get { lock (_stderr) return _stderrText.ToString(); }
    }

    public Task<int?> Completion => _completion.Task;

    /// <summary>Completes once <see cref="Dispose"/> has finished: the connector ended this channel.</summary>
    public Task Disposed => _disposed.Task;

    public bool IsDisposed => _disposed.Task.IsCompleted;

    /// <summary>Reads of <see cref="Stdout"/> in progress right now: a client reader stuck on a dead channel shows here.</summary>
    public int PendingStdoutReads => _stdout.PendingReads;

    internal void Begin() => _remote.Start();

    internal void Cut(int? exitCode, string? transportError)
    {
        _cutExitCode = exitCode;
        _transportError = transportError;
        _cut = true;
        // The remote side's stdin ends (the proxy exits 0 and the daemon keeps the sessions); the
        // channel's stdout ends; a write to the channel's stdin fails, as a closed pipe's does. A hung
        // command goes with the link.
        CloseQuietly(_proxyStdin);
        CloseQuietly(_proxyStdoutPipe);
        _killed.Set();
    }

    internal void Stall() => _stalled = true;

    /// <summary>
    /// As the real channels end: stdin's EOF, then up to a grace period for the remote side to exit, then
    /// it is killed (<see cref="Completion"/> null). Only then does stdout's read end close: a read pending
    /// on it returns once the remote side's end is gone. A stalled or hung remote side is killed at once,
    /// since no EOF will ever end it.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposing, 1) != 0) return;

        bool endsOnEof = !_stalled && _script is not { Hang: true };
        _stdin.Dispose();
        if (!endsOnEof || !_remote.Join(ExitGrace))
        {
            _killedByChannel = true;
            _killed.Set();
            CloseQuietly(_proxyStdin);
            CloseQuietly(_proxyStdoutPipe);
            _remote.Join(ExitGrace);
        }

        CloseQuietly(_stdoutReader);
        _disposed.TrySetResult();
    }

    private void RunRemote()
    {
        int? exitCode = null;
        try
        {
            if (_noise.Length > 0) WriteRemote(_noise);
            if (_script is { } script)
            {
                if (script.Stdout.Length > 0) WriteRemote(script.Stdout);
                if (script.Stderr.Length > 0) _stderr.Write(script.Stderr);
                if (script.Hang) _killed.Wait();
                _transportError ??= script.TransportError;
                exitCode = script.Hang || script.TransportError is not null ? null : script.ExitCode;
            }
            else
            {
                exitCode = MuxProxyCommand.Run(_proxyStdin, _remoteStdout, _stderr, _host.ConnectDaemonAsync);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The link was cut, or the channel killed, while the remote side was still writing.
        }
        catch (Exception ex)
        {
            // Never past this thread's entry point: that would take the test host down.
            _stderr.Write($"fake remote side failed: {ex}\n");
        }
        finally
        {
            // As a process's exit closes its streams.
            CloseQuietly(_proxyStdoutPipe);
            CloseQuietly(_proxyStdin);
            if (_transportError is { } error) _stderr.Write($"native ssh: {error}\n");
            _completion.TrySetResult(_cut ? _cutExitCode : _killedByChannel ? null : exitCode);
        }
    }

    private void WriteRemote(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        _remoteStdout.Write(bytes, 0, bytes.Length);
    }

    private static void CloseQuietly(Stream stream)
    {
        try { stream.Dispose(); }
        catch (IOException) { /* already gone */ }
    }

    /// <summary>The channel's stdin. While the link is stalled, bytes and the EOF are lost on the way.</summary>
    private sealed class ChannelStdin(FakeRemoteChannel channel) : Stream
    {
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
            if (channel._stalled) return;
            channel._stdinWriter.Write(buffer);
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _closed, 1) == 0 && !channel._stalled)
            {
                CloseQuietly(channel._stdinWriter);
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// The channel's stdout. Disposing it does not wake a read in progress (an anonymous pipe on
    /// Windows); the remote side's end closing does. At the end, a native transport error throws.
    /// </summary>
    private sealed class ChannelStdout(FakeRemoteChannel channel) : Stream
    {
        private int _pendingReads;

        public int PendingReads => Volatile.Read(ref _pendingReads);

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
            Interlocked.Increment(ref _pendingReads);
            try
            {
                int read = channel._stdoutReader.Read(buffer);
                if (read == 0 && !buffer.IsEmpty && channel._transportError is { } error)
                {
                    throw new SshExecTransportException($"SSH to {channel._host.DisplayName} failed: {error}", error);
                }

                return read;
            }
            finally
            {
                Interlocked.Decrement(ref _pendingReads);
            }
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>What the remote side writes: dropped while the link is stalled.</summary>
    private sealed class RemoteStdout(FakeRemoteChannel channel) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);
            Write(buffer.AsSpan(offset, count));
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (channel._stalled) return;
            channel._proxyStdoutPipe.Write(buffer);
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) CloseQuietly(channel._proxyStdoutPipe);
            base.Dispose(disposing);
        }
    }
}
