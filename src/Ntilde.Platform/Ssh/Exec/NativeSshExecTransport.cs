using System.Globalization;
using System.Text;
using System.Text.Json;
using Ntilde.Platform.Ssh.Interactions;
using Ntilde.Platform.Ssh.Models;
using Ntilde.Platform.Ssh.Native;
using Ntilde.VT;

namespace Ntilde.Platform.Ssh.Exec;

/// <summary>
/// Runs a remote command over the native backend (Phase 4 spec §8.3) with <c>nova_ssh_exec</c>: the
/// native session's connect, jump chain, host-key and auth path, then the command with no PTY. A
/// persisted remote tab on a native profile reaches <c>ntilde-mux proxy --stdio</c> this way, without
/// the ssh executable.
/// </summary>
/// <remarks>
/// <see cref="Start"/> returns as soon as the native layer has the session. Connect and auth happen
/// afterwards, on the channel's poll thread, which answers their prompts through the interaction
/// handler. Such a failure (or a connection lost later) shows as an
/// <see cref="SshExecTransportException"/> from <see cref="ISshExecChannel.Stdout"/> once the output
/// before it has been read. Its message is also in <see cref="ISshExecChannel.StderrTail"/>, and
/// <see cref="ISshExecChannel.Completion"/> is null.
/// </remarks>
public sealed class NativeSshExecTransport : ISshExecTransport
{
    private readonly SshProfile _profile;
    private readonly INativeSshInterop _interop;
    private readonly ISshInteractionHandler? _interactionHandler;
    private readonly Func<SshProfile, NativeSshConnectionOptions> _optionsFactory;
    private readonly Action<string> _log;

    /// <param name="profile">The profile: its address, auth and jump chain, and the context its prompts carry.</param>
    /// <param name="interop">The native layer (<see cref="NativeSshInterop"/>).</param>
    /// <param name="interactionHandler">
    /// Answers host-key, password, passphrase and keyboard-interactive prompts, from the known-hosts store
    /// and the vault or by asking the user. With none, every prompt is refused.
    /// </param>
    /// <param name="optionsFactory">
    /// Builds the connection options. Production passes <see cref="NativeSshConnectionOptionsFactory.Create(SshProfile)"/>,
    /// the builder <see cref="Sessions.NativeSshSession"/> uses.
    /// </param>
    /// <param name="log">Where start, failure and exit are logged; <see cref="TerminalLogger.Log(string)"/> by default.</param>
    public NativeSshExecTransport(
        SshProfile profile,
        INativeSshInterop interop,
        ISshInteractionHandler? interactionHandler,
        Func<SshProfile, NativeSshConnectionOptions> optionsFactory,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(interop);
        ArgumentNullException.ThrowIfNull(optionsFactory);

        _profile = profile;
        _interop = interop;
        _interactionHandler = interactionHandler;
        _optionsFactory = optionsFactory;
        _log = log ?? TerminalLogger.Log;
    }

    public string DisplayName =>
        string.IsNullOrWhiteSpace(_profile.User) ? _profile.Host : $"{_profile.User}@{_profile.Host}";

    /// <summary>
    /// Starts <paramref name="remoteCommand"/> and returns its channel at once. Connect and auth have not
    /// happened yet: see the class remarks.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The command is blank, or the profile cannot be connected to: a blank host or user, or a jump hop
    /// without a host. <see cref="ArgumentOutOfRangeException"/> for a port outside 1-65535.
    /// </exception>
    /// <exception cref="InvalidOperationException">The native layer rejected the connection options.</exception>
    public ISshExecChannel Start(string remoteCommand, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteCommand);
        ct.ThrowIfCancellationRequested();

        NativeSshConnectionOptions options = _optionsFactory(_profile);

        // The command itself is not logged, as OpenSshExecTransport's sanitized command line leaves it out.
        _log(string.Create(CultureInfo.InvariantCulture,
            $"[NativeSshExec] {DisplayName}: exec on {options.User}@{options.Host}:{options.Port} via {options.JumpHops.Count} jump hop(s)"));

