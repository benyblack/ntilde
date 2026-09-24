using Ntilde.Mux.Contracts;
using Ntilde.Mux.Transport;

namespace Ntilde.Mux;

/// <summary>
/// The daemon around a <see cref="MuxServer"/> (spec §4): one per app-data root (lock file +
/// descriptor), reaps exited sessions, exits when idle, and stops on a <c>shutdown</c> request.
/// </summary>
public sealed class MuxDaemonHost : IDisposable
{
    private readonly MuxServer _server;
    private readonly MuxDaemonOptions _options;
    private readonly TaskCompletionSource<string> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Serializes Tick's body against RequestStop's teardown: a slow ReapExitedSessions must finish
    // (or never start) before KillAllSessions/server.Dispose() run, never overlap them. Monitor
    // rather than SemaphoreSlim because the idle path calls RequestStop from inside Tick on the same
    // thread, which a plain lock (Monitor is what `lock` compiles to) re-enters without deadlocking.
    private readonly object _tickLock = new();
    private FileStream? _lock;
    private Timer? _timer;
    private long _idleSinceMs = -1;
    private int _stopping;

    public MuxDaemonHost(MuxServer server, MuxDaemonOptions options)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Task<string> Completion => _completion.Task;
    private string LockPath => Path.Combine(Path.GetDirectoryName(_options.DescriptorPath)!, "mux.lock");

    public void Start()
    {
        // On Linux/macOS the default socket endpoint lives in this same directory
        // (MuxDiscovery.GetDefaultEndpoint), so it must come up 0700 from the start: a plain
        // CreateDirectory would leave it at the default mode, and UnixSocketMuxListener's own
        // EnsurePrivateDirectory would then find it already existing with the "wrong" mode and refuse
        // to serve. Windows has no such requirement (ACLs, not POSIX modes).
        string descriptorDir = Path.GetDirectoryName(_options.DescriptorPath)!;
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(descriptorDir);
        }
        else
        {
            UnixSocketMuxListener.EnsurePrivateDirectory(descriptorDir);
        }

        try
        {
            _lock = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new MuxDaemonAlreadyRunningException($"Another multiplexer owns {LockPath}: {ex.Message}");
        }

        // The lock is the single-daemon guard. Holding it proves no other daemon owns this root, so a
        // descriptor left behind is stale even if its pid is alive: a crashed daemon's pid can be
        // reused by another process of the same name (the GUI itself is one). Overwritten below.
        if (MuxDiscovery.TryReadDescriptor(_options.DescriptorPath, out MuxEndpointDescriptor? stale) && stale.Pid != _options.Pid)
        {
            Log($"[MuxDaemon] replacing a stale descriptor (pid {stale.Pid}, {stale.Endpoint})");
        }

        // Before the server starts: a shutdown request or an accept-loop fault can arrive as soon as
        // the accept thread runs, and one raised before these were attached would be lost.
        _server.ShutdownRequested += OnShutdownRequested;
        _server.AcceptLoopFaulted += OnAcceptLoopFaulted;
        try
        {
            _server.Start(CreateListener());
            MuxDiscovery.WriteDescriptor(_options.DescriptorPath, new MuxEndpointDescriptor
            {
                MinVersion = _server.Options.MinProtocolVersion,
                MaxVersion = _server.Options.MaxProtocolVersion,
                Endpoint = _options.Endpoint,
                Pid = _options.Pid,
                ProcessName = _options.ProcessName,
            });
        }
        catch
        {
            _server.ShutdownRequested -= OnShutdownRequested;
            _server.AcceptLoopFaulted -= OnAcceptLoopFaulted;
            _server.Dispose();
            ReleaseLock();
            throw;
        }

        // A stop that ran while this method was still writing the descriptor (the accept loop can
        // fault, or a client ask for shutdown, the moment the server starts) must not leave a
        // descriptor or a ticking timer behind it.
        lock (_tickLock)
        {
            if (Volatile.Read(ref _stopping) != 0)
            {
                MuxDiscovery.DeleteDescriptorIfOwned(_options.DescriptorPath, _options.Pid);
                return;
            }

            _timer = new Timer(_ => Tick(), null, _options.TickInterval, _options.TickInterval);
        }

