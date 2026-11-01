using Ntilde.Backup;
using Ntilde.CommandAssist.Models;
using Ntilde.CommandAssist.Storage;
using Ntilde.Platform.Ssh.Storage;

namespace Ntilde.Tests.Backup;

public sealed class BackupStoreCoordinationTests
{
    [Theory]
    [InlineData(BackupCategory.Connections, ImportMode.Merge)]
    [InlineData(BackupCategory.Connections, ImportMode.Replace)]
    [InlineData(BackupCategory.Snippets, ImportMode.Merge)]
    [InlineData(BackupCategory.Snippets, ImportMode.Replace)]
    public void Import_RespectsExistingLeaseBeforeSnapshotOrMutation(BackupCategory category, ImportMode mode)
    {
        using var live = BackupTestTree.CreatePopulated();
        using var source = BackupTestTree.CreatePopulated();
        string relative = Relative(category);
        source.WriteFile(relative, category == BackupCategory.Connections ? """{"SchemaVersion":1,"Profiles":[{"Id":"imported","Name":"imported"}]}""" : """[{"Id":"imported","Name":"imported"}]""");
        string bundle = Path.Combine(source.Root, "export.ntildebackup");
        Assert.True(new BackupService(source.Root).Export(bundle, [category]).Success);
        string original = live.ReadFile(relative);
        string lockPath = Path.Combine(live.Root, relative + ".lock");
        using var owner = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var service = new BackupService(live.Root) { MutationLeaseTimeout = TimeSpan.Zero };

        BackupOutcome outcome = service.Import(bundle, mode, [category]);

        Assert.False(outcome.Success);
        Assert.Equal(BackupFailureKind.WriteFailed, outcome.Failure);
        Assert.Equal(original, live.ReadFile(relative));
        Assert.Empty(service.ListSnapshots());
    }

