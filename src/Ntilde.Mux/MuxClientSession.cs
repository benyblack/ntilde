using Ntilde.Mux.Contracts;
using Ntilde.Pty;
using Ntilde.Replay;
using Ntilde.VT;

namespace Ntilde.Mux;

/// <summary>
/// A pane's view of a multiplexed session. The pane keeps its own parser and buffer: restore on
/// <see cref="SnapshotReceived"/>, parse <see cref="OnOutputReceived"/>, resize on
/// <see cref="StreamResize"/> - exactly where the event sits in the stream, never ahead of it
/// (<see cref="OrdersResizeInStream"/>). Device queries are answered by the mux
/// (<see cref="AnswersDeviceQueries"/>).
/// </summary>
/// <remarks>
/// <b>Which thread raises what.</b> The stream events - <see cref="SnapshotReceived"/>,
/// <see cref="OnOutputReceived"/>, <see cref="StreamResize"/>, <see cref="OnExit"/> and
/// <see cref="Faulted"/> - are raised on the owning <see cref="MuxClient"/>'s reader thread, one at a
/// time and strictly in frame order. <see cref="Disconnected"/> is NOT confined to it: it fires on
/// whichever thread notices the connection ending first - the reader thread (end of stream, a
/// protocol error, a throwing handler), the sender thread (a failed write), or the thread that calls
/// <see cref="MuxClient.Dispose"/> - and
/// it can therefore run concurrently with a stream event still in progress on the reader thread.
/// A handler that touches UI state or the pane's buffer must marshal for <see cref="Disconnected"/>
/// even if it relies on the reader thread's ordering for everything else. It is raised at most once.
/// </remarks>
public sealed class MuxClientSession : ITerminalSession, ITerminalSessionCapabilities
{
    private const int MaxIncompleteUtf8Bytes = 3;
    private static readonly long SessionInfoMaxAgeMs = 1000;

    private readonly MuxClient _client;
    private Utf8ChunkDecoder _decoder = new();
    private char[] _chars = new char[Utf8ChunkDecoder.GetMaxCharCount(4096)];
    private long _expectedOffset = -1;   // next raw offset this session accepts; -1 = not attached
    private long _deliveredOffset;       // advanced after handlers return (what tests wait on)
    private long _attachedSeq = -1;
    private MuxPresentation? _presentation;
    private int _exited;
    private int _exitCode;
    private int _exitNotified;
    private int _faulted;
    private int _hasActiveChildren;
    private long _sessionInfoAtMs = long.MinValue / 2;
    private int _sessionInfoInFlight;
    private int _recording;
    private int _flightRecording;
    private int _disposed;

    internal MuxClientSession(MuxClient client, Guid sessionId, string shellCommand, string? shellArguments)
    {
        _client = client;
        Id = sessionId;
        ShellCommand = shellCommand;
        ShellArguments = shellArguments;
    }

    public Guid Id { get; }
    public string ShellCommand { get; }
    public string? ShellArguments { get; }
    public bool AnswersDeviceQueries => true;
    public bool OrdersResizeInStream => true;
    public bool ForceConPtyFiltering => _client.ForceConPtyFiltering;
    public bool IsAttached => Interlocked.Read(ref _expectedOffset) >= 0;
    public long AttachedSeq => Interlocked.Read(ref _attachedSeq);

    /// <summary>Raw stream offset whose effects have been delivered to every handler.</summary>
    public long StreamPosition => Interlocked.Read(ref _deliveredOffset);

    internal int LastSnapshotByteCount { get; private set; }

    public event Action<TerminalStateSnapshot>? SnapshotReceived;
    public event Action<string>? OnOutputReceived;
    public event Action<int, int>? StreamResize;
    public event Action<int>? OnExit;
    public event Action<string?>? Disconnected;

