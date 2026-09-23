using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Ntilde.Mux.Contracts;
using Ntilde.VT;

namespace Ntilde.Mux;

/// <summary>
/// One connection to a <see cref="MuxServer"/>. Its reader thread is the delivery thread for every
/// session opened on it: snapshots, output, resizes, exits and faults are raised there, strictly in
/// frame order. RPC continuations are forced asynchronous so no awaiting caller ever runs on it.
/// <see cref="Disconnected"/> (and each session's) is the exception: it is raised on whichever of the
/// reader thread, the sender thread or the <see cref="Dispose"/> caller ends the connection first.
/// </summary>
public sealed class MuxClient : IDisposable
{
    /// <summary>Disconnect reason when the transport simply went away: not an error code, so not in MuxErrorCodes.</summary>
    private const string ReasonDisconnected = "disconnected";

    private readonly Stream _stream;
    private readonly MuxClientOptions _options;
    private readonly Thread _readerThread;
    private readonly Thread _senderThread;
    private readonly BlockingCollection<MuxOutboundFrame> _outbound = new(boundedCapacity: 1024);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<MuxResponse>> _pending = new();
    private readonly ConcurrentDictionary<long, MuxClientSession> _pendingAttaches = new();

    /// <summary>
    /// Attach request ids a caller gave up on (cancelled or timed out) while the server had already
    /// committed to answering. A late Snapshot for one of these is dropped and detached instead of
    /// being treated as unsolicited (which would kill the whole connection over a race the caller,
    /// not the server, created).
    /// </summary>
    private readonly ConcurrentDictionary<long, MuxClientSession> _abandonedAttaches = new();

    private readonly ConcurrentDictionary<Guid, MuxClientSession> _sessions = new();
    private long _nextId;
    private int _disconnected;
    private string? _disconnectReason;

    private MuxClient(Stream stream, MuxClientOptions options)
    {
        _stream = stream;
        _options = options;
        _readerThread = new Thread(ReadLoop) { IsBackground = true, Name = "MuxClientRead" };
        _senderThread = new Thread(SendLoop) { IsBackground = true, Name = "MuxClientSend" };
        _senderThread.Start();
        _readerThread.Start();
    }

    public int ProtocolVersion { get; private set; }

    /// <summary>From Welcome. Construct the pane's parser with this so both halves of a snapshot parse identically.</summary>
    public bool ForceConPtyFiltering { get; private set; }

    public bool IsConnected => Volatile.Read(ref _disconnected) == 0;
    public string? DisconnectReason => Volatile.Read(ref _disconnectReason);
    public event Action<string?>? Disconnected;

    internal bool IsOnDeliveryThread => Thread.CurrentThread == _readerThread;

    /// <summary>What an attaching session may adopt: consulted by <see cref="MuxClientSession.DeliverResize"/> too, since a resize is just as capable of demanding an oversize buffer as an attach's snapshot.</summary>
    internal MuxAttachLimits AttachLimits => _options.AttachLimits;

