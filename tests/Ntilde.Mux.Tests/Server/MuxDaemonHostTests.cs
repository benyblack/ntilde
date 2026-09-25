using System.Diagnostics;
using System.Runtime.Versioning;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Mux.Transport;

namespace Ntilde.Mux.Tests.Server;

public sealed class MuxDaemonHostTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nmxd" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<IDisposable> _owned = new();

    public void Dispose()
    {
        for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private (MuxDaemonHost Host, MuxServer Server, MuxDaemonOptions Options) NewHost(
        TimeSpan? idle = null, int? pid = null, MuxServerOptions? serverOptions = null, Func<string, IMuxListener>? listenerFactory = null,
        TimeSpan? acceptFailureStopAfter = null)
    {
        var server = new MuxServer(new ScriptedSessionFactory(), serverOptions ?? new MuxServerOptions { ForceConPtyFiltering = false });
        using Process self = Process.GetCurrentProcess();
        var options = new MuxDaemonOptions
        {
            Endpoint = MuxDiscovery.GetDefaultEndpoint(_root),
            DescriptorPath = MuxDiscovery.GetDescriptorPath(_root),
            IdleExitAfter = idle ?? TimeSpan.Zero,
            TickInterval = TimeSpan.FromMilliseconds(50),
            Pid = pid ?? Environment.ProcessId,
            ProcessName = self.ProcessName,
            ListenerFactory = listenerFactory ?? MuxListeners.Create,
            AcceptFailureStopAfter = acceptFailureStopAfter ?? TimeSpan.FromSeconds(60),
        };
        var host = new MuxDaemonHost(server, options);
        _owned.Add(host);
        return (host, server, options);
    }

    private static Task<MuxClient> ConnectAsync(string endpoint) =>
        MuxClient.ConnectAsync(MuxEndpointConnector.Connect(endpoint, TimeSpan.FromSeconds(5)), null, Ct);

    [Fact]
    public async Task Start_writes_a_live_descriptor_and_serves_the_endpoint()
    {
        var (host, _, o) = NewHost();
        host.Start();
        Assert.True(MuxDiscovery.TryReadLiveDescriptor(o.DescriptorPath, out MuxEndpointDescriptor? d));
        Assert.Equal(o.Endpoint, d.Endpoint);
        using MuxClient client = await ConnectAsync(d.Endpoint);
        await client.PingAsync(Ct);
    }

    [Fact]
    public async Task Shutdown_request_stops_the_host_and_deletes_the_descriptor()
    {
        var (host, _, o) = NewHost();
        host.Start();
        using MuxClient client = await ConnectAsync(o.Endpoint);
        Guid id = await MuxTestHost.SpawnAsync(client);
        await client.ShutdownServerAsync(Ct);
        Assert.Equal("shutdown", await host.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.False(File.Exists(o.DescriptorPath));
    }

    [Fact]
    public async Task Idle_host_exits_after_the_idle_period()
    {
        var (host, _, o) = NewHost(idle: TimeSpan.FromMilliseconds(200));
        host.Start();
        Assert.Equal("idle", await host.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.False(File.Exists(o.DescriptorPath));
    }

    [Fact]
    public async Task A_running_session_keeps_the_host_alive_past_the_idle_period()
    {
        var (host, _, o) = NewHost(idle: TimeSpan.FromMilliseconds(200));
        host.Start();
        using (MuxClient client = await ConnectAsync(o.Endpoint)) await MuxTestHost.SpawnAsync(client);
        await Task.Delay(600, Ct);
        Assert.False(host.Completion.IsCompleted);
    }

    [Fact]
    public async Task Connection_during_idle_shutdown_is_refused_not_hung()
    {
        var (host, _, o) = NewHost(idle: TimeSpan.FromMilliseconds(100));
        host.Start();
        await host.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using Stream s = MuxEndpointConnector.Connect(o.Endpoint, TimeSpan.FromMilliseconds(500));
            using MuxClient c = await MuxClient.ConnectAsync(s, new MuxClientOptions { RequestTimeout = TimeSpan.FromSeconds(2) }, Ct);
        });
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
    }

    [Fact]
    public void A_second_host_on_the_same_root_refuses_to_start()
    {
        // The lock file is what refuses it (the descriptor check that used to back it up is gone).
        var (first, _, _) = NewHost();
        first.Start();
        var (second, _, _) = NewHost(pid: Environment.ProcessId + 100_000);
        Assert.Throws<MuxDaemonAlreadyRunningException>(second.Start);
    }

    [Fact]
    public async Task A_host_whose_start_failed_does_not_report_a_stop_when_disposed()
    {
        // mux serve disposes the host after a refused start; "stopped (disposed)" for a daemon that
        // never served read as if one had been running and was just shut down.
        var (first, _, _) = NewHost();
        first.Start();
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var server = new MuxServer(new ScriptedSessionFactory(), new MuxServerOptions { ForceConPtyFiltering = false });
        using var second = new MuxDaemonHost(server, new MuxDaemonOptions
        {
            Endpoint = MuxDiscovery.GetDefaultEndpoint(_root),
            DescriptorPath = MuxDiscovery.GetDescriptorPath(_root),
            IdleExitAfter = TimeSpan.Zero,
            Pid = Environment.ProcessId + 100_000,
            Log = log.Enqueue,
        });
        Assert.Throws<MuxDaemonAlreadyRunningException>(second.Start);

        second.Dispose();

        Assert.Equal("disposed", await second.Completion.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.DoesNotContain(log, line => line.Contains("stopped", StringComparison.Ordinal));
        Assert.True(MuxDiscovery.TryReadLiveDescriptor(MuxDiscovery.GetDescriptorPath(_root), out _), "the running daemon's descriptor is untouched");
    }

    /// <summary>
    /// Final-fix item 8: holding the lock proves no other daemon owns the root, so a leftover
    /// descriptor is stale even when its pid is alive under the right process name (pid reuse; the
    /// GUI is a process of the same name). It is overwritten, not obeyed.
    /// </summary>
    [Fact]
    public void A_leftover_descriptor_naming_a_live_pid_is_overwritten_once_the_lock_is_held()
    {
        using Process self = Process.GetCurrentProcess();
        var (host, _, o) = NewHost(pid: Environment.ProcessId + 100_000);
        MuxDiscovery.WriteDescriptor(o.DescriptorPath, new MuxEndpointDescriptor
        {
            Endpoint = "left-behind",
            Pid = Environment.ProcessId, // alive, and named like us
            ProcessName = self.ProcessName,
            MinVersion = 1,
            MaxVersion = 1,
        });

        host.Start();

        Assert.True(MuxDiscovery.TryReadDescriptor(o.DescriptorPath, out MuxEndpointDescriptor? d));
        Assert.Equal(o.Endpoint, d.Endpoint);
        Assert.Equal(o.Pid, d.Pid);
    }

    // PR #489 review 2, item 1 (controller rule): the accept loop retries forever; the host stops
    // ("accept-failed") only after AcceptFailureStopAfter of continuous failure with NO client
    // connected - never killing shells a connected client is still using.

    private static readonly TimeSpan ShortWindow = TimeSpan.FromMilliseconds(300);

    /// <summary>(a) Accept fails continuously while a client stays connected: the host keeps running.</summary>
    [Fact]
    public async Task Failing_accepts_with_a_client_connected_never_stop_the_host()
    {
        var inner = new InMemoryMuxListener();
        var listener = new MuxServerAcceptLoopTests.ScriptedListener(inner, call => call >= 1); // one success, then broken
        var (host, server, o) = NewHost(serverOptions: MuxServerAcceptLoopTests.FastRetry(), listenerFactory: _ => listener,
            acceptFailureStopAfter: ShortWindow);
        host.Start();
        using MuxClient client = await MuxClient.ConnectAsync(inner.Connect(), null, Ct);
        await client.PingAsync(Ct);

        await TestWait.UntilAsync(() => server.AcceptFailingFor > ShortWindow * 3, "accept has been failing well past the window");
        Assert.False(host.Completion.IsCompleted, "the host stopped although a client was connected");
        await client.PingAsync(Ct); // its connection (and shells) are still served
        Assert.True(File.Exists(o.DescriptorPath));

        client.Dispose(); // now nobody can reach the daemon: it gives up
        Assert.Equal("accept-failed", await host.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct));
    }

    /// <summary>(b) Accept fails continuously with no connection: the host stops after the window and frees the root.</summary>
    [Fact]
    public async Task Failing_accepts_with_no_client_stop_the_host_after_the_window()
    {
        var listener = new MuxServerAcceptLoopTests.ScriptedListener(new InMemoryMuxListener(), _ => true);
        var (host, _, o) = NewHost(serverOptions: MuxServerAcceptLoopTests.FastRetry(), listenerFactory: _ => listener,
            acceptFailureStopAfter: ShortWindow);
        var sw = Stopwatch.StartNew();

        host.Start();

        Assert.Equal("accept-failed", await host.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.True(sw.Elapsed >= ShortWindow, $"stopped after {sw.Elapsed}, before the {ShortWindow} window");
        Assert.True(listener.Failed > 3, "the loop kept retrying until the host gave up");
        Assert.False(File.Exists(o.DescriptorPath));
        var (next, _, _) = NewHost(pid: Environment.ProcessId + 100_000);
        next.Start(); // the lock was released
    }

    /// <summary>(c) Transient failures, then success: the clock resets and the host keeps serving.</summary>
    [Fact]
    public async Task Transient_accept_failures_then_success_keep_the_host_serving()
    {
        var inner = new InMemoryMuxListener();
        var listener = new MuxServerAcceptLoopTests.ScriptedListener(inner, call => call < 3);
        var (host, server, _) = NewHost(serverOptions: MuxServerAcceptLoopTests.FastRetry(), listenerFactory: _ => listener,
            acceptFailureStopAfter: ShortWindow);
        host.Start();
        using (MuxClient first = await MuxClient.ConnectAsync(inner.Connect(), null, Ct)) await first.PingAsync(Ct);

        await Task.Delay(ShortWindow * 3, Ct);

        Assert.False(host.Completion.IsCompleted);
        Assert.Null(server.AcceptFailingFor);
        using MuxClient later = await MuxClient.ConnectAsync(inner.Connect(), null, Ct);
        await later.PingAsync(Ct);
    }

    /// <summary>
    /// PR #489 review 2, item 2: the lock file is never unlinked (unlock-then-unlink let two starters
    /// lock different inodes on Linux/macOS), and a leftover one is simply reused by the next start.
    /// </summary>
    [Fact]
    public void Stopping_leaves_the_lock_file_and_a_later_start_reuses_it()
    {
        var (first, _, o) = NewHost();
        first.Start();
        string lockPath = Path.Combine(Path.GetDirectoryName(o.DescriptorPath)!, "mux.lock");
        first.RequestStop("test");
        Assert.True(File.Exists(lockPath), "the lock file must outlive the daemon");

        var (second, _, _) = NewHost(pid: Environment.ProcessId + 100_000);
        second.Start();
        Assert.True(MuxDiscovery.TryReadDescriptor(o.DescriptorPath, out MuxEndpointDescriptor? d));
        Assert.Equal(Environment.ProcessId + 100_000, d.Pid);
        var (third, _, _) = NewHost(pid: Environment.ProcessId + 200_000);
        Assert.Throws<MuxDaemonAlreadyRunningException>(third.Start); // the reused file still locks
    }

    /// <summary>
    /// PR #489 review 2, item 3: a SocketException is not an IOException, so escaping the listener
    /// factory as itself it crashed `mux serve` (and wrote the GUI's startup-error file) instead of
    /// exiting 1. Start reports it as an IOException and releases the lock.
    /// </summary>
    [Fact]
    public void A_socket_failure_creating_the_listener_surfaces_as_IOException_and_releases_the_lock()
    {
        var (host, _, o) = NewHost(listenerFactory: _ => throw new System.Net.Sockets.SocketException(98 /* EADDRINUSE */));

        var ex = Assert.Throws<IOException>(host.Start);
        Assert.IsType<System.Net.Sockets.SocketException>(ex.InnerException);
        Assert.False(File.Exists(o.DescriptorPath));
        var (next, _, _) = NewHost(pid: Environment.ProcessId + 100_000);
        next.Start();
    }
}

/// <summary>
/// Fix-round-1 regression: on Linux/macOS the default socket endpoint lives inside the same
/// directory as the descriptor (<see cref="MuxDiscovery.GetDefaultEndpoint(string)"/>), so
/// <see cref="MuxDaemonHost.Start"/> must create that directory 0700 itself - a plain
/// <see cref="Directory.CreateDirectory(string)"/> would leave it at the default mode, and
/// <c>UnixSocketMuxListener</c>'s own directory check would then find it already existing at the
/// "wrong" mode and refuse to serve, so every fresh start would fail off-Windows.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class MuxDaemonHostUnixDirectoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nmxu" + Guid.NewGuid().ToString("N")[..8]);
    private MuxDaemonHost? _host;

    public void Dispose()
    {
        _host?.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void Start_on_a_fresh_root_succeeds_and_creates_the_mux_directory_0700()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the 0700 socket directory is a Linux/macOS requirement.");

        var server = new MuxServer(new ScriptedSessionFactory(), new MuxServerOptions { ForceConPtyFiltering = false });
        var options = new MuxDaemonOptions
        {
            Endpoint = MuxDiscovery.GetDefaultEndpoint(_root),
            DescriptorPath = MuxDiscovery.GetDescriptorPath(_root),
        };
        _host = new MuxDaemonHost(server, options);

        _host.Start();

        string dir = Path.GetDirectoryName(options.DescriptorPath)!;
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir));
    }
}
