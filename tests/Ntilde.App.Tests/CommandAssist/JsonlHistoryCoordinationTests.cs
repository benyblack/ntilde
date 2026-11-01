using Ntilde.CommandAssist.Models;
using Ntilde.CommandAssist.Storage;

namespace Ntilde.Tests.CommandAssist;

public sealed class JsonlHistoryCoordinationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ntilde_history_coordination_{Guid.NewGuid():N}");
    private string HistoryPath => Path.Combine(_root, "history.jsonl");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public JsonlHistoryCoordinationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task PeerPatchesAndSweeps_AreRefreshed_BeforeApplyingAnotherPatch()
    {
        var a = new JsonlHistoryStore(HistoryPath, 50);
        var b = new JsonlHistoryStore(HistoryPath, 50);
        await a.AppendAsync(Entry("one", "gti one"), Ct);
        Assert.Single(await b.GetRecentAsync(10, Ct));
        Assert.True(await a.TryUpdateExecutionResultAsync("one", 0, 12, cancellationToken: Ct));
        Assert.Equal(12, Assert.Single(await b.SearchAsync("gti", 10, Ct)).DurationMs);
        await a.AppendAsync(Entry("two", "gti two"), Ct);
        Assert.Equal(2, await b.TryMarkInvalidCommandsByFirstTokenAsync("gti", Ct));
        Assert.True(await a.TryUpdateExecutionResultAsync("one", 0, 20, cancellationToken: Ct));
        var entries = await b.GetRecentAsync(10, Ct);
        Assert.All(entries, entry => Assert.True(entry.IsInvalidCommand));
        Assert.Equal(20, entries.Single(entry => entry.Id == "one").DurationMs);
    }

    [Fact]
    public async Task Clear_RemovesOrphanedCompactionFiles_AndKeepsTheCommandFreeLockFile()
    {
        var a = new JsonlHistoryStore(HistoryPath, 50);
        var b = new JsonlHistoryStore(HistoryPath, 50);
        await a.AppendAsync(Entry("old", "echo old"), Ct);
        Assert.Single(await b.GetRecentAsync(10, Ct));
        string unique = HistoryPath + $".{Guid.NewGuid():N}.tmp";
        string legacy = HistoryPath + ".tmp";
        string unrelated = HistoryPath + ".notes.tmp";
        await File.WriteAllTextAsync(unique, "synthetic secret from a killed writer", Ct);
        await File.WriteAllTextAsync(legacy, "synthetic legacy secret", Ct);
        await File.WriteAllTextAsync(unrelated, "unrelated neighbor", Ct);

        await a.ClearAsync(Ct);

        Assert.Empty(await b.GetRecentAsync(10, Ct));
        Assert.False(File.Exists(unique));
        Assert.False(File.Exists(legacy));
        Assert.True(File.Exists(unrelated));
        Assert.Equal(16, new FileInfo(HistoryPath + ".lock").Length);
        await b.AppendAsync(Entry("new", "echo new"), Ct);
        b.SetMaxEntries(1);
        Assert.Equal("new", Assert.Single(await b.GetRecentAsync(10, Ct)).Id);
        Assert.Equal("new", Assert.Single(await a.GetRecentAsync(10, Ct)).Id);
    }

    [Fact]
    public async Task FailedCompaction_DiscardsPrunedCache_CleansTemp_AndRecoversFromDisk()
    {
        var store = new JsonlHistoryStore(HistoryPath, 50);
        await store.AppendAsync(Entry("one", "echo one"), Ct);
        await store.AppendAsync(Entry("two", "echo two"), Ct);
        string saved = HistoryPath + ".saved";
        File.Move(HistoryPath, saved);
        Directory.CreateDirectory(HistoryPath); // A directory blocks replacing the log on all platforms.
        store.SetMaxEntries(1);
        Exception? error = await Record.ExceptionAsync(() => store.GetRecentAsync(10, Ct));
        Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
        Directory.Delete(HistoryPath);
        File.Move(saved, HistoryPath);
        store.SetMaxEntries(50);
        Assert.Equal(2, (await store.GetRecentAsync(10, Ct)).Count);
        Assert.Equal(2, (await new JsonlHistoryStore(HistoryPath, 50).GetRecentAsync(10, Ct)).Count);
    }

    private static CommandHistoryEntry Entry(string id, string text) => new(
        id, text, DateTimeOffset.UtcNow, "bash", null, null, null, null, null,
        false, false, CommandCaptureSource.ShellIntegration, null);

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
