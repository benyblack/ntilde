using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Transport;
using Ntilde.Pty;

namespace Ntilde.Mux;

/// <summary>
/// Hosts headless sessions and serves them to clients over any <see cref="Stream"/>. Phase 2 runs
/// this inside <c>ntilde mux serve</c> with a pipe/socket <see cref="IMuxListener"/>.
/// </summary>
public sealed class MuxServer : IDisposable
{
    private readonly ITerminalSessionFactory _factory;
    private readonly ConcurrentDictionary<Guid, HeadlessTerminalSession> _sessions = new();
    private readonly ConcurrentDictionary<Guid, MuxServerConnection> _connections = new();
    private readonly CancellationTokenSource _cts = new();
    private IMuxListener? _listener;
    private Thread? _acceptThread;
    private int _disposed;

    // Serializes connection registration against TryBeginIdleShutdown (spec §4): a connection either
    // registers before the idle check (which then refuses) or is refused after it.
    private readonly object _lifecycleGate = new();
    private bool _acceptingStopped;

    public MuxServer(ITerminalSessionFactory sessionFactory, MuxServerOptions? options = null)
    {
        _factory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        Options = Validate(options ?? new MuxServerOptions());
    }

    /// <summary>
    /// Refuses values that cannot work instead of failing later on a worker thread - e.g. a
    /// <see cref="MuxServerOptions.MaxSnapshotBytes"/> no frame can carry, which would make every
    /// large attach go unanswered until the client's request timeout.
    /// </summary>
    private static MuxServerOptions Validate(MuxServerOptions options)
    {
        MuxServerOptions o = options;
        if (o.ClientSendBudgetBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options), o.ClientSendBudgetBytes, "ClientSendBudgetBytes must be positive.");
        if (o.MaxQueuedSnapshotBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options), o.MaxQueuedSnapshotBytes, "MaxQueuedSnapshotBytes must be positive.");
        if (o.MaxSnapshotBytes <= 0 || o.MaxSnapshotBytes > MuxProtocol.MaxFrameBytes - MuxFrames.SnapshotHeaderBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(options), o.MaxSnapshotBytes,
                $"MaxSnapshotBytes must be in 1..{MuxProtocol.MaxFrameBytes - MuxFrames.SnapshotHeaderBytes} (one frame minus the snapshot header).");
        }

        if (o.MaxInboundFrameBytes <= 0 || o.MaxInboundFrameBytes > MuxProtocol.MaxFrameBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(options), o.MaxInboundFrameBytes, $"MaxInboundFrameBytes must be in 1..{MuxProtocol.MaxFrameBytes}.");
        }

        if (o.MaxAttachScrollbackRows < 0) throw new ArgumentOutOfRangeException(nameof(options), o.MaxAttachScrollbackRows, "MaxAttachScrollbackRows cannot be negative.");
        if (o.MaxCells <= 0) throw new ArgumentOutOfRangeException(nameof(options), o.MaxCells, "MaxCells must be positive.");
        if (o.MaxDimension <= 0) throw new ArgumentOutOfRangeException(nameof(options), o.MaxDimension, "MaxDimension must be positive.");
        if (o.MaxFlightRecordingBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options), o.MaxFlightRecordingBytes, "MaxFlightRecordingBytes must be positive.");
        if (o.MaxQueuedInputBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options), o.MaxQueuedInputBytes, "MaxQueuedInputBytes must be positive.");
        if (o.AcceptRetryInitialDelay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options), o.AcceptRetryInitialDelay, "AcceptRetryInitialDelay must be positive.");
        if (o.AcceptRetryMaxDelay < o.AcceptRetryInitialDelay) throw new ArgumentOutOfRangeException(nameof(options), o.AcceptRetryMaxDelay, "AcceptRetryMaxDelay cannot be shorter than AcceptRetryInitialDelay.");
        if (o.MaxConsecutiveAcceptFailures <= 0) throw new ArgumentOutOfRangeException(nameof(options), o.MaxConsecutiveAcceptFailures, "MaxConsecutiveAcceptFailures must be positive.");
        return o;
    }

    public MuxServerOptions Options { get; }
    public int ConnectionCount => _connections.Count;
    /// <summary>A snapshot of the ids of the sessions this server hosts right now (a copy, hence a method).</summary>
    public IReadOnlyCollection<Guid> GetSessionIds() => _sessions.Keys.ToArray();

    internal event Action<MuxServerConnection>? ConnectionClosed;

    /// <summary>
    /// A client sent <c>shutdown</c>. Raised on a thread-pool thread after the reply is queued; the
    /// host is expected to kill every session and dispose the server. Handler exceptions are logged.
    /// </summary>
    public event Action? ShutdownRequested;

    /// <summary>
    /// The accept loop stopped for a reason other than <see cref="Dispose"/> or an idle shutdown -
    /// <see cref="MuxServerOptions.MaxConsecutiveAcceptFailures"/> failures in a row (the last one is
    /// the argument), or the listener closing under it (null). The server then accepts nobody, so
    /// the host must stop it. Raised once, on a thread-pool thread; handler exceptions are logged.
    /// </summary>
    public event Action<Exception?>? AcceptLoopFaulted;

    /// <summary>True once <see cref="TryBeginIdleShutdown"/> succeeded: new connections are refused.</summary>
    public bool IsAcceptingStopped { get { lock (_lifecycleGate) return _acceptingStopped; } }

    /// <summary>Sessions whose child has not exited (a snapshot).</summary>
    public int RunningSessionCount => _sessions.Values.Count(s => !s.IsExited);

    /// <summary>Starts accepting on a dedicated thread. The server owns the listener from here on.</summary>
    public void Start(IMuxListener listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        if (Interlocked.CompareExchange(ref _listener, listener, null) is not null)
        {
            throw new InvalidOperationException("The server is already started.");
        }

        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "MuxAccept" };
        _acceptThread.Start();
    }

    public void AcceptConnection(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        MuxServerConnection connection;
        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _disposed) != 0 || _acceptingStopped)
            {
                stream.Dispose();
                return;
            }

            connection = new MuxServerConnection(this, stream);
            _connections[connection.ConnectionId] = connection;
        }

        connection.Start();
    }

    /// <summary>
    /// Removes and disposes exited sessions nobody is attached to that exited at least
    /// <paramref name="grace"/> ago (spec §4). Returns how many were reaped.
    /// </summary>
    public int ReapExitedSessions(TimeSpan grace)
    {
        long now = Environment.TickCount64;
        long graceMs = (long)grace.TotalMilliseconds;
        int reaped = 0;
        foreach (HeadlessTerminalSession s in _sessions.Values)
        {
            if (!s.IsExited || s.AttachedClients != 0 || now - s.ExitedAtMs < graceMs) continue;

            // Remove only this exact instance: never a session that replaced it under the same id.
            if (_sessions.TryRemove(new KeyValuePair<Guid, HeadlessTerminalSession>(s.Id, s)))
            {
                s.Dispose();
                reaped++;
            }
        }

        return reaped;
    }

    /// <summary>
    /// Stops accepting iff there is nothing to serve (no connection, no running session),
    /// atomically with <see cref="AcceptConnection"/>: a connection either registered before (and
    /// this returns false) or is refused after. Idempotent once it has succeeded.
    /// </summary>
    public bool TryBeginIdleShutdown()
    {
        lock (_lifecycleGate)
        {
            if (_acceptingStopped) return true;
            if (!_connections.IsEmpty || RunningSessionCount != 0) return false;
            _acceptingStopped = true;
        }

        _listener?.Dispose();
        return true;
    }

    /// <summary>Kills and disposes every session (the <c>shutdown</c> path).</summary>
    public void KillAllSessions()
    {
        foreach (Guid id in _sessions.Keys)
        {
            if (_sessions.TryRemove(id, out HeadlessTerminalSession? s))
            {
                try { s.Kill(); }
                catch (Exception ex) { Log($"[MuxServer] kill {id} failed: {ex.Message}"); }
                s.Dispose();
            }
        }
    }

    internal void RequestShutdown()
    {
        try { ShutdownRequested?.Invoke(); }
        catch (Exception ex) { Log($"[MuxServer] ShutdownRequested handler threw: {ex}"); }
    }

    internal bool TryGetSession(Guid id, [NotNullWhen(true)] out HeadlessTerminalSession? session) =>
        _sessions.TryGetValue(id, out session);

    /// <summary>
    /// Every peer-supplied geometry (spawn, attach presentation, resize) passes here before it can
    /// reach a <c>TerminalBuffer</c>, which allocates eagerly: over the ceiling is a request error,
    /// never an OOM on a parse thread nor a ResizeEvent that makes every attached client drop.
    /// </summary>
    internal void RequireGeometry(int cols, int rows)
    {
        if (cols <= 0 || rows <= 0)
        {
            throw new MuxRequestException(MuxErrorCodes.ProtocolError, $"Invalid geometry {cols}x{rows}: cols and rows must be positive.");
        }

        if (cols > Options.MaxDimension || rows > Options.MaxDimension || (long)cols * rows > Options.MaxCells)
        {
            throw new MuxRequestException(MuxErrorCodes.ProtocolError,
                $"Geometry {cols}x{rows} exceeds this server's ceiling ({Options.MaxDimension} per dimension, {Options.MaxCells} cells).");
        }
    }

    internal Guid Spawn(SpawnParams p)
    {
        RequireGeometry(p.Cols, p.Rows);

        var request = new TerminalSessionRequest(
            p.Command, p.Arguments, p.StartingDirectory, p.Cols, p.Rows,
            p.EnvironmentOverrides, p.SkipPowerShellPostLaunchInit, Ssh: null);

        ITerminalSession inner;
        try
        {
            inner = _factory.Create(request);
        }
        catch (Exception ex)
        {
            throw new MuxRequestException(MuxErrorCodes.SpawnFailed, ex.Message);
        }

        if (inner is not ITerminalByteOutput)
        {
            inner.Dispose();
            throw new MuxRequestException(MuxErrorCodes.SpawnFailed,
                $"{inner.GetType().Name} does not expose raw output, so it cannot be multiplexed.");
        }

        Guid id = Guid.NewGuid();
        HeadlessTerminalSession session;
        try
        {
            session = new HeadlessTerminalSession(id, inner, new HeadlessSessionOptions
            {
                Title = string.IsNullOrEmpty(p.Title) ? p.Command : p.Title,
                Command = p.Command,
                Arguments = p.Arguments,
                Cols = p.Cols,
                Rows = p.Rows,
                ForceConPtyFiltering = Options.ForceConPtyFiltering,
                Log = Options.Log,
                MaxQueuedInputBytes = Options.MaxQueuedInputBytes,
            });
        }
        catch (Exception ex)
        {
            // The factory already started a child: nothing else will ever own it, so end it here
            // rather than leak a process, and report the failure as the spawn's (not an internal
            // error that would close this client's connection).
            try { inner.Dispose(); }
            catch (Exception disposeEx) { Log($"[MuxServer] disposing an unwrapped session failed: {disposeEx.Message}"); }
            throw new MuxRequestException(MuxErrorCodes.SpawnFailed, $"The session could not be multiplexed: {ex.Message}");
        }

        _sessions[id] = session;

        // Dispose may have run while the factory was spawning (it sets _disposed before it sweeps
        // _sessions). Checked after publishing, so there is no gap: either Dispose's sweep sees
        // this session or this check sees Dispose - and HeadlessTerminalSession.Dispose is
        // idempotent if both do. Without it the child and its parse thread outlive the server.
        if (Volatile.Read(ref _disposed) != 0)
        {
            if (_sessions.TryRemove(id, out HeadlessTerminalSession? orphan)) orphan.Dispose();
            throw new MuxRequestException(MuxErrorCodes.SpawnFailed, "The server is shutting down.");
        }

        return id;
    }

    internal SessionSummary[] ListSessions() =>
        _sessions.Values.Select(s => new SessionSummary
        {
            SessionId = s.Id,
            Title = s.Title,
            Command = s.Command,
            Arguments = s.Arguments,
            Cols = s.Cols,
            Rows = s.Rows,
            Running = !s.IsExited,
            ExitCode = s.ExitCode,
            AttachedClients = s.AttachedClients,
            Faulted = s.IsFaulted,
        }).ToArray();

    internal void Kill(Guid id)
    {
        if (!_sessions.TryRemove(id, out HeadlessTerminalSession? session))
        {
            throw new MuxRequestException(MuxErrorCodes.UnknownSession, $"No session {id}.");
        }

        session.Kill();

        // Kill queues the terminal exit behind whatever output is already queued, so attached
        // clients still get Exited in stream order. Dispose (which cancels first and would drop that
        // exit) only once the parse thread has worked through it: FlushAsync completes after the
        // exit item was processed - or dropped, if the session stopped some other way meanwhile.
        // Without this the removed session was never disposed: its queues, token source and child
        // handle were left to the finalizer.
        _ = session.FlushAsync().ContinueWith(
            _ =>
            {
                try { session.Dispose(); }
                catch (Exception ex) { Log($"[MuxServer] disposing killed session {id} failed: {ex.Message}"); }
            },
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    internal void OnConnectionClosed(MuxServerConnection connection)
    {
        _connections.TryRemove(connection.ConnectionId, out _);
        ConnectionClosed?.Invoke(connection);
    }

    /// <summary>Never throws: a caller's logger must not take down the thread (accept, reader, parse) that reports through it.</summary>
    internal void Log(string message)
    {
        // Reached from parse threads and catch clauses: a throwing host logger must not fault a
        // session or turn a report about one failure into a second, escaping one.
        try { Options.Log?.Invoke(message); }
        catch (Exception) { /* deliberately swallowed - see above */ }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        _listener?.Dispose();
        _acceptThread?.Join(TimeSpan.FromSeconds(5));
        foreach (MuxServerConnection connection in _connections.Values) connection.Abort("server_shutdown");
        // TryRemove per key rather than dispose-all-then-Clear: a Clear would also drop a session a
        // late Spawn published after the sweep, without disposing it. (Spawn re-checks _disposed
        // after publishing and disposes its own session in that case.)
        foreach (Guid id in _sessions.Keys)
        {
            if (_sessions.TryRemove(id, out HeadlessTerminalSession? session)) session.Dispose();
        }

        _cts.Dispose();
    }

    /// <summary>True once the loop's end is the server's own doing (Dispose, idle shutdown), not a fault.</summary>
    private bool IsStoppingDeliberately
    {
        get
        {
            if (_cts.IsCancellationRequested || Volatile.Read(ref _disposed) != 0) return true;
            lock (_lifecycleGate) return _acceptingStopped;
        }
    }

    /// <summary>
    /// One failed accept must not end the daemon's ability to accept (a transient pipe/socket error
    /// used to leave a daemon holding its lock and descriptor while accepting nobody): each failure
    /// is logged and retried after a doubling pause. Only <see cref="MuxServerOptions.MaxConsecutiveAcceptFailures"/>
    /// in a row, or the listener closing under the loop, end it - and then as a fault the host acts on.
    /// </summary>
    private void AcceptLoop()
    {
        CancellationToken token = _cts.Token;
        TimeSpan delay = Options.AcceptRetryInitialDelay;
        int failures = 0;
        while (!token.IsCancellationRequested)
        {
            Stream? stream;
            try
            {
                stream = _listener!.Accept(token);
            }
            catch (Exception) when (IsStoppingDeliberately)
            {
                // Dispose/idle shutdown tore the listener down under the accept: the normal end.
                return;
            }
            catch (Exception ex)
            {
                failures++;
                Log($"[MuxServer] accept failed ({failures} in a row): {ex}");
                if (failures >= Options.MaxConsecutiveAcceptFailures)
                {
                    RaiseAcceptLoopFaulted(ex);
                    return;
                }

                // Cancellable pause: Dispose must not wait out a backoff.
                if (token.WaitHandle.WaitOne(delay)) return;
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, Options.AcceptRetryMaxDelay.Ticks));
                continue;
            }

            if (stream is null)
            {
                // The listener reports itself closed. Expected when we closed it; otherwise nothing
                // will ever be accepted again, which the host must hear about.
                if (!IsStoppingDeliberately) RaiseAcceptLoopFaulted(null);
                return;
            }

            failures = 0;
            delay = Options.AcceptRetryInitialDelay;
            try
            {
                AcceptConnection(stream);
            }
            catch (Exception ex)
            {
                // One connection that could not be set up is that connection's problem, not the loop's.
                Log($"[MuxServer] setting up an accepted connection failed: {ex}");
                try { stream.Dispose(); }
                catch (Exception disposeEx) { Log($"[MuxServer] disposing that connection's stream failed: {disposeEx.Message}"); }
            }
        }
    }

    private void RaiseAcceptLoopFaulted(Exception? reason)
    {
        Log($"[MuxServer] accept loop stopped: {(reason is null ? "the listener closed" : reason.Message)}");
        // Off this thread: a handler that disposes the server would otherwise join the accept thread from itself.
        _ = Task.Run(() =>
        {
            try { AcceptLoopFaulted?.Invoke(reason); }
            catch (Exception ex) { Log($"[MuxServer] AcceptLoopFaulted handler threw: {ex}"); }
        });
    }
}
