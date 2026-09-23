using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json.Serialization.Metadata;
using Ntilde.Mux.Contracts;
using Ntilde.Pty;

namespace Ntilde.Mux;

/// <summary>
/// One client: a reader thread that parses and dispatches frames, and a sender thread that drains
/// a byte-budgeted queue. <see cref="TryEnqueue"/> is called from session parse threads and never
/// blocks - overflowing the budget disconnects this client instead (spec §7).
/// </summary>
internal sealed class MuxServerConnection : IMuxFrameSink
{
    private readonly MuxServer _server;
    private readonly Stream _stream;
    private readonly Thread _readerThread;
    private readonly Thread _senderThread;
    private readonly object _gate = new();
    private readonly Queue<MuxOutboundFrame> _queue = new();
    private readonly HashSet<Guid> _attached = new(); // reader thread only
    private long _queuedBytes;       // queued + in flight
    private bool _closed;            // stream closed or closing now; nothing more is accepted
    private bool _closeAfterFlush;   // the queue ends with a final frame; the sender closes after it
    private int _version;
    private string? _closeReason;
    private int _closedNotified;

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

    private bool IsClosing
    {
        get { lock (_gate) return _closed || _closeAfterFlush; }
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

            // An empty queue takes any frame (a snapshot bigger than the budget on a fresh attach);
            // otherwise the frame must fit.
            if (_queuedBytes == 0 || _queuedBytes + frame.Length <= _server.Options.ClientSendBudgetBytes)
            {
                frame.AddRef();
                _queue.Enqueue(frame);
                _queuedBytes += frame.Length;
                Monitor.Pulse(_gate);
                return true;
            }
        }

        Abort(MuxErrorCodes.ClientTooSlow);
        return false;
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
            foreach (MuxOutboundFrame f in dropped) _queuedBytes -= f.Length;
            Monitor.PulseAll(_gate);
        }

        foreach (MuxOutboundFrame f in dropped) f.Release();
        if (reason == MuxErrorCodes.ClientTooSlow)
        {
            _server.Log($"[MuxServer] connection {ConnectionId} disconnected: {reason} (send budget {_server.Options.ClientSendBudgetBytes} bytes exceeded).");
        }

        try { _stream.Dispose(); }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
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
            foreach (MuxOutboundFrame f in dropped) _queuedBytes -= f.Length;
            _queue.Enqueue(finalFrame); // takes over the caller's reference
            _queuedBytes += finalFrame.Length;
            _closeAfterFlush = true;
            Monitor.PulseAll(_gate);
        }

        foreach (MuxOutboundFrame f in dropped) f.Release();
    }

    private void CloseWithError(long requestId, string code, string message) =>
        CloseAfter(MuxFrames.Response(new MuxResponse { Id = requestId, Error = new MuxError { Code = code, Message = message } }), code);

    private void SendLoop()
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
                    frame.Release();
                    lock (_gate) _queuedBytes -= length;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or NotSupportedException)
        {
        }

        Abort(CloseReason ?? "disconnected");
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
        }
        catch (Exception ex)
        {
            _server.Log($"[MuxServer] connection {ConnectionId} reader failed: {ex}");
            CloseWithError(0, MuxErrorCodes.Internal, ex.Message);
        }
        finally
        {
            foreach (Guid id in _attached)
            {
                if (_server.TryGetSession(id, out HeadlessTerminalSession? session)) session.PostDetach(this);
            }

            _attached.Clear();
            if (!IsClosing) Abort("disconnected");
            if (Interlocked.Exchange(ref _closedNotified, 1) == 0) _server.OnConnectionClosed(this);
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
                throw new MuxProtocolException(MuxErrorCodes.ProtocolError, $"Expected hello, got '{hello.Method}'.");
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
                        SessionIdParams p = Params(request, MuxJsonContext.Default.SessionIdParams);
                        if (_attached.Remove(p.SessionId) && _server.TryGetSession(p.SessionId, out HeadlessTerminalSession? s)) s.PostDetach(this);
                        ReplyEmpty(request);
                        break;
                    }

                case MuxMethods.Kill:
                    {
                        SessionIdParams p = Params(request, MuxJsonContext.Default.SessionIdParams);
                        _attached.Remove(p.SessionId);
                        _server.Kill(p.SessionId);
                        ReplyEmpty(request);
                        break;
                    }

                case MuxMethods.Resize:
                    {
                        ResizeParams p = Params(request, MuxJsonContext.Default.ResizeParams);
                        RequireGeometry(p.Cols, p.Rows);
                        if (p.Presentation is { } presentation) RequireGeometry(presentation.Cols, presentation.Rows);
                        Session(p.SessionId).PostResize(p.Cols, p.Rows, p.Presentation);
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
                        LiveSession(p.SessionId).Inner.EnableFlightRecording(p.MaxBytes);
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
                case MuxMethods.Hello:
                    throw new MuxProtocolException(MuxErrorCodes.ProtocolError, "hello may only be sent once.");
                default:
                    ReplyError(request, MuxErrorCodes.ProtocolError, $"Unknown method '{request.Method}'.");
                    break;
            }
        }
        catch (MuxRequestException ex)
        {
            ReplyError(request, ex.Code, ex.Message);
        }
    }

    private void HandleAttach(MuxRequest request)
    {
        if (request.Id == 0)
        {
            throw new MuxProtocolException(MuxErrorCodes.ProtocolError, "attach needs a request id: its reply is the snapshot.");
        }

        AttachParams p = Params(request, MuxJsonContext.Default.AttachParams);
        RequireGeometry(p.Presentation.Cols, p.Presentation.Rows);
        HeadlessTerminalSession session = Session(p.SessionId);
        int rows = Math.Clamp(p.MaxScrollbackRows, 0, _server.Options.MaxAttachScrollbackRows);
        _attached.Add(p.SessionId);

        // The reply - the Snapshot frame, or an error - comes from the parse thread.
        session.PostAttach(this, request.Id, rows, p.Presentation, _server.Options.MaxSnapshotBytes);
    }

    private static T Params<T>(MuxRequest request, JsonTypeInfo<T> typeInfo) where T : class =>
        MuxFrames.ParseParams(request.Params, typeInfo); // malformed → MuxProtocolException → connection closed

    private static void RequireGeometry(int cols, int rows)
    {
        if (cols <= 0 || rows <= 0) throw new MuxRequestException(MuxErrorCodes.ProtocolError, $"Invalid geometry {cols}x{rows}.");
    }

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

    private void ReplyEmpty(MuxRequest request) => Reply(request, new MuxEmpty(), MuxJsonContext.Default.MuxEmpty);

    private void ReplyError(MuxRequest request, string code, string message)
    {
        if (request.Id == 0)
        {
            _server.Log($"[MuxServer] {request.Method} (no reply wanted) failed: {code}: {message}");
            return;
        }

        Send(MuxFrames.Response(new MuxResponse { Id = request.Id, Error = new MuxError { Code = code, Message = message } }));
    }

    private void Send(MuxOutboundFrame frame)
    {
        try { TryEnqueue(frame); }
        finally { frame.Release(); }
    }
}
