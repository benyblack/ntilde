using Ntilde.CommandAssist.Models;
using Ntilde.CommandAssist.Storage;
using Worker = Ntilde.Tests.CommandAssist.JsonlHistoryProcessTests.Worker;

namespace Ntilde.Tests.CommandAssist;

public sealed class JsonSnippetStoreCoordinationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ntilde_snippet_coordination_{Guid.NewGuid():N}");
    private string PathName => Path.Combine(_root, "snippets.json");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public JsonSnippetStoreCoordinationTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentInstancesReadOnlyAfterAcquiringTheTransactionLease(bool remove)
    {
        await new JsonSnippetStore(PathName).UpsertAsync(Snippet("seed"), Ct);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var firstStore = new JsonSnippetStore(PathName, source =>
        {
            Stream stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            entered.Set();
            try
            {
                if (!release.Wait(TimeSpan.FromSeconds(5), Ct)) throw new TimeoutException("First transaction was not released.");
                return stream;
            }
            catch { stream.Dispose(); throw; }
        }, File.Create);
        int secondReads = 0;
        var secondStore = new JsonSnippetStore(PathName, source =>
        {
            Interlocked.Increment(ref secondReads);
            return File.OpenRead(source);
        }, File.Create);
        Task first = Task.Run(() => remove ? firstStore.RemoveAsync("seed", Ct) : firstStore.UpsertAsync(Snippet("a"), Ct), Ct);
        Task second;
        bool readBeforeRelease;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5), Ct));
            second = secondStore.UpsertAsync(Snippet("b"), Ct);
            readBeforeRelease = Volatile.Read(ref secondReads) != 0;
        }
        finally { release.Set(); }
        await Task.WhenAll(first, second);

        Assert.False(readBeforeRelease);
        var all = await new JsonSnippetStore(PathName).GetAllAsync(Ct);
        Assert.Equal(remove ? ["b"] : new[] { "a", "b", "seed" }, all.Select(s => s.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task TwoProcessesPreserveAllSuccessfulUpsertsAndDeletion()
    {
        await using var a = await Worker.StartAsync(PathName, snippets: true);
        await using var b = await Worker.StartAsync(PathName, snippets: true);
        for (int i = 0; i < 20; i++)
        {
            await Task.WhenAll(a.SendAsync($"upsert\ta-{i}"), b.SendAsync($"upsert\tb-{i}"));
        }
        string first = await a.SendAsync("recent");
        Assert.Equal(40, first.Split('\t')[1..].Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(first, await b.SendAsync("recent"));
        await a.SendAsync("remove\ta-0");
        await b.SendAsync("upsert\tb-after");
        string after = await b.SendAsync("recent");
        Assert.DoesNotContain("a-0", after.Split('\t')[1..]);
        Assert.Equal(40, after.Split('\t')[1..].Length);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task ForeignProcessLeaseBlocksReadsForMutationsAndCancellationReleasesTheGate()
    {
        await new JsonSnippetStore(PathName).UpsertAsync(Snippet("seed"), Ct);
        byte[] original = await File.ReadAllBytesAsync(PathName, Ct);
        await using var holder = await Worker.StartAsync(PathName, holdLock: true);
        int reads = 0;
        var store = new JsonSnippetStore(PathName, source =>
        {
            Interlocked.Increment(ref reads);
            return File.OpenRead(source);
        }, File.Create);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        Task pending = store.UpsertAsync(Snippet("cancelled"), cancel.Token);
        bool readBeforeCancellation = Volatile.Read(ref reads) != 0;
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(readBeforeCancellation);
        Assert.Equal(original, await File.ReadAllBytesAsync(PathName, Ct));
        Assert.Single(await new JsonSnippetStore(PathName).GetAllAsync(Ct)); // Queries remain read-only snapshots.

        await holder.CrashAsync();
        await store.UpsertAsync(Snippet("after-crash"), Ct);
        Assert.Equal(2, (await store.GetAllAsync(Ct)).Count);
        Assert.True(File.Exists(PathName + ".lock"));
    }

    [Fact]
    public async Task CleanupOwnsOnlyLeasedGuidNamesAndRetriesOpenFilesOnTheNextMutation()
    {
        var store = new JsonSnippetStore(PathName);
        await store.UpsertAsync(Snippet("seed"), Ct);
        string stale = PathName + $".leased.{Guid.NewGuid():N}.tmp";
        string open = PathName + $".leased.{Guid.NewGuid():N}.tmp";
        string foreign = PathName + ".leased.not-a-guid.tmp";
        string unowned = PathName + $".{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(stale, "old temporary commands", Ct);
        await File.WriteAllTextAsync(open, "active temporary commands", Ct);
        await File.WriteAllTextAsync(foreign, "unowned", Ct);
        await File.WriteAllTextAsync(unowned, "unowned", Ct);
        string? link = null;
        if (!OperatingSystem.IsWindows())
        {
            link = PathName + $".leased.{Guid.NewGuid():N}.tmp";
            File.CreateSymbolicLink(link, foreign);
        }
        using (var held = new FileStream(open, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await store.UpsertAsync(Snippet("one"), Ct);
            Assert.False(File.Exists(stale));
            Assert.True(File.Exists(open));
        }
        await store.UpsertAsync(Snippet("two"), Ct);
        Assert.False(File.Exists(open));
        Assert.True(File.Exists(foreign));
        Assert.True(File.Exists(unowned));
        if (link != null) Assert.True(File.Exists(link));
    }

    [Fact]
    public async Task WildcardCharactersInAUnixStoreNameCannotClaimAnotherStoresTemporaryFile()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows filenames do not permit question marks.");
        }
        else
        {
            string literal = Path.Combine(_root, "snip?ets.json");
            var store = new JsonSnippetStore(literal);
            await store.UpsertAsync(Snippet("seed"), Ct);
            string foreign = Path.Combine(_root, $"snippets.json.leased.{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(foreign, "another store owns this temporary document", Ct);

            await store.UpsertAsync(Snippet("new"), Ct);

            Assert.True(File.Exists(foreign));
            Assert.Equal("another store owns this temporary document", await File.ReadAllTextAsync(foreign, Ct));
        }
    }

    private static CommandSnippet Snippet(string id) =>
        new(id, id, "echo " + id, null, null, null, true, DateTimeOffset.UnixEpoch, null);
}
