using Ntilde.Mux.Contracts;
using Ntilde.Mux.Daemon;

namespace Ntilde.Mux.Tests.Daemon;

public sealed class MuxPathsTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "nmxpaths");

    [Fact]
    public void The_defaults_derive_from_the_root()
    {
        var paths = new MuxPaths(Root);

        Assert.Equal(MuxDiscovery.GetDescriptorPath(Root), paths.DescriptorPath);
        Assert.Equal(MuxDiscovery.GetDefaultEndpoint(Root), paths.Endpoint);
        Assert.Equal(Path.Combine(Root, "logs"), paths.LogDirectory);
    }

    [Fact]
    public void The_log_directory_can_be_moved_without_moving_the_endpoint()
    {
        string logs = Path.Combine(Path.GetTempPath(), "elsewhere-logs");

        var paths = new MuxPaths(Root) { LogDirectory = logs };

        Assert.Equal(logs, paths.LogDirectory);
        Assert.Equal(MuxDiscovery.GetDescriptorPath(Root), paths.DescriptorPath);
        Assert.Equal(MuxDiscovery.GetDefaultEndpoint(Root), paths.Endpoint);
    }

    [Fact]
    public void Default_uses_the_discovery_root()
    {
        Assert.Equal(MuxDiscovery.GetRootDirectory(), MuxPaths.Default().Root);
        Assert.False(MuxPaths.Default().IsStandalone);
    }

    /// <summary>Final review F3: ntilde-mux serves a root of its own, beneath the app-data root.</summary>
    [Fact]
    public void Standalone_uses_its_own_directory_under_the_discovery_root()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable(MuxPaths.StandaloneRootOverrideEnvVar) is { Length: > 0 }, "NTILDE_MUX_ROOT is set in this environment");

        MuxPaths paths = MuxPaths.Standalone();

        Assert.Equal(Path.Combine(MuxDiscovery.GetRootDirectory(), "ntilde-mux"), paths.Root);
        Assert.True(paths.IsStandalone);
    }

    /// <summary>
    /// Final review F3: on a host that also runs the GUI, ntilde-mux and the GUI's daemon share nothing - not the
    /// descriptor, the socket (or pipe), the lock or the log - so a remote client never reaches the GUI's daemon
    /// (which cannot spawn its empty-command shells), and the GUI never adopts or shuts down a remote client's
    /// sessions. The app-data root is the same for both: <c>NTILDE_APPDATA_ROOT</c>, or the platform default. The
    /// long root forces the socket out of <c>&lt;root&gt;/mux</c> into the <c>sun_path</c> fallback (off Windows),
    /// which must also tell the two apart.
    /// </summary>
    [Theory]
    [InlineData(8)]
    [InlineData(120)]
    public void The_standalone_and_the_GUI_paths_share_no_descriptor_socket_lock_or_log(int rootNameLength)
    {
        string appDataRoot = Path.Combine(Path.GetTempPath(), "nmx" + new string('r', rootNameLength));
        var gui = new MuxPaths(appDataRoot);   // the App's adapter: MuxPaths(rootOverride ?? GetRootDirectory())
        MuxPaths standalone = MuxPaths.Standalone(MuxPaths.StandaloneRootUnder(appDataRoot));
        if (!OperatingSystem.IsWindows() && rootNameLength > 100)
        {
            // Both too long for sun_path: both fall back, and still apart.
            Assert.False(gui.Endpoint.StartsWith(appDataRoot, StringComparison.Ordinal), gui.Endpoint);
            Assert.False(standalone.Endpoint.StartsWith(appDataRoot, StringComparison.Ordinal), standalone.Endpoint);
        }

        Assert.NotEqual(gui.DescriptorPath, standalone.DescriptorPath);
        Assert.NotEqual(gui.Endpoint, standalone.Endpoint);   // a pipe name on Windows, a socket path elsewhere
        if (!OperatingSystem.IsWindows())
        {
            // The socket's directory too: each daemon owns its 0700 directory.
            Assert.NotEqual(Path.GetDirectoryName(gui.Endpoint), Path.GetDirectoryName(standalone.Endpoint));
        }

        Assert.NotEqual(MuxDaemonHost.LockPathFor(gui.DescriptorPath), MuxDaemonHost.LockPathFor(standalone.DescriptorPath));
        Assert.NotEqual(gui.LogDirectory, standalone.LogDirectory);
    }

    /// <summary>The same for the roots the two executables actually resolve in this environment.</summary>
    [Fact]
    public void The_resolved_standalone_and_GUI_paths_share_no_descriptor_socket_lock_or_log()
    {
        MuxPaths gui = MuxPaths.Default(), standalone = MuxPaths.Standalone();

        Assert.NotEqual(gui.DescriptorPath, standalone.DescriptorPath);
        Assert.NotEqual(gui.Endpoint, standalone.Endpoint);
        Assert.NotEqual(MuxDaemonHost.LockPathFor(gui.DescriptorPath), MuxDaemonHost.LockPathFor(standalone.DescriptorPath));
        Assert.NotEqual(gui.LogDirectory, standalone.LogDirectory);
    }

    /// <summary>
    /// Final review F3: macOS's <c>sun_path</c> holds 103 bytes and a NUL. For a typical home directory ntilde-mux's
    /// socket still sits in its own root, under <c>~/Library/Application Support</c>, rather than in the temp
    /// fallback (which would also work, and also stay apart from the GUI's: see above).
    /// </summary>
    [Fact]
    public void A_typical_macOS_home_keeps_the_standalone_socket_in_its_root()
    {
        const int MacOSSunPathBudget = 103;
        const string AppDataRoot = "/Users/firstname.lastname/Library/Application Support/ntilde";
        string socket = $"{AppDataRoot}/{MuxPaths.StandaloneDirectoryName}/{MuxDiscovery.DirectoryName}/{MuxDiscovery.SocketFileName}";

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(socket) <= MacOSSunPathBudget, $"{socket} does not fit sun_path");
    }

    /// <summary>A standalone root is handed to the daemon it spawns through ntilde-mux's own variable; the GUI's through the app-data one.</summary>
    [Fact]
    public void A_root_is_handed_down_through_the_variable_its_executable_reads()
    {
        string other = Path.Combine(Path.GetTempPath(), "nmxh" + Guid.NewGuid().ToString("N")[..8]);

        Assert.Equal((MuxPaths.StandaloneRootOverrideEnvVar, Path.GetFullPath(other)), MuxPaths.Standalone(other).RootHandDown());
        Assert.Equal((MuxDiscovery.RootOverrideEnvVar, Path.GetFullPath(other)), new MuxPaths(other).RootHandDown());
        Assert.Null(MuxPaths.Standalone().RootHandDown());
        Assert.Null(MuxPaths.Default().RootHandDown());
    }
}
