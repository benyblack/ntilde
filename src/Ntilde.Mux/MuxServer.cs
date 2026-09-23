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

    public MuxServer(ITerminalSessionFactory sessionFactory, MuxServerOptions? options = null)
    {
        _factory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        Options = options ?? new MuxServerOptions();
    }

    public MuxServerOptions Options { get; }
    public int ConnectionCount => _connections.Count;
    public IReadOnlyCollection<Guid> SessionIds => _sessions.Keys.ToArray();

    internal event Action<MuxServerConnection>? ConnectionClosed;

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
        if (Volatile.Read(ref _disposed) != 0)
        {
            stream.Dispose();
            return;
        }

        var connection = new MuxServerConnection(this, stream);
        _connections[connection.ConnectionId] = connection;
        connection.Start();
    }

    internal bool TryGetSession(Guid id, [NotNullWhen(true)] out HeadlessTerminalSession? session) =>
        _sessions.TryGetValue(id, out session);

    internal Guid Spawn(SpawnParams p)
    {
        if (p.Cols <= 0 || p.Rows <= 0)
        {
            throw new MuxRequestException(MuxErrorCodes.ProtocolError, "spawn needs positive cols and rows.");
        }

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
        _sessions[id] = new HeadlessTerminalSession(id, inner, new HeadlessSessionOptions
        {
            Title = string.IsNullOrEmpty(p.Title) ? p.Command : p.Title,
            Command = p.Command,
            Arguments = p.Arguments,
            Cols = p.Cols,
            Rows = p.Rows,
            ForceConPtyFiltering = Options.ForceConPtyFiltering,
            Log = Options.Log,
        });
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
    }

    internal void OnConnectionClosed(MuxServerConnection connection)
    {
        _connections.TryRemove(connection.ConnectionId, out _);
        ConnectionClosed?.Invoke(connection);
    }

    internal void Log(string message) => Options.Log?.Invoke(message);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        _listener?.Dispose();
        _acceptThread?.Join(TimeSpan.FromSeconds(5));
        foreach (MuxServerConnection connection in _connections.Values) connection.Abort("server_shutdown");
        foreach (HeadlessTerminalSession session in _sessions.Values) session.Dispose();
        _sessions.Clear();
        _cts.Dispose();
    }

    private void AcceptLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                Stream? stream = _listener!.Accept(_cts.Token);
                if (stream is null) return;
                AcceptConnection(stream);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log($"[MuxServer] accept loop terminated: {ex}");
        }
    }
}
