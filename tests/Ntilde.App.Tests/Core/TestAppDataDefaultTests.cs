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
                TestAppDataDefault.IsInside(root, real),
                $"'{root}' is the machine's real app-data root '{real}'.");
        }
    }

    [Fact]
    public void TheProcessRoot_StartsEmpty_WhateverTheLegacyMigrationFinds()
    {
        // Codex review of #652: AppPaths' first use migrates legacy data into the current root - a roaming
        // last_session.json, the themes beside the test binaries - so pointing the variable at an empty directory
        // did not keep it empty.
        Assert.True(TestAppDataDefault.SweptAppPathsMigration, "TestAppDataDefault.AppPaths.cs is not compiled into this project");
        Assert.Empty(TestAppDataDefault.FilesAtStart);
    }
}