        Log($"[MuxDaemon] serving {_options.Endpoint} (pid {_options.Pid})");
    }

    /// <summary>
    /// The listener, with a socket-level failure reported as the <see cref="IOException"/> every
    /// caller already handles ("endpoint in use / unusable"): a <see cref="System.Net.Sockets.SocketException"/>
    /// is not one, and escaping as itself it crashed <c>mux serve</c> instead of exiting 1.
    /// </summary>
    private IMuxListener CreateListener()
    {
        try
        {
            return _options.ListenerFactory(_options.Endpoint);
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            throw new IOException($"Could not listen on {_options.Endpoint}: {ex.Message}", ex);
        }
    }

    private void OnShutdownRequested() => RequestStop("shutdown");

    private void OnAcceptLoopFaulted(Exception? reason)
    {
        Log($"[MuxDaemon] the endpoint stopped accepting connections ({reason?.Message ?? "listener closed"}); stopping");
        RequestStop("accept-failed");
    }

    internal void TickForTest() => Tick();

    private void Tick()
    {
        // A tick already running (on the timer's own thread pool) skips this one rather than
        // queuing up behind it - reaping/idle-checking twice in a row costs nothing once the first
        // finishes on its own next firing.
        if (!Monitor.TryEnter(_tickLock)) return;
        try
        {
            // RequestStop may have taken the lock and finished (or be about to) between the timer
            // firing and this thread getting in; either way there is nothing left to tick.
            if (Volatile.Read(ref _stopping) != 0) return;

            try
            {
                int reaped = _server.ReapExitedSessions(_options.ReapGrace);
                if (reaped > 0) Log($"[MuxDaemon] reaped {reaped} exited session(s)");

                if (_options.IdleExitAfter <= TimeSpan.Zero) return;
                if (_server.RunningSessionCount != 0 || _server.ConnectionCount != 0)
                {
                    Interlocked.Exchange(ref _idleSinceMs, -1);
                    return;
                }

                long now = Environment.TickCount64;
                long since = Interlocked.CompareExchange(ref _idleSinceMs, now, -1);
                if (since == -1) since = now;
                if (now - since >= (long)_options.IdleExitAfter.TotalMilliseconds && _server.TryBeginIdleShutdown())
                {
                    // Reentrant: RequestStop takes _tickLock too, and this thread already holds it.
                    RequestStop("idle");
                }
            }
            catch (Exception ex)
            {
                Log($"[MuxDaemon] tick failed: {ex}");
            }
        }
        finally
        {
            Monitor.Exit(_tickLock);
        }
    }

    public void RequestStop(string reason)
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0) return;

        // Blocks until an in-flight Tick (reaping, or the idle check that called us) finishes, so
        // KillAllSessions/server.Dispose() below never overlap ReapExitedSessions. Reentrant, since
        // the idle path reaches here from inside Tick on this same thread already holding the lock.
        Monitor.Enter(_tickLock);
        try
        {
            _timer?.Dispose();
            _server.ShutdownRequested -= OnShutdownRequested;
            _server.AcceptLoopFaulted -= OnAcceptLoopFaulted;
            if (reason != "idle") _server.KillAllSessions();
            _server.Dispose();
            MuxDiscovery.DeleteDescriptorIfOwned(_options.DescriptorPath, _options.Pid);
            ReleaseLock();
            Log($"[MuxDaemon] stopped ({reason})");
        }
        catch (Exception ex)
        {
            Log($"[MuxDaemon] stop failed: {ex}");
        }
        finally
        {
            Monitor.Exit(_tickLock);
            _completion.TrySetResult(reason);
        }
    }

    /// <remarks>
    /// Closes the handle and deliberately leaves the file. On Linux/macOS the lock is an advisory
    /// lock on the inode, not the name: unlocking and then unlinking lets a starter that opened the
    /// old file just before the unlink lock that orphaned inode while a second starter creates and
    /// locks a new file at the same path - two daemons, each "holding" the lock. The file is empty
    /// and reused by every later start, so leaving it costs nothing.
    /// </remarks>
    private void ReleaseLock()
    {
        FileStream? held = Interlocked.Exchange(ref _lock, null);
        held?.Dispose();
    }

    private void Log(string message)
    {
        try { _options.Log?.Invoke(message); } catch (Exception) { /* a throwing logger must not stop the daemon */ }
    }

    public void Dispose() => RequestStop("disposed");
}
