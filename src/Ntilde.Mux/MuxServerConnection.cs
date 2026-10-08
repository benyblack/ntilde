using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json.Serialization.Metadata;
using Ntilde.Mux.Contracts;
using Ntilde.Pty;

namespace Ntilde.Mux;

/// <summary>
/// One client: a reader thread that parses and dispatches frames, and a sender thread that drains
/// a byte-budgeted queue. <see cref="TryEnqueue"/> is called from session parse threads and never
/// blocks - overflowing the budget disconnects this client instead (spec §7). Snapshots, and the
/// <c>readScreen</c> replies that carry one (<see cref="MuxOutboundFrame.IsBulk"/>), are accounted
/// apart from the stream budget (<see cref="MuxServerOptions.MaxQueuedSnapshotBytes"/>).
/// </summary>
internal sealed class MuxServerConnection : IMuxFrameSink
{
    /// <summary>Close reason when the peer simply went away: not an error code, so not in MuxErrorCodes.</summary>
    private const string ReasonDisconnected = "disconnected";

    private readonly MuxServer _server;
    private readonly Stream _stream;
    private readonly Thread _readerThread;
    private readonly Thread _senderThread;
    private readonly object _gate = new();
    private readonly Queue<MuxOutboundFrame> _queue = new();

    /// <summary>
    /// Reader thread only: the close-time cleanup list - every session this connection has posted an
    /// attach to and not plainly detached from. A superset of its subscriptions (a refused attach, or
    /// one undone by a detach naming it, stays listed), which is harmless: a detach for a sink that is
    /// not subscribed is a no-op on the session. It is kept on the reader thread rather than learned
    /// from <see cref="OnSubscriptionState"/> because an attach can succeed on the parse thread just
    /// as this connection closes; the detach posted from here queues behind it and so still undoes it.
    /// Nothing else is decided from it: supersession and read-only state belong to the parse thread.
    /// </summary>
    private readonly HashSet<Guid> _attached = new();

    /// <summary>
    /// Sessions this connection is subscribed to read-only. Written only by those sessions' parse
    /// threads (<see cref="OnSubscriptionState"/>), each for its own key; read by the reader thread.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, byte> _readOnlySessions = new();
    private readonly HashSet<(Guid Session, string What)> _readOnlyDropLogged = new(); // reader thread only
    private long _queuedBytes;       // stream frames queued + in flight (everything but bulk frames)
    private long _queuedSnapshotBytes; // bulk frames (snapshots, readScreen replies) queued + in flight
    private bool _closed;            // stream closed or closing now; nothing more is accepted
    private bool _closeAfterFlush;   // the queue ends with a final frame; the sender closes after it
    private int _version;
    private string? _closeReason;
    private string _clientKind = string.Empty;

    /// <summary>
    /// The <c>shutdown</c> reply (reader writes, sender reads). The sender raises
    /// <see cref="MuxServer.ShutdownRequested"/> only once this frame is on the wire: the host's
    /// handler disposes the server, whose Abort drops every frame not yet written.
    /// </summary>
    private MuxOutboundFrame? _shutdownReply;

    /// <summary>
    /// Reader and sender each decrement exactly once, from their own outer <c>finally</c>. The one
    /// that reaches zero notifies the server - so a connection whose reader has already exited (a
    /// protocol error) but whose sender is still stuck writing an unread final frame to a stalled
    /// peer stays in <see cref="MuxServer"/>'s registry, and <see cref="MuxServer.Dispose"/> can
    /// still find and abort it instead of leaking an open stream nobody is tracking any more.
    /// </summary>
    private int _activeThreads = 2;

    public MuxServerConnection(MuxServer server, Stream stream)
    {
        _server = server;
        _stream = stream;
        _readerThread = new Thread(ReadLoop) { IsBackground = true, Name = $"MuxConnRead-{ConnectionId:N}" };
        _senderThread = new Thread(SendLoop) { IsBackground = true, Name = $"MuxConnSend-{ConnectionId:N}" };
    }

    public Guid ConnectionId { get; } = Guid.NewGuid();
    public string? CloseReason => Volatile.Read(ref _closeReason);
    public int ProtocolVersion => Volatile.Read(ref _version);
    public string ClientKind => Volatile.Read(ref _clientKind);

    internal const int MaxClientInstanceIdLength = 64;
    private string? _clientInstanceId;

