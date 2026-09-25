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

    internal MuxDaemonHost StartDaemon(string? root = null)
    {
        root ??= _root;
        var server = new MuxServer(new ScriptedSessionFactory(), new MuxServerOptions { ForceConPtyFiltering = false });
        var host = new MuxDaemonHost(server, new MuxDaemonOptions
        {
            Endpoint = MuxDiscovery.GetDefaultEndpoint(root),
            DescriptorPath = MuxDiscovery.GetDescriptorPath(root),
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

    /// <summary>
    /// Final-fix item 7: the first spawned daemon lost the lock race to one that was idle-exiting
    /// and exited itself; once nothing is advertised the launcher spawns exactly once more.
    /// </summary>
    [Fact]
    public async Task A_spawn_that_dies_is_retried_once_and_the_retry_connects()
    {
        var spawner = new FailFirstSpawner(this);
        var launcher = new MuxDaemonLauncher(MuxDiscovery.GetDescriptorPath(_root), spawner)
        {
            SpawnTimeout = TimeSpan.FromSeconds(5),
            RespawnAfter = TimeSpan.FromMilliseconds(200),
        };
        using MuxClient client = await launcher.EnsureConnectedAsync(Ct);
        await client.PingAsync(Ct);
        Assert.Equal(2, spawner.Spawns);
    }

    [Fact]
    public async Task A_daemon_that_never_comes_up_is_respawned_at_most_once()
    {
        var spawner = new NoopSpawner();
        var launcher = new MuxDaemonLauncher(MuxDiscovery.GetDescriptorPath(_root), spawner)
        {
            SpawnTimeout = TimeSpan.FromSeconds(1),
            RespawnAfter = TimeSpan.FromMilliseconds(100),
        };
        await Assert.ThrowsAsync<MuxUnavailableException>(() => launcher.EnsureConnectedAsync(Ct));
        Assert.Equal(2, spawner.Spawns);
    }

    /// <summary>First Spawn does nothing (the daemon it would start exits at once); the second starts one.</summary>
    private sealed class FailFirstSpawner(MuxDaemonLauncherTests t) : IMuxDaemonSpawner
    {
        public int Spawns;
        public void Spawn()
        {
            if (Interlocked.Increment(ref Spawns) >= 2) t.StartDaemon();
        }
    }

    /// <summary>
    /// Final-fix item 9: the descriptor is a plain file, so a live pid alone is not trusted - its
    /// endpoint must be where this root's daemon listens. Otherwise it is treated as no daemon.
    /// </summary>
    [Fact]
    public async Task A_descriptor_naming_a_foreign_endpoint_is_not_connected_to()
    {
        // A daemon really is listening at the foreign endpoint: connecting would succeed.
        string elsewhere = Path.Combine(_root, "elsewhere");
        MuxDaemonHost real = StartDaemon(elsewhere);
        using var self = Process.GetCurrentProcess();
        var logs = new List<string>();
        // A live pid (ours), but an endpoint that is not this root's.
        MuxDiscovery.WriteDescriptor(MuxDiscovery.GetDescriptorPath(_root), new MuxEndpointDescriptor
        {
            Endpoint = MuxDiscovery.GetDefaultEndpoint(elsewhere),
            Pid = Environment.ProcessId,
            ProcessName = self.ProcessName,
            MinVersion = 1,
            MaxVersion = 1,
        });
        var launcher = new MuxDaemonLauncher(MuxDiscovery.GetDescriptorPath(_root), new NoopSpawner(), log: s => { lock (logs) logs.Add(s); });

        Assert.Null(await launcher.TryConnectExistingAsync(Ct));
        lock (logs) Assert.Contains(logs, l => l.Contains("is not this profile's", StringComparison.Ordinal));
        Assert.False(real.Completion.IsCompleted);
    }

    [Fact]
    public void The_trusted_endpoint_check_accepts_this_roots_endpoint_only()
    {
        Assert.False(MuxDaemonLauncher.IsTrustedEndpoint("somewhere-else", _root, out string? why));
        Assert.NotNull(why);
        if (OperatingSystem.IsWindows())
        {
            // A pipe name has no directory to check: the endpoint equality is the whole test here.
            Assert.True(MuxDaemonLauncher.IsTrustedEndpoint(MuxDiscovery.GetDefaultEndpoint(_root), _root, out _));
        }
    }

    [Fact]
    public void A_socket_directory_that_is_not_0700_is_not_trusted()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix socket directory modes are a Linux/macOS check.");
        string endpoint = MuxDiscovery.GetDefaultEndpoint(_root);
        string dir = Path.GetDirectoryName(endpoint)!;
        Directory.CreateDirectory(dir);
#pragma warning disable CA1416 // skipped on Windows above
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        Assert.False(MuxDaemonLauncher.IsTrustedEndpoint(endpoint, _root, out string? why));
        Assert.Contains("expected 700", why);
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#pragma warning restore CA1416
        Assert.True(MuxDaemonLauncher.IsTrustedEndpoint(endpoint, _root, out _));
    }

    /// <summary>
    /// Final-fix item 10: a live daemon of another protocol version is reported as a version
    /// mismatch with the kill-server hint - and not answered with a pointless second daemon.
    /// </summary>
    [Fact]
    public async Task A_version_mismatch_is_reported_with_the_kill_server_hint_and_nothing_is_spawned()
    {
        StartDaemon();
        var spawner = new NoopSpawner();
        var launcher = new MuxDaemonLauncher(MuxDiscovery.GetDescriptorPath(_root), spawner,
            new MuxClientOptions { MinProtocolVersion = 99, MaxProtocolVersion = 99 });

        MuxUnavailableException ex = await Assert.ThrowsAsync<MuxUnavailableException>(() => launcher.EnsureConnectedAsync(Ct));

        Assert.True(ex.VersionMismatch);
        Assert.Contains("ntilde mux kill-server", ex.Message);
        Assert.Equal(0, spawner.Spawns);
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
