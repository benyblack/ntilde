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
/// reader thread, the sender thread, the <see cref="Dispose"/> caller or a caller whose send overflowed
/// (<see cref="MuxClientOptions.MaxOverflowBytes"/>) ends the connection first.
/// </summary>
/// <remarks>
/// No send blocks its caller (Phase 5, ruling R6): the UI thread types, resizes, detaches and kills through
/// here, and a stalled link must not freeze it. A frame that finds the send queue full waits in an overflow
/// behind it, and frames reach the wire in the order of their calls whichever way they went.
/// </remarks>
public sealed class MuxClient : IDisposable
{
    /// <summary>Disconnect reason when the transport simply went away: not an error code, so not in MuxErrorCodes.</summary>
    private const string ReasonDisconnected = "disconnected";

    /// <summary>Disconnect reason when more than <see cref="MuxClientOptions.MaxOverflowBytes"/> waited behind a stalled link.</summary>
    private const string ReasonSendOverflow = "send overflow";

    /// <summary>
    /// How many frames may wait for the sender: once that many do, a frame sent next waits in the overflow
    /// (<see cref="Send"/>) until the link takes one. Tests fill it to stand in for a stalled link.
    /// </summary>
    internal const int OutboundCapacity = 1024;

    private readonly Stream _stream;  // the connection: the sender writes it, and closing it ends the reader
    private readonly Stream _inbound; // what the reader reads: _stream, stamping LastReceivedTicks as bytes arrive
    private readonly MuxClientOptions _options;
    private readonly Thread _readerThread;
    private readonly Thread _senderThread;
    private readonly BlockingCollection<MuxOutboundFrame> _outbound = new(boundedCapacity: OutboundCapacity);

    /// <summary>
    /// Guards the overflow, and orders every send against every other and against <see cref="OnDisconnected"/>'s
    /// <c>CompleteAdding</c>. Held only for non-blocking work.
    /// </summary>
    private readonly object _sendGate = new();

    /// <summary>
    /// Frames sent while <see cref="_outbound"/> was full, or while earlier ones still waited here: in call order.
    /// The pump moves the head into <see cref="_outbound"/>, and dequeues it only once it is in, so a later send
    /// cannot overtake it.
    /// </summary>
    private readonly Queue<MuxOutboundFrame> _overflow = new();

    private long _overflowBytes; // guarded by _sendGate: the payload bytes waiting in _overflow
    private bool _pumping;       // guarded by _sendGate: a PumpOverflow work item is queued or running, and owns the head

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
    private long _lastReceivedTicks; // written by the reader thread only (through _inbound)

