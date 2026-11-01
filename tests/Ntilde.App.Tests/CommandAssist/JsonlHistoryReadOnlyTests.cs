using System.Text.Json;
using Ntilde.CommandAssist.Models;
using Ntilde.CommandAssist.Storage;

namespace Ntilde.Tests.CommandAssist;

public sealed class JsonlHistoryReadOnlyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ntilde_history_readonly_{Guid.NewGuid():N}");
    private string HistoryPath => Path.Combine(_root, "history.jsonl");
    private string LockPath => HistoryPath + ".lock";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public JsonlHistoryReadOnlyTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Queries_ReadSnapshotsWithoutWriting_WhenCoordinationIsNotWritable(bool directoryBlocker)
    {
        var store = new JsonlHistoryStore(HistoryPath, 50);
        await store.AppendAsync(Entry("one", 1), Ct);
        await store.AppendAsync(Entry("two", 2), Ct);
        await File.AppendAllTextAsync(HistoryPath, "{ corrupt tail\n", Ct);
        byte[] before = await File.ReadAllBytesAsync(HistoryPath, Ct);
        byte[] revision = await File.ReadAllBytesAsync(LockPath, Ct);
        FileAttributes attributes = File.GetAttributes(LockPath);
        if (directoryBlocker)
        {
            File.Delete(LockPath);
            Directory.CreateDirectory(LockPath);
        }
        else File.SetAttributes(LockPath, attributes | FileAttributes.ReadOnly);

        try
        {
            Exception? access = Record.Exception(() =>
            {
                using var denied = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            });
            Assert.True(access is IOException or UnauthorizedAccessException, "Fixture did not deny the write-capable lock open.");
            store.SetMaxEntries(1);
            Assert.Equal("two", Assert.Single(await store.GetRecentAsync(10, Ct)).Id);
            Assert.Equal("two", Assert.Single(await store.SearchAsync("echo", 10, Ct)).Id);
            Exception? mutation = await Record.ExceptionAsync(() => store.AppendAsync(Entry("blocked", 3), Ct));
            Assert.True(mutation is IOException or UnauthorizedAccessException, mutation?.ToString());
            Assert.Equal(before, await File.ReadAllBytesAsync(HistoryPath, Ct));
            if (!directoryBlocker) Assert.Equal(revision, await File.ReadAllBytesAsync(LockPath, Ct));
            Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
        }
        finally
        {
            if (directoryBlocker)
            {
                Directory.Delete(LockPath);
                await File.WriteAllBytesAsync(LockPath, revision, CancellationToken.None);
            }
            else File.SetAttributes(LockPath, attributes);
        }

        // Writable access returns to the coordinated path and applies pending repair/cap work.
        Assert.Equal("two", Assert.Single(await store.GetRecentAsync(10, Ct)).Id);
        Assert.Single(await File.ReadAllLinesAsync(HistoryPath, Ct));
    }

    [Fact]
    public async Task ReadOnlyLegacyQueries_DoNotMigrateOrRetire_AndRecoverWhenWritable()
    {
        string legacy = Path.Combine(_root, "history.json");
        await File.WriteAllTextAsync(legacy, JsonSerializer.Serialize(
            new List<CommandHistoryEntry> { Entry("one", 1), Entry("two", 2) },
            CommandAssistJsonContext.Default.ListCommandHistoryEntry), Ct);
        await File.WriteAllBytesAsync(LockPath, new byte[16], Ct);
        byte[] before = await File.ReadAllBytesAsync(legacy, Ct);
        FileAttributes attributes = File.GetAttributes(LockPath);
        File.SetAttributes(LockPath, attributes | FileAttributes.ReadOnly);
        var store = new JsonlHistoryStore(HistoryPath, 1, legacy);
        try
        {
            Assert.Equal("two", Assert.Single(await store.GetRecentAsync(10, Ct)).Id);
            Assert.Equal("two", Assert.Single(await store.SearchAsync("echo", 10, Ct)).Id);
            Assert.Equal(before, await File.ReadAllBytesAsync(legacy, Ct));
            Assert.False(File.Exists(HistoryPath));
            Assert.False(File.Exists(legacy + ".bak"));
            Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
        }
        finally { File.SetAttributes(LockPath, attributes); }

        Assert.Equal("two", Assert.Single(await store.GetRecentAsync(10, Ct)).Id);
        Assert.True(File.Exists(HistoryPath));
        Assert.False(File.Exists(legacy));
        Assert.True(File.Exists(legacy + ".bak"));
    }

    private static CommandHistoryEntry Entry(string id, int seconds) => new(
        id, "echo " + id, DateTimeOffset.UnixEpoch.AddSeconds(seconds), "bash", null, null, null, null, null,
        false, false, CommandCaptureSource.ShellIntegration, null);

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