        NovaSshSafeHandle handle = _interop.Exec(options, remoteCommand);
        NativeSshExecChannel channel;
        try
        {
            // A responder per connection: the vault's password is offered once per connection, like a
            // shell session's. No session id, because this connection is not in ActiveSshSessionRegistry
            // (spec §8.4), so a password typed here is not kept for reconnects.
            channel = new NativeSshExecChannel(
                handle, _interop, new NativeSshPromptResponder(_profile, sessionId: null), _interactionHandler, DisplayName, _log);
        }
        catch
        {
            _interop.Close(handle);
            throw;
        }

        if (ct.IsCancellationRequested)
        {
            // Cancelled while starting: nobody has the channel yet, so stop it outright.
            channel.Abort();
            ct.ThrowIfCancellationRequested();
        }

        return channel;
    }
}

/// <summary>
/// A native exec session as an <see cref="ISshExecChannel"/>. One dedicated thread polls the session's
/// handle; nothing else polls it. That thread routes stdout into a <see cref="BoundedChunkQueue"/>
/// behind <see cref="Stdout"/>, stderr into <see cref="StderrTail"/>, prompts to the interaction
/// handler, and the exit status and the close into <see cref="Completion"/>.
/// </summary>
/// <remarks>
/// <para>
/// The output path has no thread-pool work. The poll thread queues each stdout chunk and wakes a
/// synchronous <see cref="Stream.Read(Span{byte})"/> (the mux client's read thread) directly, so
/// remote output keeps flowing while the pool is starved. Only an asynchronous read that finds the
/// queue empty waits on a pool thread. Those reads are the proxy handshake and one-shot
/// <see cref="SshExec"/> commands; neither is on the output path.
/// </para>
/// <para>
/// Backpressure: past <see cref="StdoutPauseThresholdBytes"/> unread, the poll thread waits for the
/// reader and stops polling meanwhile. rusty_ssh's queue then fills to its 4 MiB budget, its worker
/// stops reading the channel, and the remote is held back by the SSH window and TCP. Nothing buffers
/// without bound.
/// </para>
/// <para>
/// <see cref="Completion"/> is the exit status the command reported, or null when it reported none.
/// The native layer cannot tell a command killed by a signal from a lost connection: neither gets an
/// exit status. A failure the native layer reported is the difference, and it surfaces as an
/// <see cref="SshExecTransportException"/> on <see cref="Stdout"/>.
/// </para>
/// </remarks>
internal sealed class NativeSshExecChannel : ISshExecChannel
{
    /// <summary>How long <see cref="Dispose"/> lets the command end on stdin's EOF before closing the session.</summary>
    internal static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(2);

    /// <summary>How long a closed session's poll thread may take to stop.</summary>
    internal static readonly TimeSpan StopWait = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan PollDelay = TimeSpan.FromMilliseconds(10);

    /// <summary>Unread stdout at which the poll thread stops and waits for the reader.</summary>
    internal const long StdoutPauseThresholdBytes = 16L * 1024 * 1024;

    /// <summary>Unread stdout at which a waiting poll thread resumes.</summary>
    internal const long StdoutResumeThresholdBytes = StdoutPauseThresholdBytes / 2;

    private const int StderrTailBytes = 8192;

    private readonly INativeSshInterop _interop;
    private readonly NativeSshPromptResponder _promptResponder;
    private readonly ISshInteractionHandler? _interactionHandler;
    private readonly string _displayName;
    private readonly Action<string> _log;
    private readonly BoundedChunkQueue _stdoutQueue = new(StdoutPauseThresholdBytes, StdoutResumeThresholdBytes);
    private readonly StdoutStream _stdout;
    private readonly StdinStream _stdin;
    private readonly BoundedTail _stderrTail = new(StderrTailBytes);
    private readonly TaskCompletionSource<int?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _pollThread;

    private NovaSshSafeHandle? _handle;
    private volatile string? _failure;
    private volatile bool _ended;
    private int _closing;

