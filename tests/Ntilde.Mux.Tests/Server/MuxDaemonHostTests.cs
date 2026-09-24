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

    private (MuxDaemonHost Host, MuxServer Server, MuxDaemonOptions Options) NewHost(TimeSpan? idle = null, int? pid = null)
    {
        var server = new MuxServer(new ScriptedSessionFactory(), new MuxServerOptions { ForceConPtyFiltering = false });
        using Process self = Process.GetCurrentProcess();
        var options = new MuxDaemonOptions
        {
            Endpoint = MuxDiscovery.GetDefaultEndpoint(_root),
            DescriptorPath = MuxDiscovery.GetDescriptorPath(_root),
            IdleExitAfter = idle ?? TimeSpan.Zero,
            TickInterval = TimeSpan.FromMilliseconds(50),
            Pid = pid ?? Environment.ProcessId,
            ProcessName = self.ProcessName,
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
        var (first, _, _) = NewHost();
        first.Start();
        var (second, _, _) = NewHost(pid: Environment.ProcessId + 100_000);
        Assert.Throws<MuxDaemonAlreadyRunningException>(second.Start);
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
