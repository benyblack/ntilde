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
    }
}
