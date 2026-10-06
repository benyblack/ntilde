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
    private int _startFailed;
    // Tick state, touched only under _tickLock.
    private int _foreignDescriptorPid;
    private bool _socketLossReported;
    private string? _descriptorFailure; // the last one logged, so a lasting failure is logged once

    public MuxDaemonHost(MuxServer server, MuxDaemonOptions options)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Task<string> Completion => _completion.Task;
    private string LockPath => LockPathFor(_options.DescriptorPath);

    /// <summary>The lock a daemon serving <paramref name="descriptorPath"/> holds: <c>mux.lock</c> beside the descriptor.</summary>
    internal static string LockPathFor(string descriptorPath) => Path.Combine(Path.GetDirectoryName(descriptorPath)!, "mux.lock");

    public void Start()
    {
        try
        {
            StartCore();
        }
        catch
        {
            // Every failure path below has already released what it took; this only tells a later
            // Dispose/RequestStop that there is no running daemon to report stopping.
            Volatile.Write(ref _startFailed, 1);
            throw;
        }
    }

    private void StartCore()
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

        // Before the server starts: a shutdown request can arrive as soon as the accept thread runs,
        // and one raised before this was attached would be lost.
        _server.ShutdownRequested += OnShutdownRequested;
        try
        {
            _server.Start(CreateListener());
            MuxDiscovery.WriteDescriptor(_options.DescriptorPath, CreateDescriptor());
        }
        catch
        {
            _server.ShutdownRequested -= OnShutdownRequested;
            _server.Dispose();
            ReleaseLock();
            throw;
        }

        // A stop that ran while this method was still writing the descriptor (a client can ask for
        // shutdown the moment the server starts) must not leave a descriptor or a ticking timer behind it.
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

    private MuxEndpointDescriptor CreateDescriptor() => new()
    {
        MinVersion = _server.Options.MinProtocolVersion,
        MaxVersion = _server.Options.MaxProtocolVersion,
        Endpoint = _options.Endpoint,
        Pid = _options.Pid,
        ProcessName = _options.ProcessName,
        StartTime = _options.StartToken,
    };

    /// <summary>
    /// Inside Tick. The descriptor is how everyone finds this daemon, and it is a plain file: deleting
    /// the app-data root removes it (on Windows the held mux.lock survives), leaving a daemon that
    /// holds shells, so never idles out, that nobody can find, and whose lock refuses every new
    /// daemon. Put it back. One naming another live daemon is left alone: that cannot happen while
    /// we hold the lock, and if it somehow does, it is not ours to overwrite.
    /// </summary>
    private void EnsureDescriptor()
    {
        bool readable = MuxDiscovery.TryReadDescriptorText(_options.DescriptorPath, out string? currentText, out MuxEndpointDescriptor? current);
        if (readable && current!.Pid == _options.Pid)
        {
            _foreignDescriptorPid = 0;
            CheckSocket();
            return;
        }

        if (readable && MuxDiscovery.IsProcessAlive(current!.Pid, current.ProcessName))
        {
            if (_foreignDescriptorPid != current.Pid)
            {
                _foreignDescriptorPid = current.Pid;
                Log($"[MuxDaemon] the descriptor names another live multiplexer (pid {current.Pid}); leaving it alone");
            }

            return;
        }

        _foreignDescriptorPid = 0;
        string dir = Path.GetDirectoryName(_options.DescriptorPath)!;
        if (!Directory.Exists(dir))
        {
            // As at start: off Windows the socket lives here, so the directory must be 0700.
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
            else UnixSocketMuxListener.EnsurePrivateDirectory(dir);
        }

        // Another daemon (or a tool) may write the descriptor between the judgement above and here:
        // create-new for a missing file, compare-then-replace for a stale one, never a blind overwrite.
        BeforeDescriptorWriteForTest?.Invoke();
        MuxEndpointDescriptor mine = CreateDescriptor();
        bool wrote = currentText is null
            ? MuxDiscovery.TryWriteDescriptorIfAbsent(_options.DescriptorPath, mine)
            : MuxDiscovery.TryReplaceDescriptorIfUnchanged(_options.DescriptorPath, currentText, mine);
        if (!wrote)
        {
            Log("[Mux] descriptor written by another daemon meanwhile; leaving it");
            return;
        }

        Log(readable
            ? $"[MuxDaemon] the descriptor named pid {current!.Pid}, not this daemon; rewrote it"
            : "[MuxDaemon] descriptor was missing; rewrote it");
        CheckSocket();
    }

    /// <summary>
    /// Off Windows the endpoint is a socket file, deleted with its directory. The listener cannot be
    /// reached through a new file at that path, so there is no repair short of a restart: say so once
    /// per loss. (Re-binding would mean swapping the server's listener under a live accept loop.)
    /// </summary>
    /// <remarks>
    /// A successor daemon (started after the directory, and so the lock's name, was deleted) binds the
    /// same default socket path, and <see cref="File.Exists(string)"/> cannot tell its socket from
    /// ours. That is only safe because EnsureDescriptor's foreign-live-descriptor branch returns
    /// before calling this: a successor writes its descriptor right after binding, so once its socket
    /// exists this daemon stops looking at the path.
    /// </remarks>
    private void CheckSocket()
    {
        if (OperatingSystem.IsWindows()) return;
        if (File.Exists(_options.Endpoint))
        {
            _socketLossReported = false;
            return;
        }

        if (_socketLossReported) return;
        _socketLossReported = true;
        Log($"[MuxDaemon] the endpoint socket {_options.Endpoint} is gone; this multiplexer (pid {_options.Pid}) cannot be reached until it is restarted");
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

    /// <summary>
    /// Inside Tick. The accept loop retries forever; this is where a daemon nobody can reach gives up:
    /// failing for <see cref="MuxDaemonOptions.AcceptFailureStopAfter"/> with no client connected.
    /// A connected client keeps it alive - killing the shells it is using would be the worse failure.
    /// </summary>
    private bool ShouldStopForAcceptFailure() =>
        _server.AcceptFailingFor is TimeSpan failing
        && failing >= _options.AcceptFailureStopAfter
        && _server.ConnectionCount == 0;

    internal void TickForTest() => Tick();

    /// <summary>Test seam: runs between EnsureDescriptor's decision to repair and its write.</summary>
    internal Action? BeforeDescriptorWriteForTest { get; set; }

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

                // Its own catch: a descriptor that cannot be written must not skip the stop checks below.
                try
                {
                    EnsureDescriptor();
                    _descriptorFailure = null;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (_descriptorFailure != ex.Message) Log($"[MuxDaemon] could not restore the descriptor: {ex.Message}");
                    _descriptorFailure = ex.Message;
                }

                if (ShouldStopForAcceptFailure())
                {
                    Log($"[MuxDaemon] the endpoint has not accepted a connection for {_server.AcceptFailingFor?.TotalSeconds:0} s and no client is connected; stopping");
                    RequestStop("accept-failed"); // reentrant, as for idle below
                    return;
                }

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

        // A Start that failed already released everything it took; logging "stopped" for a daemon
        // that never served would only mislead whoever reads mux.log or the --foreground output.
        if (Volatile.Read(ref _startFailed) != 0)
        {
            _completion.TrySetResult(reason);
            return;
        }

        // Blocks until an in-flight Tick (reaping, or the idle check that called us) finishes, so
        // KillAllSessions/server.Dispose() below never overlap ReapExitedSessions. Reentrant, since
        // the idle path reaches here from inside Tick on this same thread already holding the lock.
        Monitor.Enter(_tickLock);
        try
        {
            _timer?.Dispose();
            _server.ShutdownRequested -= OnShutdownRequested;
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