    /// <summary>
    /// The mux's parser for this session failed: its stream has ended and the server has dropped
    /// this subscription (the connection and every other session carry on). The child may still be
    /// running, but nothing more will be delivered for it; the argument is a human-readable reason.
    /// </summary>
    public event Action<string>? Faulted;

    /// <summary>True once <see cref="Faulted"/> has been raised.</summary>
    public bool IsFaulted => Volatile.Read(ref _faulted) != 0;

    /// <summary>Returns the snapshot's <c>StreamSeq</c>. <see cref="SnapshotReceived"/> has fired by then.</summary>
    public Task<long> AttachAsync(int maxScrollbackRows, MuxPresentation presentation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _presentation = presentation;
        return _client.AttachAsync(this, maxScrollbackRows, presentation, cancellationToken);
    }

    public void SendInput(string input)
    {
        if (string.IsNullOrEmpty(input) || Volatile.Read(ref _disposed) != 0) return;
        _client.SendInput(Id, input);
    }

    /// <summary>Requests a resize. Does not raise <see cref="StreamResize"/>: that comes back in-stream.</summary>
    public void Resize(int cols, int rows)
    {
        if (cols <= 0 || rows <= 0 || Volatile.Read(ref _disposed) != 0) return;
        MuxPresentation? presentation = _presentation is { } current ? current with { Cols = cols, Rows = rows } : null;
        if (presentation is not null) _presentation = presentation;
        _client.SendResize(Id, cols, rows, presentation);
    }

    /// <summary>Pushes new cell metrics / colours (theme or font change). Latest client wins.</summary>
    public void UpdatePresentation(MuxPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        _presentation = presentation;
        _client.SendResize(Id, presentation.Cols, presentation.Rows, presentation);
    }

    public bool IsProcessRunning => Volatile.Read(ref _exited) == 0;
    public int? ExitCode => Volatile.Read(ref _exited) != 0 ? Volatile.Read(ref _exitCode) : null;

    /// <summary>The last probed value; a stale read kicks off a refresh and returns immediately.</summary>
    public bool HasActiveChildProcesses
    {
        get
        {
            RefreshSessionInfoIfStale();
            return Volatile.Read(ref _hasActiveChildren) != 0;
        }
    }

    public async Task<SessionInfoResult> RefreshSessionInfoAsync(CancellationToken cancellationToken = default)
    {
        SessionInfoResult info = await _client.RequestAsync(MuxMethods.SessionInfo, new SessionIdParams { SessionId = Id },
            MuxJsonContext.Default.SessionIdParams, MuxJsonContext.Default.SessionInfoResult, cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _hasActiveChildren, info.HasActiveChildProcesses ? 1 : 0);
        Interlocked.Exchange(ref _sessionInfoAtMs, Environment.TickCount64);
        return info;
    }

    public bool IsRecording => Volatile.Read(ref _recording) != 0;

    /// <summary>The path is on the mux's machine (the same one, in Phase 1-3).</summary>
    public void StartRecording(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        Volatile.Write(ref _recording, 1);
        _client.PostRequest(MuxMethods.StartRecording, new StartRecordingParams { SessionId = Id, Path = filePath }, MuxJsonContext.Default.StartRecordingParams);
    }

    public void StopRecording()
    {
        Volatile.Write(ref _recording, 0);
        _client.PostRequest(MuxMethods.StopRecording, new SessionIdParams { SessionId = Id }, MuxJsonContext.Default.SessionIdParams);
    }

    public bool IsFlightRecording => Volatile.Read(ref _flightRecording) != 0;

    public void EnableFlightRecording(long maxTotalBytes)
    {
        Volatile.Write(ref _flightRecording, 1);
        _client.PostRequest(MuxMethods.EnableFlightRecording, new EnableFlightRecordingParams { SessionId = Id, MaxBytes = maxTotalBytes },
            MuxJsonContext.Default.EnableFlightRecordingParams);
    }

    public void DisableFlightRecording()
    {
        Volatile.Write(ref _flightRecording, 0);
        _client.PostRequest(MuxMethods.DisableFlightRecording, new SessionIdParams { SessionId = Id }, MuxJsonContext.Default.SessionIdParams);
    }