    [Theory]
    [InlineData(BackupCategory.Connections)]
    [InlineData(BackupCategory.Snippets)]
    public void Restore_RespectsExistingLeaseBeforePreRestoreSnapshot(BackupCategory category)
    {
        using var live = BackupTestTree.CreatePopulated();
        var service = new BackupService(live.Root) { MutationLeaseTimeout = TimeSpan.Zero };
        SnapshotInfo? snapshot = service.Snapshot(SnapshotReason.Auto);
        Assert.NotNull(snapshot);
        string relative = Relative(category);
        string changed = category == BackupCategory.Connections ? """{"SchemaVersion":1,"Profiles":[{"Id":"changed","Name":"changed"}]}""" : """[{"Id":"changed","Name":"changed"}]""";
        live.WriteFile(relative, changed);
        using var owner = new FileStream(Path.Combine(live.Root, relative + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        BackupOutcome outcome = service.Restore(snapshot.Id);

        Assert.False(outcome.Success);
        Assert.Equal(BackupFailureKind.WriteFailed, outcome.Failure);
        Assert.Equal(changed, live.ReadFile(relative));
        Assert.Single(service.ListSnapshots());
    }

    [Fact]
    public async Task Merge_ReadsTheBaselineAfterThePendingWriterCommits()
    {
        using var live = BackupTestTree.CreatePopulated();
        using var source = BackupTestTree.CreatePopulated();
        source.WriteFile("ssh/profiles.json", """{"SchemaVersion":1,"Profiles":[{"Id":"54d78456-b0e8-4e37-a773-6efeb3103194","Name":"imported","Host":"imported.invalid"}]}""");
        string bundle = Path.Combine(source.Root, "export.ntildebackup");
        Assert.True(new BackupService(source.Root).Export(bundle, [BackupCategory.Connections]).Success);
        string path = Path.Combine(live.Root, "ssh", "profiles.json");
        using var owner = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new BackupService(live.Root) { MutationLeaseContendedForTest = _ => waiting.TrySetResult() };
        Task<BackupOutcome> import = Task.Run(() => service.Import(bundle, ImportMode.Merge, [BackupCategory.Connections]), TestContext.Current.CancellationToken);
        try
        {
            Task reached = await Task.WhenAny(waiting.Task, import).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Same(waiting.Task, reached);
            // A cooperating store commits under its existing lease, before releasing it.
            live.WriteFile("ssh/profiles.json", """{"SchemaVersion":1,"Profiles":[{"Id":"f5af2d96-9873-4020-94c5-74c8d9179d9c","Name":"concurrent","Host":"concurrent.invalid"}]}""");
        }
        finally
        {
            owner.Dispose();
            await import.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        Assert.True((await import).Success);
        var profiles = new JsonSshProfileStore(path).GetProfiles();
        Assert.Contains(profiles, item => item.Name == "concurrent");
        Assert.Contains(profiles, item => item.Name == "imported");
    }

    [Fact]
    public async Task ARealSnippetUpsert_WaitsThenPreservesImportedContent()
    {
        using var live = BackupTestTree.CreatePopulated();
        using var source = BackupTestTree.CreatePopulated();
        string relative = Relative(BackupCategory.Snippets);
        await new JsonSnippetStore(Path.Combine(source.Root, relative)).UpsertAsync(Snippet("imported"), TestContext.Current.CancellationToken);
        string bundle = Path.Combine(source.Root, "export.ntildebackup");
        Assert.True(new BackupService(source.Root).Export(bundle, [BackupCategory.Snippets]).Success);
        int reads = 0;
        var store = new JsonSnippetStore(Path.Combine(live.Root, relative), path =>
        {
            Interlocked.Increment(ref reads);
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        }, null);
        Task? upsert = null;
        var service = new BackupService(live.Root)
        {
            BeforeCommitForTest = () =>
            {
                // Upsert runs synchronously until its contended lease awaits: it must not
                // reach the injected read factory before the import has committed.
                upsert = store.UpsertAsync(Snippet("edited"), TestContext.Current.CancellationToken);
                Assert.Equal(0, Volatile.Read(ref reads));
            }
        };
        try { Assert.True(service.Import(bundle, ImportMode.Replace, [BackupCategory.Snippets]).Success); }
        finally { if (upsert is not null) await upsert.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken); }
        var snippets = await store.GetAllAsync(TestContext.Current.CancellationToken);
        Assert.Contains(snippets, item => item.Id == "imported");
        Assert.Contains(snippets, item => item.Id == "edited");
    }

    [Fact]
    public void PartialAcquisitionFailure_ReleasesEarlierLocksAndPreservesRevisionBytes()
    {
        using var live = BackupTestTree.CreatePopulated();
        using var source = BackupTestTree.CreatePopulated();
        string bundle = Path.Combine(source.Root, "export.ntildebackup");
        Assert.True(new BackupService(source.Root).Export(bundle, [BackupCategory.Connections, BackupCategory.Snippets]).Success);
        string snippetLock = Path.Combine(live.Root, Relative(BackupCategory.Snippets) + ".lock");
        byte[] revision = Guid.NewGuid().ToByteArray();
        File.WriteAllBytes(snippetLock, revision);
        using var owner = new FileStream(Path.Combine(live.Root, Relative(BackupCategory.Connections) + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var service = new BackupService(live.Root) { MutationLeaseTimeout = TimeSpan.Zero };
        BackupOutcome outcome = service.Import(bundle, ImportMode.Replace, [BackupCategory.Connections, BackupCategory.Snippets]);
        Assert.False(outcome.Success);
        using (new FileStream(snippetLock, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        Assert.Equal(revision, File.ReadAllBytes(snippetLock));
        Assert.Empty(service.ListSnapshots());
    }

    [Fact]
    public void CommitFailure_HoldsBothLocksAndReleasesThemWhenItEscapes()
    {
        using var live = BackupTestTree.CreatePopulated();
        using var source = BackupTestTree.CreatePopulated();
        string bundle = Path.Combine(source.Root, "export.ntildebackup");
        Assert.True(new BackupService(source.Root).Export(bundle, [BackupCategory.Connections, BackupCategory.Snippets]).Success);
        string[] locks = [Path.Combine(live.Root, Relative(BackupCategory.Connections) + ".lock"), Path.Combine(live.Root, Relative(BackupCategory.Snippets) + ".lock")];
        var service = new BackupService(live.Root)
        {
            SimulateCommitPhaseFailureForTest = () =>
            {
                foreach (string path in locks) Assert.Throws<IOException>(() => new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
                return new InvalidOperationException("synthetic commit escape");
            }
        };
        Assert.Throws<InvalidOperationException>(() => service.Import(bundle, ImportMode.Replace, [BackupCategory.Connections, BackupCategory.Snippets]));
        foreach (string path in locks) using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    }

    [Fact]
    public void PermanentLockPathError_FailsWithoutContentionRetries()
    {
        using var live = BackupTestTree.CreatePopulated();
        using var source = BackupTestTree.CreatePopulated();
        string bundle = Path.Combine(source.Root, "export.ntildebackup");
        Assert.True(new BackupService(source.Root).Export(bundle, [BackupCategory.Snippets]).Success);
        Directory.CreateDirectory(Path.Combine(live.Root, Relative(BackupCategory.Snippets) + ".lock"));
        int retries = 0;
        var service = new BackupService(live.Root) { MutationLeaseTimeout = TimeSpan.Zero, MutationLeaseContendedForTest = _ => retries++ };
        Assert.False(service.Import(bundle, ImportMode.Replace, [BackupCategory.Snippets]).Success);
        Assert.Equal(0, retries);
        Assert.Empty(service.ListSnapshots());
    }

    private static CommandSnippet Snippet(string id) => new(id, id, "echo " + id, null, null, null, true, DateTimeOffset.UnixEpoch, null);

    private static string Relative(BackupCategory category) => category == BackupCategory.Connections
        ? Path.Combine("ssh", "profiles.json")
        : Path.Combine("command-assist", "snippets.json");
}