    // Poll thread only.
    private int? _exitCode;

    public NativeSshExecChannel(
        NovaSshSafeHandle handle,
        INativeSshInterop interop,
        NativeSshPromptResponder promptResponder,
        ISshInteractionHandler? interactionHandler,
        string displayName,
        Action<string> log)
    {
        _handle = handle;
        _interop = interop;
        _promptResponder = promptResponder;
        _interactionHandler = interactionHandler;
        _displayName = displayName;
        _log = log;
        _stdout = new StdoutStream(this, _stdoutQueue);
        _stdin = new StdinStream(this);
        Completion = _completion.Task;

        _pollThread = new Thread(PollLoop) { IsBackground = true, Name = "SshExecPoll" };
        _pollThread.Start();
    }

    public Stream Stdout => _stdout;
    public Stream Stdin => _stdin;
    public string StderrTail => _stderrTail.ToString();
    public Task<int?> Completion { get; }

    /// <summary>
    /// Ends the command without hanging. It sends stdin's EOF, which ends a command reading its stdin
    /// (the proxy exits 0 on it), and gives the command up to <see cref="ExitGrace"/> to end. Then it
    /// closes the session, which drops the connection, and waits up to <see cref="StopWait"/> for the
    /// poll thread. A reader blocked on <see cref="Stdout"/> returns. Stdout that arrives after this
    /// call is dropped. Blocks for up to <see cref="ExitGrace"/> plus <see cref="StopWait"/>, so never
    /// call it on the UI thread.
    /// </summary>
    public void Dispose() => Close(ExitGrace);

    /// <summary>Stops the command at once, with no grace period: for a channel nobody has seen yet.</summary>
    internal void Abort() => Close(TimeSpan.Zero);

