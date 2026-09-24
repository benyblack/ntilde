using Ntilde.Mux.Contracts;

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
        Directory.CreateDirectory(Path.GetDirectoryName(_options.DescriptorPath)!);
        try
        {
            _lock = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new MuxDaemonAlreadyRunningException($"Another multiplexer owns {LockPath}: {ex.Message}");
        }

        if (MuxDiscovery.TryReadLiveDescriptor(_options.DescriptorPath, out MuxEndpointDescriptor? other) && other.Pid != _options.Pid)
        {
            ReleaseLock();
            throw new MuxDaemonAlreadyRunningException($"A multiplexer (pid {other.Pid}) is already running at {other.Endpoint}.");
        }

        try
        {
            _server.Start(_options.ListenerFactory(_options.Endpoint));
            _server.ShutdownRequested += OnShutdownRequested;
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
            _server.Dispose();
            ReleaseLock();
            throw;
        }

        _timer = new Timer(_ => Tick(), null, _options.TickInterval, _options.TickInterval);
        Log($"[MuxDaemon] serving {_options.Endpoint} (pid {_options.Pid})");
    }

    private void OnShutdownRequested() => RequestStop("shutdown");

    internal void TickForTest() => Tick();

    private void Tick()
    {
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
                RequestStop("idle");
            }
        }
        catch (Exception ex)
        {
            Log($"[MuxDaemon] tick failed: {ex}");
        }
    }

    public void RequestStop(string reason)
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0) return;
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
            _completion.TrySetResult(reason);
        }
    }

    private void ReleaseLock()
    {
        FileStream? held = Interlocked.Exchange(ref _lock, null);
        if (held is null) return;
        held.Dispose();
        try { File.Delete(LockPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void Log(string message)
    {
        try { _options.Log?.Invoke(message); } catch (Exception) { /* a throwing logger must not stop the daemon */ }
    }

    public void Dispose() => RequestStop("disposed");
}