    public Task<ExportFlightResult> ExportFlightRecordingAsync(CancellationToken cancellationToken = default) =>
        _client.RequestAsync(MuxMethods.ExportFlight, new SessionIdParams { SessionId = Id },
            MuxJsonContext.Default.SessionIdParams, MuxJsonContext.Default.ExportFlightResult, cancellationToken);

    /// <summary>
    /// Synchronous by interface. Blocking is safe because responses complete on the reader thread
    /// with continuations forced asynchronous - which is also why calling it FROM a delivery handler
    /// (on that reader thread) would deadlock, and is refused.
    /// </summary>
    public bool TryExportFlightRecording(string filePath, out FlightExportInfo info)
    {
        if (_client.IsOnDeliveryThread)
        {
            throw new InvalidOperationException("TryExportFlightRecording cannot run on the mux delivery thread.");
        }

        try
        {
            ExportFlightResult result = ExportFlightRecordingAsync().GetAwaiter().GetResult();
            if (!result.Exported || result.Bytes is null)
            {
                info = default;
                return false;
            }

            File.WriteAllBytes(filePath, result.Bytes);
            info = new FlightExportInfo(result.EventCount, result.FirstEventMs, result.LastEventMs, result.TruncatedAtStart);
            return true;
        }
        catch (Exception ex) when (ex is IOException or MuxProtocolException or TimeoutException)
        {
            info = default;
            return false;
        }
    }

    public Task KillAsync(CancellationToken cancellationToken = default) => _client.KillAsync(Id, cancellationToken);

    public void Kill() => _client.PostRequest(MuxMethods.Kill, new SessionIdParams { SessionId = Id }, MuxJsonContext.Default.SessionIdParams);