    /// <summary>The hello's <c>clientInstanceId</c> once this connection has welcomed with one; null otherwise.</summary>
    public string? ClientInstanceId => Volatile.Read(ref _clientInstanceId);

    /// <summary>A v2 peer understands sessionChanged and killed (spec §2); a v1 peer never sees them.</summary>
    public bool WantsSessionEvents => ProtocolVersion >= MuxProtocol.SessionEventsVersion;

    /// <summary>Parse thread of <paramref name="sessionId"/>. The only writer of this connection's read-only state.</summary>
    public void OnSubscriptionState(Guid sessionId, bool subscribed, bool readOnly)
    {
        if (subscribed && readOnly) _readOnlySessions.TryAdd(sessionId, 0);
        else _readOnlySessions.TryRemove(sessionId, out _);
    }

    private bool IsClosing
    {
        get { lock (_gate) return _closed || _closeAfterFlush; }
    }

    /// <summary>Tests: bytes charged to the stream account and to the snapshot account, queued or in flight.</summary>
    internal (long Stream, long Snapshot) QueuedBytesForTest
    {
        get { lock (_gate) return (_queuedBytes, _queuedSnapshotBytes); }
    }

    public void Start()
    {
        _senderThread.Start();
        _readerThread.Start();
    }

    public bool TryEnqueue(MuxOutboundFrame frame)
    {
        lock (_gate)
        {
            if (_closed || _closeAfterFlush) return false;

            // Two accounts sharing one FIFO queue. Snapshots are charged to their own bound, never
            // to the stream budget: one snapshot can be bigger than the whole budget (10k rows at
            // 200 cols is ~32 MB), and parallel attaches on one GUI connection must not read as a
            // slow client. A readScreen reply carries a snapshot too (up to ~5.6 MB as base64), so it
            // is charged the same way although its kind is Response (IsBulk). Either account takes
            // any frame while it is empty; otherwise the frame must fit what is left of it.
            bool bulk = frame.IsBulk;
            long queued = bulk ? _queuedSnapshotBytes : _queuedBytes;
            long limit = bulk ? _server.Options.MaxQueuedSnapshotBytes : _server.Options.ClientSendBudgetBytes;
            if (queued == 0 || queued + frame.Length <= limit)
            {
                frame.AddRef();
                _queue.Enqueue(frame);
                Account(frame, frame.Length);
                Monitor.Pulse(_gate);
                return true;
            }
        }

        Abort(MuxErrorCodes.ClientTooSlow);
        return false;
    }

    /// <summary>
    /// Caller holds <see cref="_gate"/>. Charges (positive) or refunds (negative) the frame's own account: the one
    /// <see cref="MuxOutboundFrame.IsBulk"/> chose at enqueue, since a frame's kind and mark never change.
    /// </summary>
    private void Account(MuxOutboundFrame frame, long delta)
    {
        if (frame.IsBulk) _queuedSnapshotBytes += delta;
        else _queuedBytes += delta;
    }

    /// <summary>
    /// Ends the connection now: drops queued frames and closes the stream, which unblocks both
    /// threads. For <c>client_too_slow</c> the reason cannot be delivered - the peer is not reading -
    /// so it is recorded here and logged (spec §9.5).
    /// </summary>
    public void Abort(string reason)
    {
        MuxOutboundFrame[] dropped;
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            Interlocked.CompareExchange(ref _closeReason, reason, null);
            dropped = _queue.ToArray();
            _queue.Clear();
            foreach (MuxOutboundFrame f in dropped) Account(f, -f.Length);
            Monitor.PulseAll(_gate);
        }

        foreach (MuxOutboundFrame f in dropped) f.Release();
        if (reason == MuxErrorCodes.ClientTooSlow)
        {
            // SafeLog: this runs on a session parse thread (Broadcast -> TryEnqueue -> Abort)
            // outside any try, and must still reach the stream dispose below.
            SafeLog($"[MuxServer] connection {ConnectionId} disconnected: {reason} (send budget of {_server.Options.ClientSendBudgetBytes} stream bytes or {_server.Options.MaxQueuedSnapshotBytes} snapshot bytes exceeded).");
        }

