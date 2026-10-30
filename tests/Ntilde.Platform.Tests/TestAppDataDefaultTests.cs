using Ntilde.Tests;

namespace Ntilde.Platform.Tests;

/// <summary>
/// #317: nothing in this test process resolves the machine's real app-data root, the one the developer's own Ntilde
/// uses: SSH profiles, generated OpenSSH config and known hosts live under it.
/// </summary>
public class TestAppDataDefaultTests
{
    [Fact]
    public void TheTestProcess_ResolvesAScratchRoot_NotTheRealOne()
    {
        // The process default, or a test's own scope: either way, not the real root.
        Assert.False(
            TestAppDataDefault.IsInside(PlatformAppPaths.RootDirectory, PlatformAppPaths.DefaultRootDirectory),
            $"'{PlatformAppPaths.RootDirectory}' is the machine's real app-data root.");
    }
}
