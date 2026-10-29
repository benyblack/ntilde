using Ntilde.Shell.Shortcuts;

namespace Ntilde.Tests.Core;

public sealed class CommandPaletteUsageStoreTests
{
    // #355: this used to poll for the file against a 2 s wall clock, and missed it when the
    // thread pool was saturated by a full parallel run. The save is awaited now - no deadline.
    [Fact]
    public async Task RecordUse_PersistsCountAcrossReloads()
    {
        string tempRoot = CreateTempDirectory();
        string path = Path.Combine(tempRoot, "command-palette-usage.json");

        try
        {
            var store = new CommandPaletteUsageStore(path);
            store.RecordUse("settings", new DateTimeOffset(2026, 5, 26, 12, 0, 0, TimeSpan.Zero));
            await store.SaveAsync();

            var reloaded = new CommandPaletteUsageStore(path);
            IReadOnlyDictionary<string, CommandPaletteUsageEntry> snapshot = reloaded.Load();

            Assert.Equal(1, snapshot["settings"].UseCount);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Back_to_back_saves_all_complete_and_the_last_one_wins()
    {
        // Each save is queued to the pool while the next RecordUse runs, so older saves are
        // superseded mid-flight. Every task must still complete, and the file must end up with
        // the newest entries: an older write that finished after a newer one would leave a
        // lower count behind.
        string tempRoot = CreateTempDirectory();
        string path = Path.Combine(tempRoot, "command-palette-usage.json");

        try
        {
            var store = new CommandPaletteUsageStore(path);
            var saves = new List<Task>();
            for (int i = 0; i < 50; i++)
            {
                store.RecordUse("settings", DateTimeOffset.UnixEpoch.AddMinutes(i));
                saves.Add(store.SaveAsync());
            }

            await Task.WhenAll(saves);

            IReadOnlyDictionary<string, CommandPaletteUsageEntry> snapshot = new CommandPaletteUsageStore(path).Load();
            Assert.Equal(50, snapshot["settings"].UseCount);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Load_UsesCaseInsensitiveDictionaryForDeserializedEntries()
    {
        string tempRoot = CreateTempDirectory();
        string path = Path.Combine(tempRoot, "command-palette-usage.json");

        try
        {
            File.WriteAllText(
                path,
                """
                {
                  "Settings": {
                    "CommandId": "Settings",
                    "UseCount": 3,
                    "LastUsedAt": "2026-05-26T12:00:00+00:00"
                  }
                }
                """);

            var store = new CommandPaletteUsageStore(path);
            IReadOnlyDictionary<string, CommandPaletteUsageEntry> snapshot = store.Load();

            Assert.True(snapshot.TryGetValue("settings", out CommandPaletteUsageEntry? entry));
            Assert.Equal(3, entry.UseCount);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Load_KeysDifferingOnlyByCase_KeepsTheLastOne()
    {
        string tempRoot = CreateTempDirectory();
        string path = Path.Combine(tempRoot, "command-palette-usage.json");

        try
        {
            // Both keys survive the case-sensitive deserialize. Collapsing them must not throw,
            // because the catch below Load's parse would discard the whole history.
            File.WriteAllText(
                path,
                """
                {
                  "settings": {
                    "CommandId": "settings",
                    "UseCount": 3,
                    "LastUsedAt": "2026-05-26T12:00:00+00:00"
                  },
                  "Settings": {
                    "CommandId": "Settings",
                    "UseCount": 7,
                    "LastUsedAt": "2026-05-27T12:00:00+00:00"
                  }
                }
                """);

            var store = new CommandPaletteUsageStore(path);
            IReadOnlyDictionary<string, CommandPaletteUsageEntry> snapshot = store.Load();

            Assert.Equal(7, Assert.Single(snapshot).Value.UseCount);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ntilde_usage_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