        // Closing is the goal; a transport that complains while being closed is already closed
        // as far as this connection is concerned.
        try { _stream.Dispose(); }
        catch (IOException) { /* the peer end is already gone */ }
        catch (ObjectDisposedException) { /* disposed by the other thread first */ }
    }

    /// <summary>Sends one last frame (the peer is still reading and deserves the reason), then closes.</summary>
    private void CloseAfter(MuxOutboundFrame finalFrame, string reason)
    {
        MuxOutboundFrame[] dropped;
        lock (_gate)
        {
            if (_closed || _closeAfterFlush)
            {
                finalFrame.Release();
                return;
            }

            Interlocked.CompareExchange(ref _closeReason, reason, null);
            dropped = _queue.ToArray();
            _queue.Clear();
            foreach (MuxOutboundFrame f in dropped) Account(f, -f.Length);
            _queue.Enqueue(finalFrame); // takes over the caller's reference
            Account(finalFrame, finalFrame.Length);
            _closeAfterFlush = true;
            Monitor.PulseAll(_gate);
        }

        foreach (MuxOutboundFrame f in dropped) f.Release();
    }

    /// <summary>
    /// Never throws: building the response frame is itself untrusted-input-adjacent (the message
    /// usually echoes something the peer sent), so a failure here - most plausibly
    /// <see cref="MuxErrorCodes.FrameTooLarge"/> from an oversize message - falls back to a plain
    /// abort instead of escaping from inside a reader-thread catch clause and killing the process.
    /// </summary>
    private void CloseWithError(long requestId, string code, string message)
    {
        MuxOutboundFrame frame;
        try
        {
            frame = MuxFrames.Response(new MuxResponse { Id = requestId, Error = new MuxError { Code = code, Message = message } });
        }
        catch (Exception ex)
        {
            SafeLog($"[MuxServer] connection {ConnectionId}: could not build a close-with-error frame for '{code}': {ex.Message}");
            Abort(code);
            return;
        }

        CloseAfter(frame, code);
    }

    /// <summary>At most 64 characters of client-supplied text, so an oversize field never blows up an echo of it.</summary>
    private static string Clip(string value) =>
        value.Length <= 64 ? value : string.Concat(value.AsSpan(0, 64), "…");

    private void SafeLog(string message)
    {
        // Called from catch clauses and parse threads: a throwing host logger must never turn a
        // report about a failure into a second, escaping failure.
        try { _server.Log(message); }
        catch (Exception) { /* deliberately swallowed - see above */ }
    }

    private void SendLoop()
    {
        try
        {
            try
            {
                while (TryTakeNext(out MuxOutboundFrame? frame))
                {
                    int length = frame.Length;
                    try
                    {
                        frame.WriteTo(_stream);
                    }
                    finally
                    {
                        lock (_gate) Account(frame, -length); // Kind and IsBulk stay readable after Release
                        frame.Release();
                    }

                    // Reached only when the write succeeded. Frames are not pooled (only their
                    // buffers are), so reference identity cannot match a later frame.
                    if (ReferenceEquals(frame, Volatile.Read(ref _shutdownReply))) RaiseShutdownRequested();
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or NotSupportedException)
            {
                // The peer went away or the stream was closed under us (Abort from another
                // thread): not an error, just the end of this connection. The Abort below closes
                // up with whatever reason was recorded first.
            }

            Abort(CloseReason ?? ReasonDisconnected);
        }
        catch (Exception ex)
        {
            // Last resort: nothing may escape a background thread's entry point and crash the
            // process over one client's connection (spec §9: untrusted input is a per-connection
            // failure, never a server-wide one).
            SafeLog($"[MuxServer] connection {ConnectionId} sender loop crashed: {ex}");
            try { Abort(MuxErrorCodes.Internal); }
            catch (Exception) { /* already logged; nothing may escape this thread's entry point */ }
        }
        finally
        {
            NotifyThreadFinished();
        }
    }

    private bool TryTakeNext([NotNullWhen(true)] out MuxOutboundFrame? frame)
    {
        lock (_gate)
        {
            while (_queue.Count == 0)
            {
                if (_closed || _closeAfterFlush)
                {
                    frame = null;
                    return false;
                }

                Monitor.Wait(_gate);
            }

            frame = _queue.Dequeue();
            return true;
        }
    }

    private void ReadLoop()
    {
        try
        {
            try
            {
                while (!IsClosing)
                {
                    MuxInboundFrame? frame = MuxFrameReader.Read(_stream, _server.Options.MaxInboundFrameBytes);
                    if (frame is null) break;
                    using (frame)
                    {
                        Dispatch(frame);
                    }
                }
            }
            catch (MuxProtocolException ex)
            {
                CloseWithError(0, ex.Code, ex.Message);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // The peer closed or the stream was aborted from another thread: the connection is
                // over, and the finally below does the cleanup either way.
            }
            catch (Exception ex)
            {
                SafeLog($"[MuxServer] connection {ConnectionId} reader failed: {ex}");
                CloseWithError(0, MuxErrorCodes.Internal, ex.Message);
            }
            finally
            {
                foreach (Guid id in _attached)
                {
                    if (_server.TryGetSession(id, out HeadlessTerminalSession? session)) session.PostDetach(this);
                }

                _attached.Clear();
                if (!IsClosing) Abort(ReasonDisconnected);
            }
        }
        catch (Exception ex)
        {
            // Last resort: nothing may escape a background thread's entry point and crash the
            // process over one client's connection (spec §9: untrusted input is a per-connection
            // failure, never a server-wide one).
            SafeLog($"[MuxServer] connection {ConnectionId} reader loop crashed: {ex}");
            try { Abort(MuxErrorCodes.Internal); }
            catch (Exception) { /* already logged; nothing may escape this thread's entry point */ }
        }
        finally
        {
            NotifyThreadFinished();
        }
    }

    /// <summary>
    /// The last of the reader and the sender to finish tells the server: see the remarks on
    /// <see cref="_activeThreads"/>.
    /// </summary>
    private void NotifyThreadFinished()
    {
        if (Interlocked.Decrement(ref _activeThreads) == 0)
        {
            _server.OnConnectionClosed(this);
        }
    }

    private void Dispatch(MuxInboundFrame frame)
    {
        if (ProtocolVersion == 0)
        {
            if (frame.Kind != MuxFrameKind.Request)
            {
                throw new MuxProtocolException(MuxErrorCodes.ProtocolError, "The first frame must be a hello request.");
            }

            MuxRequest hello = MuxFrames.ParseJson(frame.Payload, MuxJsonContext.Default.MuxRequest);
            if (hello.Method != MuxMethods.Hello)
            {
                throw new MuxProtocolException(MuxErrorCodes.ProtocolError, $"Expected hello, got '{Clip(hello.Method)}'.");
            }

            HandleHello(hello);
            return;
        }

        switch (frame.Kind)
        {
            case MuxFrameKind.Request:
                HandleRequest(MuxFrames.ParseJson(frame.Payload, MuxJsonContext.Default.MuxRequest));
                break;
            case MuxFrameKind.Input:
                if (!MuxFrames.TryParseInput(frame.Payload, out Guid id, out ReadOnlySpan<byte> utf8))
                {
                    throw new MuxProtocolException(MuxErrorCodes.ProtocolError, "Input frame shorter than its session id.");
                }

                if (_readOnlySessions.ContainsKey(id))
                {
                    NoteReadOnlyDrop(id, "input");
                    break;
                }

                // One Input frame is one whole SendInput string, so decoding it alone is lossless.
                if (_server.TryGetSession(id, out HeadlessTerminalSession? session)) session.SendInput(Encoding.UTF8.GetString(utf8));
                break;
            default:
                throw new MuxProtocolException(MuxErrorCodes.ProtocolError, $"A client may not send {frame.Kind} frames.");
        }
    }

    private void HandleHello(MuxRequest request)
    {
        HelloParams p = MuxFrames.ParseParams(request.Params, MuxJsonContext.Default.HelloParams);
        MuxServerOptions o = _server.Options;
        if (MuxProtocol.NegotiateVersion(o.MinProtocolVersion, o.MaxProtocolVersion, p.MinVersion, p.MaxVersion) is not int chosen)
        {
            CloseWithError(request.Id, MuxErrorCodes.VersionMismatch,
                $"Server speaks {o.MinProtocolVersion}..{o.MaxProtocolVersion}; client offered {p.MinVersion}..{p.MaxVersion}.");
            return;
        }

        Volatile.Write(ref _clientKind, Clip(p.ClientKind ?? string.Empty));

        // Evict before welcome (spec §2.5), and record our own id only afterwards: two twins hello-ing
        // at once then cannot each see the other as live and both die.
        if (p.ClientInstanceId is { } instanceId)
        {
            if (instanceId.Length > MaxClientInstanceIdLength)
            {
                SafeLog($"[MuxServer] connection {ConnectionId}: ignoring a clientInstanceId of {instanceId.Length} characters (limit {MaxClientInstanceIdLength}).");
            }
            else
            {
                _server.EvictTwins(this, instanceId);
                Volatile.Write(ref _clientInstanceId, instanceId);
            }
        }

        Volatile.Write(ref _version, chosen);
        Reply(request, new WelcomeResult { Version = chosen, ForceConPtyFiltering = o.ForceConPtyFiltering }, MuxJsonContext.Default.WelcomeResult);
    }

    private void HandleRequest(MuxRequest request)
    {
        try
        {
            switch (request.Method)
            {
                case MuxMethods.Ping:
                    ReplyEmpty(request);
                    break;
                case MuxMethods.Shutdown:
                    HandleShutdown(request);
                    break;
                case MuxMethods.ListSessions:
                    Reply(request, new ListSessionsResult { Sessions = _server.ListSessions() }, MuxJsonContext.Default.ListSessionsResult);
                    break;
                case MuxMethods.Spawn:
                    Guid spawned = _server.Spawn(Params(request, MuxJsonContext.Default.SpawnParams));
                    Reply(request, new SpawnResult { SessionId = spawned }, MuxJsonContext.Default.SpawnResult);
                    break;
                case MuxMethods.Attach:
                    HandleAttach(request);
                    break;
                case MuxMethods.Detach:
                    {
                        DetachParams p = Params(request, MuxJsonContext.Default.DetachParams);

                        // Whether a detach naming an older attach is stale is decided on the parse
                        // thread, the only place that knows which attaches took effect. Only a plain
                        // detach is sure to end the subscription, so only it drops the cleanup entry.
                        if (_attached.Contains(p.SessionId) && _server.TryGetSession(p.SessionId, out HeadlessTerminalSession? s))
                        {
                            s.PostDetach(this, p.UserDetached == true, p.AttachRequestId);
                        }

                        if (p.AttachRequestId is null) _attached.Remove(p.SessionId);
                        ReplyEmpty(request);
                        break;
                    }

                case MuxMethods.Kill:
                    {
                        SessionIdParams p = Params(request, MuxJsonContext.Default.SessionIdParams);
                        _attached.Remove(p.SessionId);
                        _server.Kill(p.SessionId, this, ClientKind);
                        ReplyEmpty(request);
                        break;
                    }

                case MuxMethods.Resize:
                    {
                        ResizeParams p = Params(request, MuxJsonContext.Default.ResizeParams);
                        if (_readOnlySessions.ContainsKey(p.SessionId))
                        {
                            NoteReadOnlyDrop(p.SessionId, "resize");
                            ReplyEmpty(request);
                            break;
                        }

                        _server.RequireGeometry(p.Cols, p.Rows);
                        if (p.Presentation is { } presentation) _server.RequireGeometry(presentation.Cols, presentation.Rows);
                        Session(p.SessionId).PostResize(this, p.Cols, p.Rows, p.Presentation); // named: its kitty flag is this client's own
                        ReplyEmpty(request);
                        break;
                    }

                case MuxMethods.SessionInfo:
                    {
                        HeadlessTerminalSession s = Session(Params(request, MuxJsonContext.Default.SessionIdParams).SessionId);
                        Reply(request, new SessionInfoResult
                        {
                            Running = !s.IsExited,
                            ExitCode = s.ExitCode,
                            HasActiveChildProcesses = !s.IsExited && s.Inner.HasActiveChildProcesses,
                            Pid = (s.Inner as RustPtySession)?.Pid,
                            Title = s.Title,
                            Cwd = s.Cwd,
                            AttachedClients = s.AttachedClients,
                            InteractiveClients = ProtocolVersion >= MuxProtocol.SessionEventsVersion ? s.InteractiveClients : null, // v2 only: a v1 peer sees the v1 shape
                        }, MuxJsonContext.Default.SessionInfoResult);
                        break;
                    }

                case MuxMethods.StartRecording:
                    {
                        StartRecordingParams p = Params(request, MuxJsonContext.Default.StartRecordingParams);
                        LiveSession(p.SessionId).Inner.StartRecording(p.Path);
                        ReplyEmpty(request);
                        break;
                    }

                case MuxMethods.StopRecording:
                    Session(Params(request, MuxJsonContext.Default.SessionIdParams).SessionId).Inner.StopRecording();
                    ReplyEmpty(request);
                    break;
                case MuxMethods.EnableFlightRecording:
                    {
                        EnableFlightRecordingParams p = Params(request, MuxJsonContext.Default.EnableFlightRecordingParams);
                        if (p.MaxBytes <= 0) throw new MuxRequestException(MuxErrorCodes.ProtocolError, "maxBytes must be positive.");

                        // The budget is memory the daemon holds: never the peer's to choose unbounded.
                        LiveSession(p.SessionId).Inner.EnableFlightRecording(Math.Min(p.MaxBytes, _server.Options.MaxFlightRecordingBytes));
                        ReplyEmpty(request);
                        break;
                    }

                case MuxMethods.DisableFlightRecording:
                    Session(Params(request, MuxJsonContext.Default.SessionIdParams).SessionId).Inner.DisableFlightRecording();
                    ReplyEmpty(request);
                    break;
                case MuxMethods.ExportFlight:
                    Reply(request, Session(Params(request, MuxJsonContext.Default.SessionIdParams).SessionId).ExportFlight(), MuxJsonContext.Default.ExportFlightResult);
                    break;
                case MuxMethods.ReadScreen:
                    HandleReadScreen(request);
                    break;
                case MuxMethods.Hello:
                    throw new MuxProtocolException(MuxErrorCodes.ProtocolError, "hello may only be sent once.");
                default:
                    ReplyError(request, MuxErrorCodes.ProtocolError, $"Unknown method '{Clip(request.Method)}'.");
                    break;
            }
        }
        catch (MuxRequestException ex)
        {
            ReplyError(request, ex.Code, ex.Message);
        }
        catch (Exception ex) when (ex is not MuxProtocolException)
        {
            // A well-formed request whose *operation* failed - most plausibly the session's own
            // StartRecording on an unwritable path, or a flight export hitting I/O. That is this
            // request's failure, never the connection's: letting it reach ReadLoop's generic catch
            // would close the shared connection and disconnect every other pane on the client.
            // (A MuxProtocolException still propagates: malformed input does end the connection.)
            SafeLog($"[MuxServer] connection {ConnectionId}: {Clip(request.Method)} failed: {ex}");
            string message = ex.Message.Length <= 512 ? ex.Message : string.Concat(ex.Message.AsSpan(0, 512), "…");
            ReplyError(request, MuxErrorCodes.Internal, $"{ex.GetType().Name}: {message}");
        }
    }

    private void HandleAttach(MuxRequest request)
    {
        if (request.Id == 0)
        {
            throw new MuxProtocolException(MuxErrorCodes.ProtocolError, "attach needs a request id: its reply is the snapshot.");
        }

        AttachParams p = Params(request, MuxJsonContext.Default.AttachParams);
        if (!MuxAttachModes.TryParse(p.Mode, out MuxAttachMode mode))
        {
            throw new MuxRequestException(MuxErrorCodes.ProtocolError, $"Unknown attach mode '{Clip(p.Mode ?? string.Empty)}'.");
        }

        _server.RequireGeometry(p.Presentation.Cols, p.Presentation.Rows);
        HeadlessTerminalSession session = Session(p.SessionId);
        int rows = Math.Clamp(p.MaxScrollbackRows, 0, _server.Options.MaxAttachScrollbackRows);
        _attached.Add(p.SessionId);

        // The reply - the Snapshot frame, or an error - comes from the parse thread, which also reports
        // the resulting subscription (OnSubscriptionState). Read-only enforcement follows that report,
        // never this request: a refused or failed attach leaves the connection as it was. Not a
        // security boundary: any same-user process can open another, interactive connection.
        session.PostAttach(this, request.Id, rows, p.Presentation, _server.Options.MaxSnapshotBytes, mode);
    }

    /// <summary>
    /// Phase 5: a session's screen and status for a connection that need not be attached to it. Only the parse thread
    /// may capture, so the reply comes from there, as an attach's does, and subscribes nothing. The row count is
    /// clamped, never refused: <c>protocol_error</c> must keep meaning "this daemon does not know readScreen".
    /// </summary>
    private void HandleReadScreen(MuxRequest request)
    {
        ReadScreenParams p = Params(request, MuxJsonContext.Default.ReadScreenParams);
        HeadlessTerminalSession session = Session(p.SessionId);
        if (request.Id == 0) return; // a read does nothing but answer

        int rows = Math.Clamp(p.MaxScrollbackRows, 0, MuxReadScreenLimits.MaxScrollbackRows);

        // Probed here, as sessionInfo does: the probe can walk the process table, and the parse thread is the output path.
        bool hasChildren = !session.IsExited && session.Inner.HasActiveChildProcesses;
        session.PostReadScreen(this, request.Id, rows, _server.Options.MaxReadScreenBytes, hasChildren);
    }

    /// <summary>Reader thread. Once per (session, kind) per connection lifetime, so a client typing into a read-only view does not flood the log.</summary>
    private void NoteReadOnlyDrop(Guid sessionId, string what)
    {
        if (_readOnlyDropLogged.Add((sessionId, what)))
        {
            SafeLog($"[MuxServer] connection {ConnectionId} ({ClientKind}) is read-only on session {sessionId}: dropping its {what} (logged once)");
        }
    }

    private static T Params<T>(MuxRequest request, JsonTypeInfo<T> typeInfo) where T : class =>
        MuxFrames.ParseParams(request.Params, typeInfo); // malformed → MuxProtocolException → connection closed

    private HeadlessTerminalSession Session(Guid id) =>
        _server.TryGetSession(id, out HeadlessTerminalSession? s) ? s : throw new MuxRequestException(MuxErrorCodes.UnknownSession, $"No session {id}.");

    private HeadlessTerminalSession LiveSession(Guid id)
    {
        HeadlessTerminalSession s = Session(id);
        return s.IsExited ? throw new MuxRequestException(MuxErrorCodes.SessionExited, $"Session {id} has exited.") : s;
    }

    private void Reply<T>(MuxRequest request, T result, JsonTypeInfo<T> typeInfo)
    {
        if (request.Id == 0) return;
        MuxOutboundFrame frame;
        try
        {
            frame = MuxFrames.Response(new MuxResponse { Id = request.Id, Result = MuxFrames.ToElement(result, typeInfo) });
        }
        catch (MuxProtocolException ex) when (ex.Code == MuxErrorCodes.FrameTooLarge)
        {
            ReplyError(request, ex.Code, ex.Message);
            return;
        }

        Send(frame);
    }

    /// <summary>
    /// Replies, and has the sender raise <see cref="MuxServer.ShutdownRequested"/> once that reply
    /// is written (see <see cref="_shutdownReply"/>). A request that wants no reply raises it now.
    /// If the reply never reaches the wire (the peer went away first), the event is not raised:
    /// the client saw a failure and may retry.
    /// </summary>
    private void HandleShutdown(MuxRequest request)
    {
        if (request.Id == 0)
        {
            RaiseShutdownRequested();
            return;
        }

        MuxOutboundFrame frame = MuxFrames.Response(new MuxResponse { Id = request.Id, Result = MuxFrames.ToElement(new MuxEmpty(), MuxJsonContext.Default.MuxEmpty) });
        Volatile.Write(ref _shutdownReply, frame); // before it is queued, so the sender cannot miss it
        Send(frame);
    }

    /// <summary>
    /// Off this connection's threads: the handler tears the server down, and Dispose -> Abort must
    /// not run on the very thread it is stopping. Control plane, not the output path.
    /// </summary>
    private void RaiseShutdownRequested() =>
        ThreadPool.UnsafeQueueUserWorkItem(static s => s.RequestShutdown(), _server, preferLocal: false);

    private void ReplyEmpty(MuxRequest request) => Reply(request, new MuxEmpty(), MuxJsonContext.Default.MuxEmpty);

    private void ReplyError(MuxRequest request, string code, string message)
    {
        if (request.Id == 0)
        {
            SafeLog($"[MuxServer] {Clip(request.Method)} (no reply wanted) failed: {code}: {message}");
            return;
        }

        MuxOutboundFrame frame;
        try
        {
            frame = MuxFrames.Response(new MuxResponse { Id = request.Id, Error = new MuxError { Code = code, Message = message } });
        }
        catch (Exception ex)
        {
            SafeLog($"[MuxServer] connection {ConnectionId}: could not build an error reply for '{code}': {ex.Message}");
            Abort(MuxErrorCodes.Internal);
            return;
        }

        Send(frame);
    }

    private void Send(MuxOutboundFrame frame)
    {
        try { TryEnqueue(frame); }
        finally { frame.Release(); }
    }
}
