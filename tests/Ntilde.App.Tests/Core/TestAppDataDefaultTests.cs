using Ntilde.AgentHost.Contracts;
using Ntilde.Mux.Contracts;
using Ntilde.Shell;

namespace Ntilde.Tests.Core;

/// <summary>
/// #317: nothing in the App.Tests process resolves the machine's real app-data root, the one the developer's own
/// Ntilde uses on the same machine.
/// </summary>
/// <remarks>
/// The tests write there whenever they don't redirect it: command-assist bootstraps, sessions a closing window saves,
/// the agent-host discovery file, mux descriptors. Per-class <see cref="TestAppDataRoot"/> scopes covered the classes
/// known to write; the process now starts on a scratch root, so a class nobody thought of is covered too.
/// </remarks>
public class TestAppDataDefaultTests
{
    [Fact]
    public void TheTestProcess_ResolvesAScratchRoot_NotTheRealOne()
    {
        // The process default or a parallel test's scope: either way, not the real root.
        string real = Path.GetFullPath(AppPaths.DefaultRootDirectory);

        foreach (string root in new[]
                 {
                     AppPaths.RootDirectory,
                     AgentHostDiscovery.GetDefaultDirectory(),
                     MuxDiscovery.GetRootDirectory(),
                 })
        {
            Assert.False(
                IsInside(Path.GetFullPath(root), real),
                $"'{root}' is the machine's real app-data root '{real}'.");
        }
    }

    private static bool IsInside(string path, string directory)
    {
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string trimmed = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.Equals(trimmed, comparison)
            || path.StartsWith(trimmed + Path.DirectorySeparatorChar, comparison);
    }
}