    /// <summary>Detaches. The session keeps running in the mux; use <see cref="Kill"/> to end it.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Interlocked.Exchange(ref _expectedOffset, -1);
        _client.Detach(this);
    }

    internal void DeliverSnapshot(long seq, TerminalStateSnapshot snapshot, int byteCount)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (seq != snapshot.StreamSeq)
        {
            throw new MuxProtocolException(MuxErrorCodes.ProtocolError, $"Snapshot frame says seq {seq}; its payload says {snapshot.StreamSeq}.");
        }

        if (snapshot.StreamSeq < 0)
        {
            throw new MuxProtocolException(MuxErrorCodes.ProtocolError, $"Snapshot StreamSeq {snapshot.StreamSeq} is negative.");
        }

        byte[] tail = snapshot.DecoderTail ?? [];
        if (tail.Length > MaxIncompleteUtf8Bytes)
        {
            throw new MuxProtocolException(MuxErrorCodes.ProtocolError, $"Decoder tail of {tail.Length} bytes cannot be an incomplete UTF-8 sequence.");
        }

        if (snapshot.StreamSeq > long.MaxValue - tail.Length)
        {
            throw new MuxProtocolException(MuxErrorCodes.ProtocolError,
                $"Snapshot StreamSeq {snapshot.StreamSeq} plus a {tail.Length}-byte tail overflows a 64-bit stream offset.");
        }

        // Seed a fresh decoder with the pending bytes: they complete with the first Output's bytes.
        var decoder = new Utf8ChunkDecoder();
        if (tail.Length > 0 && decoder.Decode(tail, _chars) != 0)
        {
            throw new MuxProtocolException(MuxErrorCodes.ProtocolError, "Decoder tail decoded to text; it must be an incomplete UTF-8 prefix.");
        }

        _decoder = decoder;
        long next = snapshot.StreamSeq + tail.Length;
        LastSnapshotByteCount = byteCount;
        Interlocked.Exchange(ref _attachedSeq, snapshot.StreamSeq);
        Interlocked.Exchange(ref _expectedOffset, next);
        SnapshotReceived?.Invoke(snapshot);
        Interlocked.Exchange(ref _deliveredOffset, next);
    }

    internal void DeliverOutput(long seq, ReadOnlySpan<byte> data)
    {
        long expected = Interlocked.Read(ref _expectedOffset);
        if (expected < 0) return; // not attached, or detached with frames still in flight
        if (seq != expected)
        {
            throw new MuxProtocolException(MuxErrorCodes.ProtocolError,
                $"Session {Id}: output at stream offset {seq}, expected {expected}. Healing a gap here would silently desynchronise the pane.");
        }

        long next = expected + data.Length;
        if (Interlocked.CompareExchange(ref _expectedOffset, next, expected) != expected) return; // detached meanwhile

        int maxChars = Utf8ChunkDecoder.GetMaxCharCount(data.Length);
        if (_chars.Length < maxChars) _chars = new char[maxChars];
        int count = _decoder.Decode(data, _chars);
        if (count > 0) OnOutputReceived?.Invoke(new string(_chars, 0, count));
        Interlocked.Exchange(ref _deliveredOffset, next);
    }

    internal void DeliverResize(long seq, int cols, int rows)
    {
        long expected = Interlocked.Read(ref _expectedOffset);
        if (expected < 0) return;
        if (seq != expected || cols <= 0 || rows <= 0)
        {
            throw new MuxProtocolException(MuxErrorCodes.ProtocolError,
                $"Session {Id}: resize {cols}x{rows} at offset {seq}, expected offset {expected}.");
        }

        // MaxCells guards a snapshot's grid, but an in-stream resize can demand the same oversize
        // TerminalBuffer just as well - and unlike attach, there is no earlier point to refuse it at.
        long cells = (long)cols * rows;
        if (cells > _client.AttachLimits.MaxCells)
        {
            throw new MuxProtocolException(MuxErrorCodes.ProtocolError,
                $"Session {Id}: resize grid {cols}x{rows} exceeds this client's {_client.AttachLimits.MaxCells}-cell ceiling.");
        }

        StreamResize?.Invoke(cols, rows);
    }

    internal void DeliverExited(int exitCode)
    {
        Volatile.Write(ref _exitCode, exitCode);
        Volatile.Write(ref _exited, 1);
        if (Interlocked.Exchange(ref _exitNotified, 1) == 0) OnExit?.Invoke(exitCode);
    }

    internal void DeliverFaulted(string message)
    {
        // Detach locally: the stream has explicitly ended, so any frame still in flight for this
        // session is ignored rather than read as a gap.
        Interlocked.Exchange(ref _expectedOffset, -1);
        if (Interlocked.Exchange(ref _faulted, 1) == 0) Faulted?.Invoke(message);
    }

    internal void DeliverDisconnected(string? reason)
    {
        Interlocked.Exchange(ref _expectedOffset, -1);
        Disconnected?.Invoke(reason);
    }

    private void RefreshSessionInfoIfStale()
    {
        if (Environment.TickCount64 - Interlocked.Read(ref _sessionInfoAtMs) < SessionInfoMaxAgeMs) return;
        if (!_client.IsConnected || Interlocked.Exchange(ref _sessionInfoInFlight, 1) != 0) return;

        // Task.Run, not a direct call: an async method runs synchronously on the CALLER's thread up
        // to its first true suspension point, and over a fast transport the whole request/response
        // round trip can race ahead and complete on other threads before this thread reaches that
        // point - which would make the "kicks off a refresh and returns immediately" cached read
        // above observe the NEW value instead, contradicting this property's contract.
        _ = Task.Run(RefreshInBackgroundAsync);
    }

    private async Task RefreshInBackgroundAsync()
    {
        try
        {
            await RefreshSessionInfoAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or MuxProtocolException or TimeoutException)
        {
            // The cached value stands; the next read retries.
        }
        finally
        {
            Volatile.Write(ref _sessionInfoInFlight, 0);
        }
    }
}
