using System.Diagnostics;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Daemon;

namespace Ntilde.Mux.Tests.Daemon;

/// <summary>
/// What the spawned daemon is started with (Phase 4 spec §6.2). Only the start info is built: no
/// process is started.
/// </summary>
public sealed class ProcessMuxDaemonSpawnerTests
{
    private static readonly string OtherRoot = Path.Combine(Path.GetTempPath(), "nmxs" + Guid.NewGuid().ToString("N")[..8]);

    private static string? RootOverride(ProcessStartInfo psi) =>
        psi.Environment.TryGetValue(MuxDiscovery.RootOverrideEnvVar, out string? value) ? value : null;

    [Fact]
    public void The_arguments_are_the_leading_ones_then_the_serve_ones()
    {
        ProcessStartInfo psi = new ProcessMuxDaemonSpawner("dotnet", ["Ntilde.dll"], ["mux", "serve"]).CreateStartInfo();

        Assert.Equal("dotnet", psi.FileName);
        Assert.Equal(new[] { "Ntilde.dll", "mux", "serve" }, psi.ArgumentList);
    }

    [Fact]
    public void A_root_other_than_the_inherited_one_is_handed_to_the_daemon()
    {
        ProcessStartInfo psi = new ProcessMuxDaemonSpawner("ntilde-mux", [], ["serve"], OtherRoot).CreateStartInfo();

        Assert.Equal(Path.GetFullPath(OtherRoot), RootOverride(psi));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_inherited_root_leaves_the_environment_alone(bool trailingSeparator)
    {
        string root = MuxDiscovery.GetRootDirectory() + (trailingSeparator ? Path.DirectorySeparatorChar.ToString() : "");

        ProcessStartInfo psi = new ProcessMuxDaemonSpawner("ntilde", [], ["mux", "serve"], root).CreateStartInfo();

        Assert.Equal(Environment.GetEnvironmentVariable(MuxDiscovery.RootOverrideEnvVar), RootOverride(psi));
    }

    [Fact]
    public void No_root_leaves_the_environment_alone()
    {
        ProcessStartInfo psi = new ProcessMuxDaemonSpawner("ntilde", [], ["mux", "serve"]).CreateStartInfo();

        Assert.Equal(Environment.GetEnvironmentVariable(MuxDiscovery.RootOverrideEnvVar), RootOverride(psi));
    }

    /// <summary>The proxy's case: the launcher looks at <c>paths</c>, so the daemon it spawns must serve there.</summary>
    [Fact]
    public void CreateDefault_spawns_the_daemon_under_the_launchers_root()
    {
        MuxDaemonLauncher launcher = MuxDaemonLauncher.CreateDefault(null, ["serve"], new MuxPaths(OtherRoot));

        var spawner = Assert.IsType<ProcessMuxDaemonSpawner>(launcher.SpawnerForTest);
        ProcessStartInfo psi = spawner.CreateStartInfo();
        Assert.Equal(Path.GetFullPath(OtherRoot), RootOverride(psi));
        Assert.Equal(new[] { "serve" }, psi.ArgumentList);
    }
}
