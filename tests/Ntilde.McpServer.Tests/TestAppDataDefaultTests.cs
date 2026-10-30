using Ntilde.Shell;
using Ntilde.Tests;

namespace Ntilde.McpServer.Tests;

/// <summary>
/// #317: nothing in this test process resolves the machine's real app-data root, and the scratch root starts empty
/// however much legacy data <see cref="AppPaths"/>' migration finds.
/// </summary>
public class TestAppDataDefaultTests
{
    [Fact]
    public void TheTestProcess_ResolvesAScratchRoot_NotTheRealOne()
    {
        Assert.False(
            TestAppDataDefault.IsInside(AppPaths.RootDirectory, AppPaths.DefaultRootDirectory),
            $"'{AppPaths.RootDirectory}' is the machine's real app-data root.");
    }

    [Fact]
    public void TheProcessRoot_StartsEmpty_WhateverTheLegacyMigrationFinds()
    {
        Assert.True(TestAppDataDefault.SweptAppPathsMigration, "TestAppDataDefault.AppPaths.cs is not compiled into this project");
        Assert.Empty(TestAppDataDefault.FilesAtStart);
    }
}
