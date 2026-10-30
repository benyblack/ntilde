using Ntilde.Mux.Contracts;
using Ntilde.Tests;

namespace Ntilde.Mux.Tests;

/// <summary>
/// #317: nothing in this test process resolves the machine's real app-data root, where the developer's own Ntilde
/// keeps its mux descriptor and endpoint.
/// </summary>
public class TestAppDataDefaultTests
{
    [Fact]
    public void TheTestProcess_ResolvesAScratchRoot_NotTheRealOne()
    {
        // MuxDiscovery's default when NTILDE_APPDATA_ROOT is unset.
        string real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ntilde");

        Assert.False(
            TestAppDataDefault.IsInside(MuxDiscovery.GetRootDirectory(), real),
            $"'{MuxDiscovery.GetRootDirectory()}' is the machine's real app-data root '{real}'.");
    }
}
