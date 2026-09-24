using System.Diagnostics;
using Ntilde.Mux;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Shell.Mux;

namespace Ntilde.Tests.Shell.Mux;

public sealed class MuxDaemonLauncherTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nmxl" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<IDisposable> _owned = new();

    public void Dispose()
    {
        for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A "spawn" that starts an in-process daemon host on the real endpoint for _root.</summary>
    private sealed class InProcessSpawner(MuxDaemonLauncherTests t) : IMuxDaemonSpawner
    {
        public int Spawns;
        public void Spawn()
        {
            Interlocked.Increment(ref Spawns);
            t.StartDaemon();
        }
    }

    private sealed class NoopSpawner : IMuxDaemonSpawner { public int Spawns; public void Spawn() => Spawns++; }

    internal MuxDaemonHost StartDaemon()
    {
        var server = new MuxServer(new ScriptedSessionFactory(), new MuxServerOptions { ForceConPtyFiltering = false });
        var host = new MuxDaemonHost(server, new MuxDaemonOptions
        {
            Endpoint = MuxDiscovery.GetDefaultEndpoint(_root),
            DescriptorPath = MuxDiscovery.GetDescriptorPath(_root),
            IdleExitAfter = TimeSpan.Zero,
        });
        host.Start();
        _owned.Add(host);
        return host;
    }

    private MuxDaemonLauncher Launcher(IMuxDaemonSpawner spawner) =>
        new(MuxDiscovery.GetDescriptorPath(_root), spawner) { SpawnTimeout = TimeSpan.FromSeconds(3) };

    [Fact]
    public async Task No_daemon_means_TryConnectExisting_returns_null_without_spawning()
    {
        var spawner = new NoopSpawner();
        Assert.Null(await Launcher(spawner).TryConnectExistingAsync(Ct));
        Assert.Equal(0, spawner.Spawns);
    }

    [Fact]
    public async Task EnsureConnected_spawns_once_and_connects()
    {
        var spawner = new InProcessSpawner(this);
        using MuxClient client = await Launcher(spawner).EnsureConnectedAsync(Ct);
        await client.PingAsync(Ct);
        Assert.Equal(1, spawner.Spawns);
    }

    [Fact]
    public async Task EnsureConnected_uses_a_live_daemon_without_spawning()
    {
        StartDaemon();
        var spawner = new InProcessSpawner(this);
        using MuxClient client = await Launcher(spawner).EnsureConnectedAsync(Ct);
        Assert.Equal(0, spawner.Spawns);
    }

    [Fact]
    public async Task A_spawn_that_never_advertises_fails_with_MuxUnavailable_within_the_timeout()
    {
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<MuxUnavailableException>(() => Launcher(new NoopSpawner()).EnsureConnectedAsync(Ct));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task A_stale_descriptor_is_ignored_and_a_daemon_is_spawned()
    {
        MuxDiscovery.WriteDescriptor(MuxDiscovery.GetDescriptorPath(_root), new MuxEndpointDescriptor
        {
            Endpoint = "gone",
            Pid = 999_999,
            ProcessName = "nothing",
            MinVersion = 1,
            MaxVersion = 1,
        });
        var spawner = new InProcessSpawner(this);
        using MuxClient client = await Launcher(spawner).EnsureConnectedAsync(Ct);
        Assert.Equal(1, spawner.Spawns);
    }
}