    public static async Task<MuxClient> ConnectAsync(Stream stream, MuxClientOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= new MuxClientOptions();
        var client = new MuxClient(stream, options);
        try
        {
            WelcomeResult welcome = await client.RequestAsync(
                MuxMethods.Hello,
                new HelloParams { MinVersion = options.MinProtocolVersion, MaxVersion = options.MaxProtocolVersion, ClientKind = options.ClientKind },
                MuxJsonContext.Default.HelloParams,
                MuxJsonContext.Default.WelcomeResult,
                cancellationToken).ConfigureAwait(false);
            if (welcome.Version < options.MinProtocolVersion || welcome.Version > options.MaxProtocolVersion)
            {
                throw new MuxProtocolException(MuxErrorCodes.VersionMismatch,
                    $"Server chose protocol {welcome.Version}, outside this client's {options.MinProtocolVersion}..{options.MaxProtocolVersion}.");
            }

            client.ProtocolVersion = welcome.Version;
            client.ForceConPtyFiltering = welcome.ForceConPtyFiltering;
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(CancellationToken cancellationToken = default) =>
        (await RequestAsync(MuxMethods.ListSessions, new MuxEmpty(), MuxJsonContext.Default.MuxEmpty,
            MuxJsonContext.Default.ListSessionsResult, cancellationToken).ConfigureAwait(false)).Sessions;

    public async Task<Guid> SpawnAsync(SpawnParams request, CancellationToken cancellationToken = default) =>
        (await RequestAsync(MuxMethods.Spawn, request, MuxJsonContext.Default.SpawnParams,
            MuxJsonContext.Default.SpawnResult, cancellationToken).ConfigureAwait(false)).SessionId;

    public Task KillAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        RequestAsync(MuxMethods.Kill, new SessionIdParams { SessionId = sessionId }, MuxJsonContext.Default.SessionIdParams,
            MuxJsonContext.Default.MuxEmpty, cancellationToken);

    public Task PingAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(MuxMethods.Ping, new MuxEmpty(), MuxJsonContext.Default.MuxEmpty, MuxJsonContext.Default.MuxEmpty, cancellationToken);

    /// <summary>
    /// An unattached session. Wire its events, then call <see cref="MuxClientSession.AttachAsync"/>:
    /// nothing is raised before a handler can exist, so nothing needs buffering (spec §9.6).
    /// </summary>
    public MuxClientSession OpenSession(Guid sessionId, string shellCommand = "", string? shellArguments = null)
    {
        var session = new MuxClientSession(this, sessionId, shellCommand, shellArguments);
        if (!_sessions.TryAdd(sessionId, session))
        {
            throw new InvalidOperationException($"Session {sessionId} is already open on this client; dispose it first.");
        }

        return session;
    }

    internal Task<TResult> RequestAsync<TParams, TResult>(
        string method, TParams parameters, JsonTypeInfo<TParams> paramsInfo, JsonTypeInfo<TResult> resultInfo, CancellationToken cancellationToken)
        where TResult : class
    {
        long id = Interlocked.Increment(ref _nextId);
        return RequestCoreAsync(id, method, MuxFrames.ToElement(parameters, paramsInfo), resultInfo, cancellationToken);
    }

    /// <remarks>
    /// Cancellation (or the request timeout) races the reader thread for the snapshot. Whoever
    /// removes the id from <see cref="_pendingAttaches"/> first owns the outcome: the reader
    /// delivers the snapshot, or the caller abandons the attach (and a late snapshot is undone with
    /// a detach naming this attach). The request's <see cref="_pending"/> entry therefore stays
    /// registered until that is decided, so a reader that won can still complete it - and a
    /// cancellation that lost reports the attach that actually happened instead of contradicting
    /// the <see cref="MuxClientSession.SnapshotReceived"/> the pane has just handled.
    /// </remarks>
    internal async Task<long> AttachAsync(MuxClientSession session, int maxScrollbackRows, MuxPresentation presentation, CancellationToken cancellationToken)
    {
        long id = Interlocked.Increment(ref _nextId);
        JsonElement p = MuxFrames.ToElement(
            new AttachParams { SessionId = session.Id, MaxScrollbackRows = maxScrollbackRows, Presentation = presentation },
            MuxJsonContext.Default.AttachParams);
        var tcs = new TaskCompletionSource<MuxResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        _pendingAttaches[id] = session;
        try
        {
            if (!IsConnected) throw Closed(); // after registering: OnDisconnected sets the flag before failing _pending
            Enqueue(MuxFrames.Request(new MuxRequest { Id = id, Method = MuxMethods.Attach, Params = p }));

            MuxResponse response;
            try
            {
                response = await tcs.Task.WaitAsync(_options.RequestTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                // Abandoned FIRST, pending second: the reader must always find the id in one of
                // the two maps, or a late snapshot would read as unsolicited and kill the connection.
                _abandonedAttaches[id] = session;
                if (_pendingAttaches.TryRemove(id, out _))
                {
                    // An error reply that already arrived means no snapshot will follow for this id.
                    if (tcs.Task.IsCompletedSuccessfully && tcs.Task.Result.Error is not null) _abandonedAttaches.TryRemove(id, out _);
                    throw;
                }

                // The reader claimed the snapshot first and is delivering it right now; it always
                // completes tcs (with the outcome, or by failing the connection).
                _abandonedAttaches.TryRemove(id, out _);
                response = await tcs.Task.ConfigureAwait(false);
            }

            if (response.Error is { } error) throw new MuxProtocolException(error.Code, error.Message);

            // A successful attach's reply IS the Snapshot frame; OnSnapshot removes this id from
            // _pendingAttaches only when it delivers one. Still present here means the server sent a
            // bare success Response instead - a protocol violation, not a session with nothing to show.
            if (_pendingAttaches.ContainsKey(id))
            {
                throw new MuxProtocolException(MuxErrorCodes.ProtocolError,
                    $"attach {id} for session {session.Id} completed without a snapshot.");
            }

            return session.AttachedSeq;
        }
        finally
        {
            _pending.TryRemove(id, out _);
            _pendingAttaches.TryRemove(id, out _);
        }
    }

    /// <summary>Fire-and-forget request (id 0: the server sends no response).</summary>
    internal void PostRequest<T>(string method, T parameters, JsonTypeInfo<T> typeInfo) =>
        Post(MuxFrames.Request(new MuxRequest { Id = 0, Method = method, Params = MuxFrames.ToElement(parameters, typeInfo) }));

    internal void SendInput(Guid sessionId, string text) => Post(MuxFrames.Input(sessionId, Encoding.UTF8.GetBytes(text)));

    internal void SendResize(Guid sessionId, int cols, int rows, MuxPresentation? presentation) =>
        PostRequest(MuxMethods.Resize, new ResizeParams { SessionId = sessionId, Cols = cols, Rows = rows, Presentation = presentation },
            MuxJsonContext.Default.ResizeParams);

    internal void Detach(MuxClientSession session)
    {
        _sessions.TryRemove(new KeyValuePair<Guid, MuxClientSession>(session.Id, session));
        PostRequest(MuxMethods.Detach, new DetachParams { SessionId = session.Id }, MuxJsonContext.Default.DetachParams);
    }

    /// <summary>
    /// Undoes the subscription one specific attach made. The server ignores it if this connection
    /// has sent a newer attach for the session since (spec §6, <see cref="DetachParams.AttachRequestId"/>).
    /// </summary>
    private void DetachAttach(Guid sessionId, long attachRequestId) =>
        PostRequest(MuxMethods.Detach, new DetachParams { SessionId = sessionId, AttachRequestId = attachRequestId }, MuxJsonContext.Default.DetachParams);

    public void Dispose()
    {
        OnDisconnected("client_disposed");
        if (Thread.CurrentThread != _readerThread) _readerThread.Join(TimeSpan.FromSeconds(5));
        if (Thread.CurrentThread != _senderThread) _senderThread.Join(TimeSpan.FromSeconds(5));
    }

    private async Task<TResult> RequestCoreAsync<TResult>(long id, string method, JsonElement parameters, JsonTypeInfo<TResult> resultInfo, CancellationToken cancellationToken)
        where TResult : class
    {
        MuxResponse response = await SendAndAwaitAsync(id, method, parameters, cancellationToken).ConfigureAwait(false);
        if (response.Error is { } error) throw new MuxProtocolException(error.Code, error.Message);
        return MuxFrames.ParseParams(response.Result, resultInfo);
    }

    private async Task<MuxResponse> SendAndAwaitAsync(long id, string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<MuxResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            if (!IsConnected) throw Closed(); // after registering: OnDisconnected sets the flag before failing _pending
            Enqueue(MuxFrames.Request(new MuxRequest { Id = id, Method = method, Params = parameters }));
            return await tcs.Task.WaitAsync(_options.RequestTimeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private void Enqueue(MuxOutboundFrame frame)
    {
        try
        {
            _outbound.Add(frame);
        }
        catch (InvalidOperationException)
        {
            frame.Release();
            throw Closed();
        }
    }

    private void Post(MuxOutboundFrame frame)
    {
        try
        {
            _outbound.Add(frame);
        }
        catch (InvalidOperationException)
        {
            frame.Release(); // disconnected: fire-and-forget has nobody to tell
        }
    }

    private IOException Closed() => new($"The mux connection is closed ({DisconnectReason ?? ReasonDisconnected}).");

    private void SendLoop()
    {
        try
        {
            foreach (MuxOutboundFrame frame in _outbound.GetConsumingEnumerable())
            {
                try { frame.WriteTo(_stream); }
                finally { frame.Release(); }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or NotSupportedException)
        {
            OnDisconnected(ReasonDisconnected);
        }

        while (_outbound.TryTake(out MuxOutboundFrame? left)) left.Release();
    }

    private void ReadLoop()
    {
        string? reason = null;
        try
        {
            while (true)
            {
                MuxInboundFrame? frame = MuxFrameReader.Read(_stream);
                if (frame is null) break;
                using (frame)
                {
                    Dispatch(frame);
                }
            }
        }
        catch (MuxProtocolException ex)
        {
            reason = ex.Code;
            _options.Log?.Invoke($"[MuxClient] protocol error: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            reason = ReasonDisconnected;
        }
        catch (Exception ex)
        {
            reason = MuxErrorCodes.Internal;
            _options.Log?.Invoke($"[MuxClient] delivery failed: {ex}");
        }

        OnDisconnected(reason);
    }

    private void Dispatch(MuxInboundFrame frame)
    {
        switch (frame.Kind)
        {
            case MuxFrameKind.Response:
                OnResponse(MuxFrames.ParseJson(frame.Payload, MuxJsonContext.Default.MuxResponse));
                break;
            case MuxFrameKind.Notification:
                OnNotification(MuxFrames.ParseJson(frame.Payload, MuxJsonContext.Default.MuxNotification));
                break;
            case MuxFrameKind.Output:
                {
                    if (!MuxFrames.TryParseOutput(frame.Payload, out Guid id, out long seq, out ReadOnlySpan<byte> data)) throw Malformed(frame.Kind);
                    if (_sessions.TryGetValue(id, out MuxClientSession? session)) session.DeliverOutput(seq, data);
                    break;
                }

            case MuxFrameKind.ResizeEvent:
                {
                    if (!MuxFrames.TryParseResizeEvent(frame.Payload, out Guid id, out long seq, out int cols, out int rows)) throw Malformed(frame.Kind);
                    if (_sessions.TryGetValue(id, out MuxClientSession? session)) session.DeliverResize(seq, cols, rows);
                    break;
                }

            case MuxFrameKind.Snapshot:
                OnSnapshot(frame.Payload);
                break;
            default:
                throw new MuxProtocolException(MuxErrorCodes.ProtocolError, $"A server may not send {frame.Kind} frames.");
        }
    }

    private static MuxProtocolException Malformed(MuxFrameKind kind) =>
        new(MuxErrorCodes.ProtocolError, $"Malformed {kind} frame.");

    private void OnResponse(MuxResponse response)
    {
        if (response.Id == 0)
        {
            // Connection-level: the server is about to close. Keep the reason for DisconnectReason.
            if (response.Error is { } error) Interlocked.CompareExchange(ref _disconnectReason, error.Code, null);
            return;
        }

        if (_pending.TryGetValue(response.Id, out TaskCompletionSource<MuxResponse>? tcs)) tcs.TrySetResult(response);

        // After completing tcs (AttachAsync's abandon path checks it the other way round): a reply
        // to an abandoned attach means no Snapshot will follow for this id either.
        _abandonedAttaches.TryRemove(response.Id, out _);
    }

    private void OnNotification(MuxNotification notification)
    {
        if (notification.Method == MuxMethods.Exited)
        {
            ExitedNotification exited = MuxFrames.ParseParams(notification.Params, MuxJsonContext.Default.ExitedNotification);
            if (_sessions.TryGetValue(exited.SessionId, out MuxClientSession? session)) session.DeliverExited(exited.ExitCode);
        }
        else if (notification.Method == MuxMethods.Faulted)
        {
            // Session-level, never connection-level: only that session's stream ends.
            FaultedNotification faulted = MuxFrames.ParseParams(notification.Params, MuxJsonContext.Default.FaultedNotification);
            if (_sessions.TryGetValue(faulted.SessionId, out MuxClientSession? session)) session.DeliverFaulted(faulted.Message ?? "The mux session faulted.");
        }

        // Unknown notifications are ignored: a newer server may send more than this client knows.
    }

    private void OnSnapshot(ReadOnlySpan<byte> payload)
    {
        if (!MuxFrames.TryParseSnapshot(payload, out long requestId, out Guid sessionId, out long seq, out ReadOnlySpan<byte> json))
        {
            throw Malformed(MuxFrameKind.Snapshot);
        }

        if (!_pendingAttaches.TryRemove(requestId, out MuxClientSession? session))
        {
            if (_abandonedAttaches.TryRemove(requestId, out MuxClientSession? abandoned) && abandoned.Id == sessionId)
            {
                // The caller gave up on this attach before the snapshot arrived. The server already
                // subscribed us; undo that instead of treating a frame this client itself solicited
                // as unsolicited (which would tear down every session on this connection). The
                // detach names this attach, so a retry the caller has sent since is left alone.
                DetachAttach(sessionId, requestId);
                return;
            }

            throw new MuxProtocolException(MuxErrorCodes.ProtocolError, $"Unsolicited snapshot for {sessionId} (request {requestId}).");
        }

        if (session.Id != sessionId)
        {
            throw new MuxProtocolException(MuxErrorCodes.ProtocolError, $"Unsolicited snapshot for {sessionId} (request {requestId}).");
        }

        _pending.TryGetValue(requestId, out TaskCompletionSource<MuxResponse>? tcs);
        TerminalStateSnapshot snapshot;
        try
        {
            snapshot = DecodeSnapshot(json);
        }
        catch (MuxProtocolException ex)
        {
            tcs?.TrySetResult(new MuxResponse { Id = requestId, Error = new MuxError { Code = ex.Code, Message = ex.Message } });
            if (ex.Code != MuxErrorCodes.SnapshotTooLarge) throw; // malformed: this connection is no longer trustworthy

            // Too large is a policy refusal, not corruption: the server already subscribed us, so
            // undo that - this attach's subscription only, never a newer attach's.
            DetachAttach(sessionId, requestId);
            return;
        }

        session.DeliverSnapshot(seq, snapshot, json.Length);
        tcs?.TrySetResult(new MuxResponse { Id = requestId });
    }

    private TerminalStateSnapshot DecodeSnapshot(ReadOnlySpan<byte> json)
    {
        MuxAttachLimits limits = _options.AttachLimits;
        if (json.Length > limits.MaxSnapshotBytes)
        {
            throw new MuxProtocolException(MuxErrorCodes.SnapshotTooLarge,
                $"Snapshot is {json.Length} bytes; this client accepts at most {limits.MaxSnapshotBytes}.");
        }

        TerminalStateSnapshot snapshot;
        try
        {
            snapshot = TerminalStateSerializer.FromBytes(json);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            throw new MuxProtocolException(MuxErrorCodes.ProtocolError, $"Malformed snapshot: {ex.Message}", ex);
        }

        if (snapshot.Cols <= 0 || snapshot.Rows <= 0)
        {
            throw new MuxProtocolException(MuxErrorCodes.ProtocolError, $"Snapshot geometry {snapshot.Cols}x{snapshot.Rows} is invalid.");
        }

        if ((long)snapshot.Cols * snapshot.Rows > limits.MaxCells)
        {
            throw new MuxProtocolException(MuxErrorCodes.SnapshotTooLarge,
                $"Snapshot grid {snapshot.Cols}x{snapshot.Rows} exceeds this client's {limits.MaxCells}-cell ceiling.");
        }

        if (snapshot.ScrollbackRowCount > limits.MaxScrollbackRows)
        {
            throw new MuxProtocolException(MuxErrorCodes.SnapshotTooLarge,
                $"Snapshot carries {snapshot.ScrollbackRowCount} scrollback rows; this client accepts at most {limits.MaxScrollbackRows}.");
        }

        return snapshot;
    }

    private void OnDisconnected(string? reason)
    {
        if (Interlocked.Exchange(ref _disconnected, 1) != 0) return;
        Interlocked.CompareExchange(ref _disconnectReason, reason ?? ReasonDisconnected, null);
        _outbound.CompleteAdding();
        try { _stream.Dispose(); }
        catch (IOException) { /* closing a transport the peer already dropped - the goal is reached */ }

        IOException closed = Closed();
        foreach (TaskCompletionSource<MuxResponse> tcs in _pending.Values) tcs.TrySetException(closed);
        foreach (MuxClientSession session in _sessions.Values) session.DeliverDisconnected(DisconnectReason);
        Disconnected?.Invoke(DisconnectReason);
    }
}
