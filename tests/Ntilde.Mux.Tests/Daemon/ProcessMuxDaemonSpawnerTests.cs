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

    private static string? RootOverride(ProcessStartInfo psi) => Variable(psi, MuxDiscovery.RootOverrideEnvVar);

    private static string? StandaloneRootOverride(ProcessStartInfo psi) => Variable(psi, MuxPaths.StandaloneRootOverrideEnvVar);

    private static string? Variable(ProcessStartInfo psi, string name) =>
        psi.Environment.TryGetValue(name, out string? value) ? value : null;

    [Fact]
    public void The_arguments_are_the_leading_ones_then_the_serve_ones()
    {
        ProcessStartInfo psi = new ProcessMuxDaemonSpawner("dotnet", ["Ntilde.dll"], ["mux", "serve"]).CreateStartInfo();

        Assert.Equal("dotnet", psi.FileName);
        Assert.Equal(new[] { "Ntilde.dll", "mux", "serve" }, psi.ArgumentList);
    }

    /// <summary>Final review F3: ntilde-mux reads its root from its own variable; the app-data root stays as inherited.</summary>
    [Fact]
    public void A_standalone_root_other_than_the_inherited_one_is_handed_to_the_daemon_through_its_own_variable()
    {
        ProcessStartInfo psi = new ProcessMuxDaemonSpawner("ntilde-mux", [], ["serve"], MuxPaths.Standalone(OtherRoot)).CreateStartInfo();

        Assert.Equal(Path.GetFullPath(OtherRoot), StandaloneRootOverride(psi));
        Assert.Equal(Environment.GetEnvironmentVariable(MuxDiscovery.RootOverrideEnvVar), RootOverride(psi));
    }

    [Fact]
    public void A_GUI_root_other_than_the_inherited_one_is_handed_to_the_daemon()
    {
        ProcessStartInfo psi = new ProcessMuxDaemonSpawner("ntilde", [], ["mux", "serve"], new MuxPaths(OtherRoot)).CreateStartInfo();

        Assert.Equal(Path.GetFullPath(OtherRoot), RootOverride(psi));
        Assert.Equal(Environment.GetEnvironmentVariable(MuxPaths.StandaloneRootOverrideEnvVar), StandaloneRootOverride(psi));
    }

    /// <summary>ntilde-mux's own root, as its proxy resolves it: the daemon resolves the same one, so nothing is set.</summary>
    [Fact]
    public void The_inherited_standalone_root_leaves_the_environment_alone()
    {
        ProcessStartInfo psi = new ProcessMuxDaemonSpawner("ntilde-mux", [], ["serve"], MuxPaths.Standalone()).CreateStartInfo();

        Assert.Equal(Environment.GetEnvironmentVariable(MuxPaths.StandaloneRootOverrideEnvVar), StandaloneRootOverride(psi));
        Assert.Equal(Environment.GetEnvironmentVariable(MuxDiscovery.RootOverrideEnvVar), RootOverride(psi));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_inherited_root_leaves_the_environment_alone(bool trailingSeparator)
    {
        string root = MuxDiscovery.GetRootDirectory() + (trailingSeparator ? Path.DirectorySeparatorChar.ToString() : "");

        ProcessStartInfo psi = new ProcessMuxDaemonSpawner("ntilde", [], ["mux", "serve"], new MuxPaths(root)).CreateStartInfo();

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
        MuxDaemonLauncher launcher = MuxDaemonLauncher.CreateDefault(null, ["serve"], MuxPaths.Standalone(OtherRoot));

        var spawner = Assert.IsType<ProcessMuxDaemonSpawner>(launcher.SpawnerForTest);
        ProcessStartInfo psi = spawner.CreateStartInfo();
        Assert.Equal(Path.GetFullPath(OtherRoot), StandaloneRootOverride(psi));
        Assert.Equal(new[] { "serve" }, psi.ArgumentList);
    }

    /// <summary>Phase 5 R9: the App's resolver maps the installed exe to its copy outside the install root.</summary>
    [Fact]
    public void The_image_resolvers_answer_is_the_executable_started()
    {
        string? asked = null;

        ProcessStartInfo psi = new ProcessMuxDaemonSpawner("installed-ntilde", [], ["mux", "serve"], imageResolver: exe =>
        {
            asked = exe;
            return "copied-ntilde";
        }).CreateStartInfo();

        Assert.Equal("installed-ntilde", asked);
        Assert.Equal("copied-ntilde", psi.FileName);
        Assert.Equal(new[] { "mux", "serve" }, psi.ArgumentList);
    }

    [Fact]
    public void CreateDefault_hands_the_image_resolver_to_the_spawner()
    {
        MuxDaemonLauncher launcher = MuxDaemonLauncher.CreateDefault(null, ["mux", "serve"], new MuxPaths(OtherRoot), imageResolver: _ => "copied-ntilde");

        var spawner = Assert.IsType<ProcessMuxDaemonSpawner>(launcher.SpawnerForTest);
        Assert.Equal("copied-ntilde", spawner.CreateStartInfo().FileName);
    }

    /// <summary>
    /// Velopack starts the GUI with its working directory in <c>current\</c>, and a process whose working directory is
    /// there makes the next update fail (spec R9). The daemon's is the profile, else the app-data root: never inherited,
    /// never the executable's directory.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("missing")]
    public void Without_a_profile_directory_the_daemon_works_in_the_app_data_root(string? profile)
    {
        string appData = Path.Combine(OtherRoot, "data");
        string install = Path.Combine(OtherRoot, "NtildeApp");
        try
        {
            string cwd = ProcessMuxDaemonSpawner.GetDaemonWorkingDirectory(
                profile == "missing" ? Path.Combine(OtherRoot, "no-such-profile") : profile, Directory.Exists, () => appData);

            Assert.Equal(appData, cwd);
            Assert.True(Directory.Exists(appData));
            Assert.False(cwd.StartsWith(install, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { Directory.Delete(OtherRoot, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void With_a_profile_directory_the_daemon_works_there()
    {
        string profile = Path.GetTempPath();

        Assert.Equal(profile, ProcessMuxDaemonSpawner.GetDaemonWorkingDirectory(profile, Directory.Exists, () => throw new InvalidOperationException("not asked")));
    }
}