    private MuxClient(Stream stream, MuxClientOptions options)
    {
        _stream = stream;
        _inbound = new ProgressStampingStream(stream, this);
        _options = options;
        _lastReceivedTicks = Environment.TickCount64;
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

    /// <summary>
    /// <see cref="Environment.TickCount64"/> when inbound bytes last arrived - part of any frame, a reply, a
    /// notification, output - or when the connection was made, before any. Stamped on every read that
    /// brings bytes, not once a frame is complete: a single large frame (a snapshot of up to tens of MiB)
    /// trickling in over a slow link proves the link alive while it arrives. A remote host's liveness ping
    /// counts it while its own answer waits behind such a frame (Phase 4 spec §7.2). Public because that host
    /// lives in another assembly; it costs the reader one volatile write per read.
    /// </summary>
    public long LastReceivedTicks => Volatile.Read(ref _lastReceivedTicks);

    internal bool IsOnDeliveryThread => Thread.CurrentThread == _readerThread;

    /// <summary>Tests: requests sent (or still queued to send) whose reply has not arrived yet.</summary>
    internal int PendingRequestCount => _pending.Count;

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
                new HelloParams { MinVersion = options.MinProtocolVersion, MaxVersion = options.MaxProtocolVersion, ClientKind = options.ClientKind, ClientInstanceId = options.ClientInstanceId },
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

    public Task<Guid> SpawnAsync(SpawnParams request, CancellationToken cancellationToken = default) =>
        SpawnAsync(request, sessionId: null, cancellationToken);

    /// <summary>
    /// Spawns a session that takes <paramref name="sessionId"/> (Phase 4 spec §3, codex E1): a caller whose reply
    /// is lost still knows which session to end. It is written over <paramref name="request"/>'s own
    /// <see cref="SpawnParams.SessionId"/>; null leaves <paramref name="request"/> as it is, exactly as
    /// <see cref="SpawnAsync(SpawnParams, CancellationToken)"/> sends it - the daemon picks the id unless the request
    /// names one itself. An id the daemon already has is refused (<see cref="MuxErrorCodes.SessionExists"/>); a daemon
    /// older than the field ignores it and picks its own, so the result - the id the daemon used - is the one to open.
    /// </summary>
    public async Task<Guid> SpawnAsync(SpawnParams request, Guid? sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        SpawnParams p = sessionId is Guid id ? request with { SessionId = id.ToString("D") } : request;
        return (await RequestAsync(MuxMethods.Spawn, p, MuxJsonContext.Default.SpawnParams,
            MuxJsonContext.Default.SpawnResult, cancellationToken).ConfigureAwait(false)).SessionId;
    }

    public Task KillAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        RequestAsync(MuxMethods.Kill, new SessionIdParams { SessionId = sessionId }, MuxJsonContext.Default.SessionIdParams,
            MuxJsonContext.Default.MuxEmpty, cancellationToken);

    public Task PingAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(MuxMethods.Ping, new MuxEmpty(), MuxJsonContext.Default.MuxEmpty, MuxJsonContext.Default.MuxEmpty, cancellationToken);

    /// <summary>
    /// The daemon's <c>sessionInfo</c> for any session, open on this client or not (Phase 5: the agent host's windowless
    /// sessions). An unknown id throws <see cref="MuxProtocolException"/> with <see cref="MuxErrorCodes.UnknownSession"/>.
    /// </summary>
    public Task<SessionInfoResult> GetSessionInfoAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        RequestAsync(MuxMethods.SessionInfo, new SessionIdParams { SessionId = sessionId }, MuxJsonContext.Default.SessionIdParams,
            MuxJsonContext.Default.SessionInfoResult, cancellationToken);

    /// <summary>
    /// A session's screen as the daemon's headless parser holds it, and its status, without attaching (Phase 5: the
    /// agent host's windowless sessions). The daemon clamps <paramref name="maxScrollbackRows"/> to
    /// 0..<see cref="MuxReadScreenLimits.MaxScrollbackRows"/>. The snapshot passes the same
    /// <see cref="MuxClientOptions.AttachLimits"/> checks an attach's does.
    /// </summary>
    /// <returns>Null when the daemon does not know <c>readScreen</c> (it answers <c>protocol_error</c>, on any negotiated version).</returns>
    /// <exception cref="MuxProtocolException">
    /// Any other refusal, with its code: <c>unknown_session</c>, <c>session_exited</c> (the session stopped before the
    /// read ran), <c>snapshot_too_large</c> (past the daemon's <see cref="MuxReadScreenLimits.MaxSnapshotBytes"/> or this
    /// client's limits: ask for fewer rows), <c>internal_error</c> (a faulted session). A snapshot this client cannot
    /// decode fails this call only, never the connection: it arrived inside a well-formed reply.
    /// </exception>
    public async Task<MuxScreenRead?> ReadScreenAsync(Guid sessionId, int maxScrollbackRows, CancellationToken cancellationToken = default)
    {
        long id = Interlocked.Increment(ref _nextId);
        JsonElement p = MuxFrames.ToElement(new ReadScreenParams { SessionId = sessionId, MaxScrollbackRows = maxScrollbackRows },
            MuxJsonContext.Default.ReadScreenParams);
        MuxResponse response = await SendAndAwaitAsync(id, MuxMethods.ReadScreen, p, cancellationToken).ConfigureAwait(false);
        if (response.Error is { } error)
        {
            // An older daemon's answer to a method it does not know. A daemon that knows readScreen never refuses with
            // this code (it clamps instead), so it means "unsupported" whichever version was negotiated.
            if (error.Code == MuxErrorCodes.ProtocolError) return null;
            throw new MuxProtocolException(error.Code, error.Message);
        }

        ReadScreenResult result = MuxFrames.ParseParams(response.Result, MuxJsonContext.Default.ReadScreenResult);
        TerminalStateSnapshot snapshot = DecodeSnapshot(result.Snapshot);
        return new MuxScreenRead(snapshot, result with { Snapshot = [] }); // the bytes are decoded: do not hold both
    }

    /// <summary>
    /// Asks the daemon to kill every session and exit (spec §4). Completes once the server has
    /// acknowledged; the connection closes shortly after as the server tears down.
    /// </summary>
    public Task ShutdownServerAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(MuxMethods.Shutdown, new MuxEmpty(), MuxJsonContext.Default.MuxEmpty, MuxJsonContext.Default.MuxEmpty, cancellationToken);

    /// <summary>
    /// An unattached session. Wire its events, then call <see cref="MuxClientSession.AttachAsync(int, MuxPresentation, CancellationToken)"/>:
    /// nothing is raised before a handler can exist, so nothing needs buffering (spec §9.6).
    /// </summary>
    public MuxClientSession OpenSession(Guid sessionId, string shellCommand = "", string? shellArguments = null, MuxAttachMode attachMode = MuxAttachMode.Shared)
    {
        var session = new MuxClientSession(this, sessionId, shellCommand, shellArguments, attachMode);
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
    internal async Task<long> AttachAsync(MuxClientSession session, MuxAttachMode mode, int maxScrollbackRows, MuxPresentation presentation, CancellationToken cancellationToken)
    {
        // A v1 daemon ignores Mode and would silently share (spec §2.1): refuse here, before sending.
        if (mode != MuxAttachMode.Shared && ProtocolVersion < MuxProtocol.SessionEventsVersion)
        {
            throw new MuxProtocolException(MuxErrorCodes.VersionMismatch,
                $"Attach mode {mode} needs multiplexer protocol {MuxProtocol.SessionEventsVersion}; the running multiplexer speaks {ProtocolVersion}.");
        }

        long id = Interlocked.Increment(ref _nextId);
        JsonElement p = MuxFrames.ToElement(
            new AttachParams { SessionId = session.Id, MaxScrollbackRows = maxScrollbackRows, Presentation = presentation, Mode = MuxAttachModes.ToWire(mode) },
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

    /// <summary>
    /// Input for a session this client has not opened or attached (Phase 5: the agent host's windowless sessions): the
    /// daemon takes Input frames from any connection. Never blocks, like every send (ruling R6). Fire-and-forget, so
    /// nothing reports an unknown or exited session: ask <see cref="GetSessionInfoAsync"/> first. Empty text sends
    /// nothing. The daemon drops it while this connection is attached to the session read-only.
    /// </summary>
    public void SendInputTo(Guid sessionId, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0) return;
        SendInput(sessionId, text);
    }

    internal void SendResize(Guid sessionId, int cols, int rows, MuxPresentation? presentation) =>
        PostRequest(MuxMethods.Resize, new ResizeParams { SessionId = sessionId, Cols = cols, Rows = rows, Presentation = presentation },
            MuxJsonContext.Default.ResizeParams);

    internal void Detach(MuxClientSession session, bool userDetached = false)
    {
        _sessions.TryRemove(new KeyValuePair<Guid, MuxClientSession>(session.Id, session));
        // v2 only (spec §7.7): a v1 daemon has no DetachedByUser, so the member is simply not sent.
        bool? flag = userDetached && ProtocolVersion >= MuxProtocol.SessionEventsVersion ? true : null;
        PostRequest(MuxMethods.Detach, new DetachParams { SessionId = session.Id, UserDetached = flag }, MuxJsonContext.Default.DetachParams);
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

    /// <summary>A request's frame. Never blocks; a frame that cannot be sent fails the request (<see cref="IOException"/>).</summary>
    private void Enqueue(MuxOutboundFrame frame)
    {
        if (!Send(frame)) throw Closed();
    }

    /// <summary>A fire-and-forget frame. Never blocks; a frame that cannot be sent is dropped: there is nobody to tell.</summary>
    private void Post(MuxOutboundFrame frame) => Send(frame);

    /// <summary>
    /// The one send path, which never blocks its caller. The frame joins the send queue when the queue has room and
    /// nothing waits in the overflow; otherwise it joins the overflow, which one pool work item at a time
    /// (<see cref="PumpOverflow"/>) moves into the queue in order. Takes the frame's reference either way. False when
    /// the frame is not sent and never will be: the client is disconnected, or this frame took the overflow past
    /// <see cref="MuxClientOptions.MaxOverflowBytes"/>, and that disconnected it.
    /// </summary>
    private bool Send(MuxOutboundFrame frame)
    {
        bool overflowed = false;
        lock (_sendGate)
        {
            if (_outbound.IsAddingCompleted)
            {
                frame.Release();
                return false;
            }

            if (_overflow.Count == 0 && _outbound.TryAdd(frame)) return true;

            _overflow.Enqueue(frame);
            _overflowBytes += frame.PayloadLength;
            if (_overflowBytes > _options.MaxOverflowBytes)
            {
                overflowed = true;
            }
            else if (!_pumping)
            {
                _pumping = true;
                ThreadPool.UnsafeQueueUserWorkItem(static client => client.PumpOverflow(), this, preferLocal: false);
            }
        }

        if (!overflowed) return true;
        FaultFromSendOverflow(); // outside the gate: the disconnect raises host code
        return false;
    }

    /// <summary>
    /// Moves the overflow into the send queue, head first, blocking this pool thread - never a caller - while the queue
    /// is full. One runs at a time per client (<see cref="_pumping"/>). The head leaves the overflow only once the queue
    /// has taken it, so a send meanwhile lines up behind it instead of overtaking it. Once the client is disconnected it
    /// releases whatever is left: <see cref="OnDisconnected"/> leaves that to a pump, whose head may be in flight.
    /// </summary>
    private void PumpOverflow()
    {
        while (true)
        {
            MuxOutboundFrame next;
            lock (_sendGate)
            {
                if (_outbound.IsAddingCompleted) DropOverflowLocked();
                if (_overflow.Count == 0)
                {
                    _pumping = false;
                    return;
                }

                next = _overflow.Peek();
            }

            bool added;
            try
            {
                _outbound.Add(next);
                added = true;
            }
            catch (InvalidOperationException)
            {
                added = false; // CompleteAdding: the client disconnected while this waited for room
            }

            lock (_sendGate)
            {
                _overflow.Dequeue();
                _overflowBytes -= next.PayloadLength;
                if (!added) next.Release();
            }
        }
    }

    private void DropOverflowLocked()
    {
        while (_overflow.TryDequeue(out MuxOutboundFrame? frame)) frame.Release();
        _overflowBytes = 0;
    }

    /// <summary>
    /// More than <see cref="MuxClientOptions.MaxOverflowBytes"/> waits behind the link: it is given up, with the
    /// teardown a read error gets. Pending requests fail, the overflow is released, and <see cref="Disconnected"/>
    /// hands the connection to the host's reconnect or drop path. Raised on the sending caller's thread.
    /// </summary>
    private void FaultFromSendOverflow()
    {
        if (!IsConnected) return; // another send's overflow, or anything else, ended it first
        SafeLog($"[MuxClient] outbound overflow past {_options.MaxOverflowBytes} bytes; disconnecting");
        try { OnDisconnected(ReasonSendOverflow); }
        catch (Exception ex) { SafeLog($"[MuxClient] a Disconnected handler threw: {ex}"); }
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
                MuxInboundFrame? frame = MuxFrameReader.Read(_inbound);
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
            SafeLog($"[MuxClient] protocol error: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            reason = ReasonDisconnected;
        }
        catch (Exception ex)
        {
            reason = MuxErrorCodes.Internal;
            SafeLog($"[MuxClient] delivery failed: {ex}");
        }

        // OnDisconnected raises Disconnected, i.e. runs host code on this dedicated thread: nothing
        // it throws may escape the thread's entry point and take the process down with it.
        try { OnDisconnected(reason); }
        catch (Exception ex) { SafeLog($"[MuxClient] a Disconnected handler threw: {ex}"); }
    }

    /// <summary>
    /// The host's logger is arbitrary code, called here from catch clauses on the reader thread:
    /// a logger that throws (disk full, closed sink) must not turn a disconnect into a crash.
    /// </summary>
    internal void SafeLog(string message)
    {
        try { _options.Log?.Invoke(message); }
        catch (Exception) { /* deliberately swallowed - see above */ }
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
        else if (notification.Method == MuxMethods.SessionChanged)
        {
            SessionChangedNotification changed = MuxFrames.ParseParams(notification.Params, MuxJsonContext.Default.SessionChangedNotification);
            if (_sessions.TryGetValue(changed.SessionId, out MuxClientSession? session)) session.DeliverSessionChanged(changed);
        }
        else if (notification.Method == MuxMethods.Killed)
        {
            KilledNotification killed = MuxFrames.ParseParams(notification.Params, MuxJsonContext.Default.KilledNotification);
            if (_sessions.TryGetValue(killed.SessionId, out MuxClientSession? session)) session.DeliverKilled(killed.ByClientKind);
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

    /// <summary>
    /// The one decode of a daemon's snapshot, shared by attach (<see cref="OnSnapshot"/>, on the reader thread) and
    /// <see cref="ReadScreenAsync"/> (on the caller's): <see cref="MuxClientOptions.AttachLimits"/> is checked here, so
    /// neither path can adopt more than the other. Throws <see cref="MuxProtocolException"/>: <c>snapshot_too_large</c>
    /// past a limit, <c>protocol_error</c> when malformed. What that costs is each caller's own decision.
    /// </summary>
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
        lock (_sendGate)
        {
            // No send queues anything after this, and a pump blocked in Add throws out of it. A pump that runs
            // releases what is left itself: its head may be in flight into the queue, which then owns it.
            _outbound.CompleteAdding();
            if (!_pumping) DropOverflowLocked();
        }

        try { _stream.Dispose(); }
        catch (IOException) { /* closing a transport the peer already dropped - the goal is reached */ }

        IOException closed = Closed();
        foreach (TaskCompletionSource<MuxResponse> tcs in _pending.Values) tcs.TrySetException(closed);

        // Host code, raised from whichever thread ended the connection: the reader, the sender (a
        // failed write) or the Dispose caller. Each handler is isolated, so one that throws neither
        // escapes that thread - on the sender that would be a process crash - nor stops every
        // session and handler after it from being told.
        string? finalReason = DisconnectReason;
        foreach (MuxClientSession session in _sessions.Values)
        {
            try { session.DeliverDisconnected(finalReason); }
            catch (Exception ex) { SafeLog($"[MuxClient] a session Disconnected handler threw: {ex}"); }
        }

        if (Disconnected is { } handlers)
        {
            foreach (Action<string?> handler in Delegate.EnumerateInvocationList(handlers))
            {
                try { handler(finalReason); }
                catch (Exception ex) { SafeLog($"[MuxClient] a Disconnected handler threw: {ex}"); }
            }
        }
    }

    /// <summary>
    /// The connection's read side for the reader: each read that brings bytes stamps
    /// <see cref="LastReceivedTicks"/> - one volatile write, no allocation. It does not own the connection:
    /// <see cref="OnDisconnected"/> closes that.
    /// </summary>
    private sealed class ProgressStampingStream(Stream inner, MuxClient owner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Stamp(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Stamp(inner.Read(buffer));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Stamp(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Stamp(int read)
        {
            if (read > 0) Volatile.Write(ref owner._lastReceivedTicks, Environment.TickCount64);
            return read;
        }
    }
}