    private void Close(TimeSpan grace)
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0)
        {
            return;
        }

        _stdin.Dispose();

        // Nobody will read what comes after this. A poll thread parked on a full queue moves on, and
        // keeps polling (dropping stdout) until Closed.
        _stdoutQueue.Abandon();

        if (!_pollThread.Join(grace))
        {
            Log(grace == TimeSpan.Zero
                ? $"[NativeSshExec] {_displayName}: start cancelled; closing the session"
                : $"[NativeSshExec] {_displayName}: the command did not end on EOF; closing the session");
            _stop.Cancel();
            CloseHandle();
            if (!_pollThread.Join(StopWait))
            {
                // Stuck in an interaction handler that ignores its cancellation. The session is
                // closed regardless; release the reader rather than leave it on a queue nobody completes.
                Log($"[NativeSshExec] {_displayName}: the poll thread did not stop; releasing stdout");
                _stdoutQueue.Release();
                return;
            }
        }

        CloseHandle();
        _stop.Dispose();
    }

    /// <summary>At the end of stdout: the transport's failure, if it reported one.</summary>
    internal void ThrowIfTransportFailed()
    {
        if (_failure is { } failure)
        {
            throw new SshExecTransportException($"SSH to {_displayName} failed: {failure}", failure);
        }
    }

    internal void WriteStdin(ReadOnlySpan<byte> data)
    {
        NovaSshSafeHandle? handle = Volatile.Read(ref _handle);
        if (_ended || handle is null)
        {
            // As a closed pipe fails a write: nothing reads this stdin any more.
            throw new IOException($"The remote command on {_displayName} has ended; its stdin is closed.");
        }

        if (data.IsEmpty)
        {
            return;
        }

        try
        {
            // Queued at once: the native write never blocks. See the task-14 ABI notes on why large
            // stdin must not feed a command that floods its output.
            _interop.Write(handle, data);
        }
        catch (InvalidOperationException ex)
        {
            throw new IOException($"Writing to the remote command on {_displayName} failed: {ex.Message}", ex);
        }
    }

    internal void SendStdinEof()
    {
        NovaSshSafeHandle? handle = Volatile.Read(ref _handle);
        if (handle is null)
        {
            return; // The session is gone, and its stdin with it.
        }

        try
        {
            _interop.SendEof(handle);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            Log($"[NativeSshExec] {_displayName}: sending EOF: {ex.Message}");
        }
    }

    private void PollLoop()
    {
        bool closedByServer = false;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                NovaSshSafeHandle? handle = Volatile.Read(ref _handle);
                if (handle is null)
                {
                    break; // The channel closed the session.
                }

                NativeSshEvent? next = _interop.PollEvent(handle);
                if (next is null)
                {
                    Thread.Sleep(PollDelay);
                    continue;
                }

                switch (next.Kind)
                {
                    case NativeSshEventKind.Connected:
                        Log($"[NativeSshExec] {_displayName}: connected; the command is starting");
                        break;
                    case NativeSshEventKind.Data:
                        // The payload is a fresh array per event, so the queue takes it without a copy.
                        // Past the pause threshold this waits for the reader (see the class remarks).
                        _stdoutQueue.Enqueue(next.Payload, _stop.Token);
                        break;
                    case NativeSshEventKind.ExtendedData:
                        _stderrTail.Append(next.Payload);
                        break;
                    case NativeSshEventKind.HostKeyPrompt:
                    case NativeSshEventKind.PasswordPrompt:
                    case NativeSshEventKind.PassphrasePrompt:
                    case NativeSshEventKind.KeyboardInteractivePrompt:
                        // Waits here, as NativeSshSession's poll loop awaits it: the session wants the
                        // answer before anything else happens. This blocks the poll thread only; the
                        // handler shows its dialog on the UI thread and is awaited, never waited on there.
                        _promptResponder.RespondAsync(next, _interop, handle, _interactionHandler, _stop.Token)
                            .GetAwaiter().GetResult();
                        break;
                    case NativeSshEventKind.ExitStatus:
                        _exitCode = next.StatusCode;
                        break;
                    case NativeSshEventKind.Error:
                        RecordFailure(ErrorMessage(next.Payload));
                        break;
                    case NativeSshEventKind.Closed:
                        closedByServer = true;
                        return;
                    default:
                        // The forward-channel kinds cannot reach an exec session, which has no
                        // forward router; unknown kinds are skipped, as NativeSshSession skips them.
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            // A prompt cancelled by the channel's own stop is not a failure.
            if (!_stop.IsCancellationRequested)
            {
                RecordFailure($"the native session failed: {ex.Message}");
            }
        }
        finally
        {
            Finish(closedByServer);
        }
    }

    private void RecordFailure(string message)
    {
        _failure ??= message;
        _stderrTail.Append(Encoding.UTF8.GetBytes($"native ssh: {message}\n"));
        Log($"[NativeSshExec] {_displayName}: {message} (failure={NativeSshFailureClassifier.Classify(message).Kind})");
    }

    /// <summary>
    /// The end of the session, in order: the failure and the end are recorded first, so a reader that
    /// sees stdout's end also sees them. Then the handle closes and <see cref="Completion"/> resolves,
    /// with <see cref="StderrTail"/> complete. The log comes last, so it cannot hold anything up.
    /// </summary>
    private void Finish(bool closedByServer)
    {
        _ended = true;
        _stdoutQueue.Complete();
        CloseHandle();

        int? exitCode = _exitCode;
        _completion.TrySetResult(exitCode);

        string outcome = !closedByServer
            ? "stopped by the channel"
            : exitCode is { } code
                ? string.Create(CultureInfo.InvariantCulture, $"exited with {code}")
                : "ended without an exit status (a signal, or the connection was lost)";
        Log($"[NativeSshExec] {_displayName}: {outcome}");
    }

    private void CloseHandle()
    {
        NovaSshSafeHandle? handle = Interlocked.Exchange(ref _handle, null);
        if (handle is not null)
        {
            // nova_ssh_close, exactly once: the SafeHandle defers it past a poll or write in flight.
            _interop.Close(handle);
        }
    }

    /// <summary>
    /// Logs without letting the logger's failure escape: on the poll thread, an exception thrown past
    /// the loop's handler would end the process.
    /// </summary>
    private void Log(string message)
    {
        try
        {
            _log(message);
        }
        catch (Exception)
        {
            // Diagnostics only.
        }
    }

    /// <summary>The <c>{"message":"…"}</c> of an Error event, or its raw text.</summary>
    private static string ErrorMessage(byte[] payload)
    {
        if (payload.Length == 0)
        {
            return "Native SSH error";
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("message", out JsonElement message)
                && message.ValueKind == JsonValueKind.String
                && message.GetString() is { Length: > 0 } text)
            {
                return text;
            }
        }
        catch (JsonException)
        {
            // Not JSON: the raw text is the message.
        }

        return Encoding.UTF8.GetString(payload);
    }

    /// <summary>
    /// The read side of the stdout queue. At its end it reports the transport's failure, if there was
    /// one, as an <see cref="SshExecTransportException"/>; otherwise it returns 0.
    /// </summary>
    private sealed class StdoutStream : Stream
    {
        private readonly NativeSshExecChannel _channel;
        private readonly BoundedChunkQueue _queue;
        private volatile bool _disposed;

        public StdoutStream(NativeSshExecChannel channel, BoundedChunkQueue queue)
        {
            _channel = channel;
            _queue = queue;
        }

        public override bool CanRead => !_disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);
            return Read(buffer.AsSpan(offset, count));
        }

        /// <summary>The mux client's read: blocks on the queue's monitor, woken by the poll thread itself.</summary>
        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (buffer.IsEmpty)
            {
                return 0;
            }

            return AtEnd(_queue.Read(buffer, CancellationToken.None));
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ValidateBufferArguments(buffer, offset, count);
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        /// <summary>
        /// Completes synchronously when bytes are queued or stdout has ended. Otherwise it waits on a pool
        /// thread, and <paramref name="cancellationToken"/> ends that wait.
        /// </summary>
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromCanceled<int>(cancellationToken);
            }

            if (buffer.IsEmpty)
            {
                return ValueTask.FromResult(0);
            }

            try
            {
                if (_queue.TryRead(buffer.Span, out int read))
                {
                    return ValueTask.FromResult(AtEnd(read));
                }
            }
            catch (Exception ex)
            {
                return ValueTask.FromException<int>(ex);
            }

            return new ValueTask<int>(Task.Run(() => AtEnd(_queue.Read(buffer.Span, cancellationToken)), CancellationToken.None));
        }

        /// <summary>A 0 from the queue is the end: the transport's failure, unless the reader was released.</summary>
        private int AtEnd(int read)
        {
            if (read == 0 && !_queue.IsReleased)
            {
                _channel.ThrowIfTransportFailed();
            }

            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <summary>
        /// The reader is done. The queued stdout is dropped, a read in progress on another thread
        /// returns 0, and the poll thread drops later stdout instead of waiting for room.
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                _queue.Release();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// The command's stdin: each write goes to the native session's queue at once, and disposing
    /// sends EOF once. Writes fail with <see cref="IOException"/> once the command has ended, as a
    /// closed pipe's do.
    /// </summary>
    private sealed class StdinStream : Stream
    {
        private readonly NativeSshExecChannel _channel;
        private int _disposed;

        public StdinStream(NativeSshExecChannel channel)
        {
            _channel = channel;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => Volatile.Read(ref _disposed) == 0;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);
            Write(buffer.AsSpan(offset, count));
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _channel.WriteStdin(buffer);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ValidateBufferArguments(buffer, offset, count);
            return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        /// <summary>Synchronous underneath: the native write only queues.</summary>
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromCanceled(cancellationToken);
            }

            try
            {
                Write(buffer.Span);
                return ValueTask.CompletedTask;
            }
            catch (Exception ex)
            {
                return ValueTask.FromException(ex);
            }
        }

        /// <summary>A no-op, disposed or not: every write has already been queued.</summary>
        public override void Flush()
        {
        }

        /// <inheritdoc cref="Flush"/>
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _channel.SendStdinEof();
            }

            base.Dispose(disposing);
        }
    }
}
