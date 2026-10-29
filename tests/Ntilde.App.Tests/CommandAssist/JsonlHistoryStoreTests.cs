using System.Globalization;
using System.Text.Json;
using Ntilde.CommandAssist.Models;
using Ntilde.CommandAssist.Storage;

namespace Ntilde.Tests.CommandAssist;

public sealed class JsonlHistoryStoreTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _historyPath;
    private readonly string _legacyPath;

    public JsonlHistoryStoreTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"ntilde_command_assist_history_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        _historyPath = Path.Combine(_tempRoot, "history.jsonl");
        _legacyPath = Path.Combine(_tempRoot, "history.json");
    }

    private JsonlHistoryStore CreateStore(int maxEntries = 50)
        => new(_historyPath, maxEntries, _legacyPath);

    [Fact]
    public async Task AppendAsync_PersistsEntriesAcrossStoreInstances()
    {
        JsonlHistoryStore store = CreateStore();

        await store.AppendAsync(CreateEntry("git status", executedAt: DateTimeOffset.Parse("2026-03-01T10:00:00+00:00", CultureInfo.InvariantCulture)), TestContext.Current.CancellationToken);

        IReadOnlyList<CommandHistoryEntry> entries = await CreateStore().GetRecentAsync(10, TestContext.Current.CancellationToken);

        Assert.Single(entries);
        Assert.Equal("git status", entries[0].CommandText);
    }

    [Fact]
    public async Task AppendAsync_WritesOneLinePerRecord()
    {
        JsonlHistoryStore store = CreateStore();

        await store.AppendAsync(CreateEntry("git status", executedAt: DateTimeOffset.Parse("2026-03-01T10:00:00+00:00", CultureInfo.InvariantCulture)), TestContext.Current.CancellationToken);
        await store.AppendAsync(CreateEntry("dotnet test", executedAt: DateTimeOffset.Parse("2026-03-01T10:01:00+00:00", CultureInfo.InvariantCulture)), TestContext.Current.CancellationToken);

        string[] lines = await File.ReadAllLinesAsync(_historyPath, TestContext.Current.CancellationToken);

        Assert.Equal(2, lines.Length);
        Assert.All(lines, line => Assert.NotNull(
            JsonSerializer.Deserialize(line, CommandAssistJsonLinesContext.Default.CommandHistoryEntry)));
    }

    /// <summary>
    /// Appends must not rewrite the file. Asserted structurally: the first record's bytes are still
    /// the first bytes on disk after later appends.
    /// </summary>
    [Fact]
    public async Task AppendAsync_LeavesEarlierLinesUntouched()
    {
        JsonlHistoryStore store = CreateStore();
        await store.AppendAsync(CreateEntry("git status", executedAt: DateTimeOffset.Parse("2026-03-01T10:00:00+00:00", CultureInfo.InvariantCulture)), TestContext.Current.CancellationToken);
        string firstLine = (await File.ReadAllLinesAsync(_historyPath, TestContext.Current.CancellationToken))[0];

        await store.AppendAsync(CreateEntry("dotnet test", executedAt: DateTimeOffset.Parse("2026-03-01T10:01:00+00:00", CultureInfo.InvariantCulture)), TestContext.Current.CancellationToken);

        Assert.Equal(firstLine, (await File.ReadAllLinesAsync(_historyPath, TestContext.Current.CancellationToken))[0]);
    }

    [Fact]
    public async Task AppendAsync_EnforcesRetentionLimitByKeepingMostRecentEntries()
    {
        JsonlHistoryStore store = CreateStore(maxEntries: 2);

        await store.AppendAsync(CreateEntry("git status", executedAt: DateTimeOffset.Parse("2026-03-01T10:00:00+00:00", CultureInfo.InvariantCulture)), TestContext.Current.CancellationToken);
        await store.AppendAsync(CreateEntry("dotnet test", executedAt: DateTimeOffset.Parse("2026-03-01T10:01:00+00:00", CultureInfo.InvariantCulture)), TestContext.Current.CancellationToken);
        await store.AppendAsync(CreateEntry("npm run build", executedAt: DateTimeOffset.Parse("2026-03-01T10:02:00+00:00", CultureInfo.InvariantCulture)), TestContext.Current.CancellationToken);

        IReadOnlyList<CommandHistoryEntry> entries = await CreateStore(maxEntries: 2).GetRecentAsync(10, TestContext.Current.CancellationToken);

        Assert.Equal(2, entries.Count);
        Assert.DoesNotContain(entries, entry => entry.CommandText == "git status");
        Assert.Equal("npm run build", entries[0].CommandText);
        Assert.Equal("dotnet test", entries[1].CommandText);
    }

    /// <summary>
    /// An append-only log holds more physical lines than the cap until compaction reclaims them,
    /// but "history keeps N entries" has to mean the same thing to a reader either way. Reads
    /// enforce the cap so the promise does not drift with compaction timing.
    /// </summary>
    [Fact]
    public async Task GetRecentAsync_NeverReturnsMoreThanTheRetentionCap()
    {
        JsonlHistoryStore store = CreateStore(maxEntries: 2);

        await store.AppendAsync(CreateEntry("git status", executedAt: DateTimeOffset.Parse("2026-03-01T10:00:00+00:00", CultureInfo.InvariantCulture)), TestContext.Current.CancellationToken);
        await store.AppendAsync(CreateEntry("dotnet test", executedAt: DateTimeOffset.Parse("2026-03-01T10:01:00+00:00", CultureInfo.InvariantCulture)), TestContext.Current.CancellationToken);
        await store.AppendAsync(CreateEntry("npm run build", executedAt: DateTimeOffset.Parse("2026-03-01T10:02:00+00:00", CultureInfo.InvariantCulture)), TestContext.Current.CancellationToken);

        // Three lines are still on disk - the file is far below the compaction floor.
        Assert.Equal(3, (await File.ReadAllLinesAsync(_historyPath, TestContext.Current.CancellationToken)).Length);

        IReadOnlyList<CommandHistoryEntry> entries = await store.GetRecentAsync(10, TestContext.Current.CancellationToken);

        Assert.Equal(2, entries.Count);
        Assert.Equal("npm run build", entries[0].CommandText);
        Assert.Equal("dotnet test", entries[1].CommandText);
    }

    [Fact]
    public async Task SearchAsync_NeverReturnsCandidatesBeyondTheRetentionCap()
    {
        JsonlHistoryStore store = CreateStore(maxEntries: 1);

        await store.AppendAsync(CreateEntry("git status", executedAt: DateTimeOffset.Parse("2026-03-01T10:00:00+00:00", CultureInfo.InvariantCulture)), TestContext.Current.CancellationToken);
        await store.AppendAsync(CreateEntry("git stash pop", executedAt: DateTimeOffset.Parse("2026-03-01T10:01:00+00:00", CultureInfo.InvariantCulture)), TestContext.Current.CancellationToken);

        CommandHistoryEntry candidate = Assert.Single(await store.SearchAsync("git", maxCandidates: 10, TestContext.Current.CancellationToken));

        Assert.Equal("git stash pop", candidate.CommandText);
    }

    [Fact]
    public async Task SetMaxEntries_WhenLowered_AppliesToTheLiveStoreAndCompactsTheFile()
    {
        JsonlHistoryStore store = CreateStore(maxEntries: 50);
        await store.AppendAsync(CreateEntry("git status", executedAt: DateTimeOffset.Parse("2026-03-01T10:00:00+00:00", CultureInfo.InvariantCulture)), TestContext.Current.CancellationToken);
        await store.AppendAsync(CreateEntry("dotnet test", executedAt: DateTimeOffset.Parse("2026-03-01T10:01:00+00:00", CultureInfo.InvariantCulture)), TestContext.Current.CancellationToken);
        await store.AppendAsync(CreateEntry("npm run build", executedAt: DateTimeOffset.Parse("2026-03-01T10:02:00+00:00", CultureInfo.InvariantCulture)), TestContext.Current.CancellationToken);

        store.SetMaxEntries(1);

        CommandHistoryEntry kept = Assert.Single(await store.GetRecentAsync(10, TestContext.Current.CancellationToken));
        Assert.Equal("npm run build", kept.CommandText);

        // The lowered cap reached the file, not just the in-memory view, so every later reader
        // agrees - including a fresh instance built after a restart.
        Assert.Single(await File.ReadAllLinesAsync(_historyPath, TestContext.Current.CancellationToken));
        Assert.Single(await CreateStore(maxEntries: 1).GetRecentAsync(10, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SetMaxEntries_WhenRaised_KeepsEntriesTheOldCapWouldHaveDropped()
    {
        JsonlHistoryStore store = CreateStore(maxEntries: 1);
        await store.AppendAsync(CreateEntry("git status", executedAt: DateTimeOffset.Parse("2026-03-01T10:00:00+00:00", CultureInfo.InvariantCulture)), TestContext.Current.CancellationToken);
        await store.AppendAsync(CreateEntry("dotnet test", executedAt: DateTimeOffset.Parse("2026-03-01T10:01:00+00:00", CultureInfo.InvariantCulture)), TestContext.Current.CancellationToken);

        Assert.Single(await store.GetRecentAsync(10, TestContext.Current.CancellationToken));

        store.SetMaxEntries(50);

        Assert.Equal(2, (await store.GetRecentAsync(10, TestContext.Current.CancellationToken)).Count);
    }

    /// <summary>
    /// A whole-file read failure is not a corrupt line. The in-memory view is a prefix of the
    /// truth, so rewriting the file from it would delete everything past the failure point -
    /// which is exactly what treating the two the same used to do.
    /// </summary>
    [Fact]
    public async Task GetRecentAsync_WhenTheFileCannotBeRead_LeavesItIntactAndRecoversOnTheNextRead()
    {
        await File.WriteAllLinesAsync(_historyPath, new[]
        {
            Serialize(CreateEntry("git status", executedAt: DateTimeOffset.Parse("2026-03-01T10:00:00+00:00", CultureInfo.InvariantCulture))),
            Serialize(CreateEntry("dotnet test", executedAt: DateTimeOffset.Parse("2026-03-01T10:01:00+00:00", CultureInfo.InvariantCulture))),
            Serialize(CreateEntry("npm run build", executedAt: DateTimeOffset.Parse("2026-03-01T10:02:00+00:00", CultureInfo.InvariantCulture)))
        }, TestContext.Current.CancellationToken);

        JsonlHistoryStore store = CreateStore();

        IReadOnlyList<CommandHistoryEntry> duringFailure;
        using (new FileStream(_historyPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            duringFailure = await store.GetRecentAsync(10, TestContext.Current.CancellationToken);
        }

        Assert.Empty(duringFailure);
        Assert.Equal(3, (await File.ReadAllLinesAsync(_historyPath, TestContext.Current.CancellationToken)).Length);

        // The partial view was never cached as the truth, so the same instance heals itself.
        Assert.Equal(3, (await store.GetRecentAsync(10, TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task ClearAsync_RemovesPersistedHistory()
    {
        JsonlHistoryStore store = CreateStore();

        await store.AppendAsync(CreateEntry("git status"), TestContext.Current.CancellationToken);
        await store.ClearAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await store.GetRecentAsync(10, TestContext.Current.CancellationToken));
        Assert.Empty(await CreateStore().GetRecentAsync(10, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// <strong>"Clear history" has to be true, and the backup the migration leaves behind is where a
    /// V1-captured secret would still be sitting.</strong>
    /// </summary>
    /// <remarks>
    /// The V1 Enter-time capture read a keystroke mirror with no echo check, so a password typed at a
    /// non-echoing prompt in a markless session was written to <c>history.json</c> verbatim -
    /// <c>SecretsFilter</c> matches nothing on a bare word. The migration copies those entries into
    /// <c>history.jsonl</c> unfiltered and renames the source to <c>.bak</c>, so truncating only the
    /// live log left exactly the entries the user was trying to erase on disk, under a confirmation
    /// prompt that said otherwise. A user who suspects a secret is in their history has one control
    /// and it has to work.
    /// </remarks>
    [Fact]
    public async Task ClearAsync_AlsoDeletesTheMigratedLegacyBackup()
    {
        await File.WriteAllTextAsync(
            _legacyPath,
            JsonSerializer.Serialize(
                new List<CommandHistoryEntry> { CreateEntry("hunter2") },
                CommandAssistJsonContext.Default.ListCommandHistoryEntry), TestContext.Current.CancellationToken);

        JsonlHistoryStore store = CreateStore();

        // The read is what runs the migration; after it, the secret lives in two files.
        Assert.Single(await store.GetRecentAsync(10, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(_legacyPath + ".bak"));

        await store.ClearAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await store.GetRecentAsync(10, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(_legacyPath + ".bak"));
    }

    /// <summary>
    /// And an un-migrated legacy file goes too, or the next launch migrates it straight back in.
    /// </summary>
    /// <remarks>
    /// Reachable because <c>ClearAsync</c> does not load: clearing before anything has read the store
    /// writes an empty <c>history.jsonl</c> beside an untouched V1 file, and the next load retires that
    /// file to <c>.bak</c> rather than deleting it - so without this the entries the user asked to erase
    /// would sit there through the clear and every later read, invisible.
    /// </remarks>
    [Fact]
    public async Task ClearAsync_BeforeAnyRead_AlsoDeletesTheUnmigratedLegacyFile()
    {
        await File.WriteAllTextAsync(
            _legacyPath,
            JsonSerializer.Serialize(
                new List<CommandHistoryEntry> { CreateEntry("hunter2") },
                CommandAssistJsonContext.Default.ListCommandHistoryEntry), TestContext.Current.CancellationToken);

        await CreateStore().ClearAsync(TestContext.Current.CancellationToken);

        Assert.False(File.Exists(_legacyPath));
        Assert.Empty(await CreateStore().GetRecentAsync(10, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetRecentAsync_WhenLineIsCorrupt_SkipsItAndKeepsTheRest()
    {
        await File.WriteAllLinesAsync(_historyPath, new[]
        {
            Serialize(CreateEntry("git status", executedAt: DateTimeOffset.Parse("2026-03-01T10:00:00+00:00", CultureInfo.InvariantCulture))),
            "{ this is not valid json",
            Serialize(CreateEntry("dotnet test", executedAt: DateTimeOffset.Parse("2026-03-01T10:01:00+00:00", CultureInfo.InvariantCulture)))
        }, TestContext.Current.CancellationToken);

        IReadOnlyList<CommandHistoryEntry> entries = await CreateStore().GetRecentAsync(10, TestContext.Current.CancellationToken);

        Assert.Equal(2, entries.Count);
        Assert.Equal("dotnet test", entries[0].CommandText);
        Assert.Equal("git status", entries[1].CommandText);
    }

    /// <summary>A crash mid-append truncates the last line; that costs one command, not the file.</summary>
    [Fact]
    public async Task GetRecentAsync_WhenFinalLineIsTruncated_KeepsPrecedingEntries()
    {
        string good = Serialize(CreateEntry("git status", executedAt: DateTimeOffset.Parse("2026-03-01T10:00:00+00:00", CultureInfo.InvariantCulture)));
        string truncated = Serialize(CreateEntry("dotnet test"))[..20];
        await File.WriteAllTextAsync(_historyPath, good + Environment.NewLine + truncated, TestContext.Current.CancellationToken);

        IReadOnlyList<CommandHistoryEntry> entries = await CreateStore().GetRecentAsync(10, TestContext.Current.CancellationToken);

        Assert.Single(entries);
        Assert.Equal("git status", entries[0].CommandText);
    }

    [Fact]
    public async Task SearchAsync_ReturnsSubsequenceCandidatesMostRecentFirst()
    {
        JsonlHistoryStore store = CreateStore();

        await store.AppendAsync(CreateEntry("git status", executedAt: DateTimeOffset.Parse("2026-03-01T10:00:00+00:00", CultureInfo.InvariantCulture)), TestContext.Current.CancellationToken);
        await store.AppendAsync(CreateEntry("git stash pop", executedAt: DateTimeOffset.Parse("2026-03-01T10:01:00+00:00", CultureInfo.InvariantCulture)), TestContext.Current.CancellationToken);
        await store.AppendAsync(CreateEntry("docker ps", executedAt: DateTimeOffset.Parse("2026-03-01T10:02:00+00:00", CultureInfo.InvariantCulture)), TestContext.Current.CancellationToken);

        IReadOnlyList<CommandHistoryEntry> entries = await store.SearchAsync("git sta", maxCandidates: 5, TestContext.Current.CancellationToken);

        // Recall gate, not a ranking: both git rows qualify, "docker ps" does not, and the order is
        // recency because that is all a candidate cap can honestly promise.
        Assert.Equal(2, entries.Count);
        Assert.Equal("git stash pop", entries[0].CommandText);
        Assert.Equal("git status", entries[1].CommandText);
    }

    /// <summary>
    /// The gate admits exactly what <c>CommandAssistSuggestionEngine.ScoreText</c> would score above
    /// zero, which includes non-contiguous subsequence matches.
    /// </summary>
    [Fact]
    public async Task SearchAsync_AdmitsSubsequenceMatchesTheEngineWouldScore()
    {
        JsonlHistoryStore store = CreateStore();
        await store.AppendAsync(CreateEntry("dotnet test"), TestContext.Current.CancellationToken);

        Assert.Single(await store.SearchAsync("dtt", maxCandidates: 5, TestContext.Current.CancellationToken));
        Assert.Empty(await store.SearchAsync("zzz", maxCandidates: 5, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SearchAsync_WhenQueryIsEmpty_ReturnsEveryEntry()
    {
        JsonlHistoryStore store = CreateStore();
        await store.AppendAsync(CreateEntry("git status"), TestContext.Current.CancellationToken);
        await store.AppendAsync(CreateEntry("dotnet test"), TestContext.Current.CancellationToken);

        Assert.Equal(2, (await store.SearchAsync("  ", maxCandidates: 10, TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task TryUpdateExecutionResultAsync_AppendsSupersedingRecordResolvedOnReload()
    {
        JsonlHistoryStore store = CreateStore();
        CommandHistoryEntry entry = CreateEntry(
            "git status",
            executedAt: DateTimeOffset.Parse("2026-03-01T10:00:00+00:00", CultureInfo.InvariantCulture),
            exitCode: null);
        await store.AppendAsync(entry, TestContext.Current.CancellationToken);

        bool updated = await store.TryUpdateExecutionResultAsync(entry.Id, 0, 2500, cancellationToken: TestContext.Current.CancellationToken);

        // Two physical lines, one live entry: the update is a superseding record, not a rewrite.
        Assert.Equal(2, (await File.ReadAllLinesAsync(_historyPath, TestContext.Current.CancellationToken)).Length);
        IReadOnlyList<CommandHistoryEntry> entries = await CreateStore().GetRecentAsync(10, TestContext.Current.CancellationToken);

        Assert.True(updated);
        Assert.Single(entries);
        Assert.Equal(0, entries[0].ExitCode);
        Assert.Equal(2500, entries[0].DurationMs);
    }

    [Fact]
    public async Task TryUpdateExecutionResultAsync_WhenEntryIsUnknown_ReturnsFalseWithoutWriting()
    {
        JsonlHistoryStore store = CreateStore();
        await store.AppendAsync(CreateEntry("git status"), TestContext.Current.CancellationToken);

        bool updated = await store.TryUpdateExecutionResultAsync("no-such-id", 1, 10, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(updated);
        Assert.Single(await File.ReadAllLinesAsync(_historyPath, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Later records win regardless of the entries' timestamps: resolution is by file position.
    /// </summary>
    [Fact]
    public async Task GetRecentAsync_WhenIdAppearsTwice_KeepsTheLastRecord()
    {
        CommandHistoryEntry original = CreateEntry("git status", exitCode: null);
        await File.WriteAllLinesAsync(_historyPath, new[]
        {
            Serialize(original),
            Serialize(original with { ExitCode = 42, CommandText = "git status --short" })
        }, TestContext.Current.CancellationToken);

        IReadOnlyList<CommandHistoryEntry> entries = await CreateStore().GetRecentAsync(10, TestContext.Current.CancellationToken);

        Assert.Single(entries);
        Assert.Equal(42, entries[0].ExitCode);
        Assert.Equal("git status --short", entries[0].CommandText);
    }

    [Fact]
    public async Task AppendAsync_WhenDeadRecordsDominate_CompactsTheFile()
    {
        JsonlHistoryStore store = CreateStore();
        CommandHistoryEntry entry = CreateEntry("git status", exitCode: null);
        await store.AppendAsync(entry, TestContext.Current.CancellationToken);

        // Every update supersedes the same entry, so all but one line is dead. Past the minimum
        // line count the dead-ratio rule fires and the log collapses back to the live set.
        int updates = JsonlHistoryStore.CompactionMinimumLines + 4;
        for (int i = 0; i < updates; i++)
        {
            await store.TryUpdateExecutionResultAsync(entry.Id, i, i, cancellationToken: TestContext.Current.CancellationToken);
        }

        string[] lines = await File.ReadAllLinesAsync(_historyPath, TestContext.Current.CancellationToken);

        Assert.True(
            lines.Length < JsonlHistoryStore.CompactionMinimumLines,
            $"Expected compaction to collapse the log, but {lines.Length} lines remain.");
        IReadOnlyList<CommandHistoryEntry> entries = await CreateStore().GetRecentAsync(10, TestContext.Current.CancellationToken);
        Assert.Single(entries);
        Assert.Equal(updates - 1, entries[0].ExitCode);
    }

    [Fact]
    public async Task GetRecentAsync_WhenFileExceedsCap_CompactsToTheCapOnLoad()
    {
        var lines = new List<string>();
        for (int i = 0; i < 40; i++)
        {
            lines.Add(Serialize(CreateEntry(
                $"command-{i}",
                executedAt: DateTimeOffset.Parse("2026-03-01T10:00:00+00:00", CultureInfo.InvariantCulture).AddMinutes(i))));
        }

        await File.WriteAllLinesAsync(_historyPath, lines, TestContext.Current.CancellationToken);

        IReadOnlyList<CommandHistoryEntry> entries = await CreateStore(maxEntries: 5).GetRecentAsync(50, TestContext.Current.CancellationToken);

        Assert.Equal(5, entries.Count);
        Assert.Equal("command-39", entries[0].CommandText);
        Assert.Equal(5, (await File.ReadAllLinesAsync(_historyPath, TestContext.Current.CancellationToken)).Length);
    }

    [Fact]
    public async Task Load_WhenLegacyJsonExists_MigratesItAndBacksItUp()
    {
        var legacyEntries = new List<CommandHistoryEntry>
        {
            CreateEntry("git status", executedAt: DateTimeOffset.Parse("2026-03-01T10:00:00+00:00", CultureInfo.InvariantCulture)),
            CreateEntry("dotnet test", executedAt: DateTimeOffset.Parse("2026-03-01T10:01:00+00:00", CultureInfo.InvariantCulture))
        };
        await File.WriteAllTextAsync(
            _legacyPath,
            JsonSerializer.Serialize(legacyEntries, CommandAssistJsonContext.Default.ListCommandHistoryEntry), TestContext.Current.CancellationToken);

        IReadOnlyList<CommandHistoryEntry> entries = await CreateStore().GetRecentAsync(10, TestContext.Current.CancellationToken);

        Assert.Equal(2, entries.Count);
        Assert.Equal("dotnet test", entries[0].CommandText);
        Assert.True(File.Exists(_historyPath));
        Assert.Equal(2, (await File.ReadAllLinesAsync(_historyPath, TestContext.Current.CancellationToken)).Length);

        // User data is renamed, never deleted.
        Assert.False(File.Exists(_legacyPath));
        Assert.True(File.Exists(_legacyPath + ".bak"));
    }

    [Fact]
    public async Task Load_WhenLegacyJsonExceedsCap_MigratesOnlyTheMostRecentEntries()
    {
        List<CommandHistoryEntry> legacyEntries = Enumerable.Range(0, 10)
            .Select(i => CreateEntry(
                $"command-{i}",
                executedAt: DateTimeOffset.Parse("2026-03-01T10:00:00+00:00", CultureInfo.InvariantCulture).AddMinutes(i)))
            .ToList();
        await File.WriteAllTextAsync(
            _legacyPath,
            JsonSerializer.Serialize(legacyEntries, CommandAssistJsonContext.Default.ListCommandHistoryEntry), TestContext.Current.CancellationToken);

        IReadOnlyList<CommandHistoryEntry> entries = await CreateStore(maxEntries: 3).GetRecentAsync(10, TestContext.Current.CancellationToken);

        Assert.Equal(3, entries.Count);
        Assert.Equal("command-9", entries[0].CommandText);
    }

    /// <summary>
    /// A <c>history.json</c> stranded beside a live <c>history.jsonl</c> is retired to <c>.bak</c> on
    /// the next load, and its records are not replayed into the log.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The migration guard used to skip the legacy file outright once <c>history.jsonl</c> existed, so
    /// this pairing - reachable when the migration wrote the JSONL and then failed at the rename, or
    /// when a pre-V2 build ran again afterwards - left the V1 file sitting there for good, retired by
    /// nothing except the user pressing Clear history. That is the file that can hold a password
    /// verbatim (see the remarks on <c>ClearAsync</c>), so "the V1 file does not survive a load" has to
    /// hold whether or not there was anywhere to migrate it into.
    /// </para>
    /// <para>
    /// Retired, not imported: V1 ids carry into the JSONL unchanged, so replaying those records would
    /// overwrite live entries with their pre-patch selves and drop every exit code and typo flag
    /// collected since.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Load_WhenJsonlAlreadyExists_RetiresTheStrandedLegacyFileWithoutImportingIt()
    {
        await File.WriteAllTextAsync(
            _legacyPath,
            JsonSerializer.Serialize(
                new List<CommandHistoryEntry> { CreateEntry("hunter2") },
                CommandAssistJsonContext.Default.ListCommandHistoryEntry), TestContext.Current.CancellationToken);
        await File.WriteAllLinesAsync(_historyPath, new[] { Serialize(CreateEntry("git status")) }, TestContext.Current.CancellationToken);

        IReadOnlyList<CommandHistoryEntry> entries = await CreateStore().GetRecentAsync(10, TestContext.Current.CancellationToken);

        Assert.Equal("git status", Assert.Single(entries).CommandText);
        Assert.False(File.Exists(_legacyPath));
        Assert.True(File.Exists(_legacyPath + ".bak"));
    }

    /// <summary>
    /// The stranded case can arrive with a <c>.bak</c> already on disk - a migration that succeeded,
    /// then a pre-V2 build that wrote <c>history.json</c> again - and it is the newer file that has to
    /// end up retired.
    /// </summary>
    [Fact]
    public async Task Load_WhenAStrandedLegacyFileAndAnOlderBackupBothExist_RetiresTheStrandedOne()
    {
        await File.WriteAllTextAsync(_legacyPath + ".bak", "[]", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            _legacyPath,
            JsonSerializer.Serialize(
                new List<CommandHistoryEntry> { CreateEntry("hunter2") },
                CommandAssistJsonContext.Default.ListCommandHistoryEntry), TestContext.Current.CancellationToken);
        await File.WriteAllLinesAsync(_historyPath, new[] { Serialize(CreateEntry("git status")) }, TestContext.Current.CancellationToken);

        Assert.Equal("git status", Assert.Single(await CreateStore().GetRecentAsync(10, TestContext.Current.CancellationToken)).CommandText);

        Assert.False(File.Exists(_legacyPath));
        Assert.Contains("hunter2", await File.ReadAllTextAsync(_legacyPath + ".bak", TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    /// <summary>
    /// Retiring the stranded legacy file is housekeeping, so a rename that cannot happen - a legacy
    /// file held open by a backup agent, a read-only volume - costs the rename and nothing else. The
    /// next launch retries it.
    /// </summary>
    /// <remarks>
    /// The failure is provoked with a directory parked on the backup path rather than an open handle,
    /// because that fails the same way on every platform: POSIX <c>rename</c> ignores the advisory lock
    /// that <c>FileShare.None</c> takes, so the Windows-only trick would quietly succeed on the Linux
    /// leg of the matrix and assert nothing.
    /// </remarks>
    [Fact]
    public async Task Load_WhenTheStrandedLegacyFileCannotBeRetired_StillReturnsHistory()
    {
        await File.WriteAllTextAsync(_legacyPath, "[]", TestContext.Current.CancellationToken);
        await File.WriteAllLinesAsync(_historyPath, new[] { Serialize(CreateEntry("git status")) }, TestContext.Current.CancellationToken);

        string blocked = _legacyPath + ".bak";
        Directory.CreateDirectory(blocked);
        await File.WriteAllTextAsync(Path.Combine(blocked, "in-the-way.txt"), "x", TestContext.Current.CancellationToken);

        Assert.Equal("git status", Assert.Single(await CreateStore().GetRecentAsync(10, TestContext.Current.CancellationToken)).CommandText);
        Assert.True(File.Exists(_legacyPath));

        Directory.Delete(blocked, recursive: true);

        Assert.Equal("git status", Assert.Single(await CreateStore().GetRecentAsync(10, TestContext.Current.CancellationToken)).CommandText);
        Assert.False(File.Exists(_legacyPath));
    }

    [Fact]
    public async Task Load_WhenLegacyJsonIsCorrupt_StartsCleanAndStillBacksItUp()
    {
        await File.WriteAllTextAsync(_legacyPath, "{ this is not valid json", TestContext.Current.CancellationToken);

        IReadOnlyList<CommandHistoryEntry> entries = await CreateStore().GetRecentAsync(10, TestContext.Current.CancellationToken);

        Assert.Empty(entries);
        Assert.False(File.Exists(_legacyPath));
        Assert.True(File.Exists(_legacyPath + ".bak"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private static string Serialize(CommandHistoryEntry entry)
        => JsonSerializer.Serialize(entry, CommandAssistJsonLinesContext.Default.CommandHistoryEntry);

    // ---------------------------- retroactive typo flagging (dogfood round 4, item 4)

    /// <summary>
    /// The owner's case. Three <c>gti</c> lines captured before the flag existed, all unflagged; he
    /// reproduces the typo once and every one of them is suppressed, without his having to retype the
    /// other two.
    /// </summary>
    [Fact]
    public async Task TryMarkInvalidCommandsByFirstTokenAsync_FlagsEveryOlderEntryWithTheSameFirstToken()
    {
        JsonlHistoryStore store = CreateStore();
        await store.AppendAsync(CreateEntry("gti status", DateTimeOffset.Parse("2026-03-01T10:00:00+00:00", CultureInfo.InvariantCulture)), TestContext.Current.CancellationToken);
        await store.AppendAsync(CreateEntry("gti log --oneline", DateTimeOffset.Parse("2026-03-01T10:01:00+00:00", CultureInfo.InvariantCulture)), TestContext.Current.CancellationToken);
        await store.AppendAsync(CreateEntry("git status", DateTimeOffset.Parse("2026-03-01T10:02:00+00:00", CultureInfo.InvariantCulture)), TestContext.Current.CancellationToken);

        int flagged = await store.TryMarkInvalidCommandsByFirstTokenAsync("gti", TestContext.Current.CancellationToken);

        Assert.Equal(2, flagged);

        IReadOnlyList<CommandHistoryEntry> entries = await CreateStore().GetRecentAsync(10, TestContext.Current.CancellationToken);
        Assert.All(
            entries.Where(entry => entry.CommandText.StartsWith("gti", StringComparison.Ordinal)),
            entry => Assert.True(entry.IsInvalidCommand));

        // And nothing else moved: `git status` is a different program that happens to share a prefix.
        Assert.False(entries.Single(entry => entry.CommandText == "git status").IsInvalidCommand);
    }

    /// <summary>
    /// The sweep survives a reload, because it is written as superseding records rather than held in
    /// memory - a flag the next launch forgets is not a suppression.
    /// </summary>
    [Fact]
    public async Task TryMarkInvalidCommandsByFirstTokenAsync_PersistsAcrossStoreInstances()
    {
        JsonlHistoryStore store = CreateStore();
        await store.AppendAsync(CreateEntry("gti status", DateTimeOffset.Parse("2026-03-01T10:00:00+00:00", CultureInfo.InvariantCulture)), TestContext.Current.CancellationToken);

        await store.TryMarkInvalidCommandsByFirstTokenAsync("gti", TestContext.Current.CancellationToken);

        IReadOnlyList<CommandHistoryEntry> entries = await CreateStore().GetRecentAsync(10, TestContext.Current.CancellationToken);
        Assert.True(entries.Single().IsInvalidCommand);
    }

    /// <summary>
    /// Case-insensitive, matching how <c>CommandAssistSuggestionEngine</c> groups history: if the
    /// grouping treats two spellings as one row, the suppression has to reach both or the row survives.
    /// </summary>
    [Fact]
    public async Task TryMarkInvalidCommandsByFirstTokenAsync_MatchesCaseInsensitively()
    {
        JsonlHistoryStore store = CreateStore();
        await store.AppendAsync(CreateEntry("GTI status", DateTimeOffset.Parse("2026-03-01T10:00:00+00:00", CultureInfo.InvariantCulture)), TestContext.Current.CancellationToken);

        Assert.Equal(1, await store.TryMarkInvalidCommandsByFirstTokenAsync("gti", TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Idempotent, and it says so in its return value: a user who mistypes the same word twice must not
    /// pay for a second pass of writes.
    /// </summary>
    [Fact]
    public async Task TryMarkInvalidCommandsByFirstTokenAsync_SkipsEntriesAlreadyFlagged()
    {
        JsonlHistoryStore store = CreateStore();
        await store.AppendAsync(CreateEntry("gti status", DateTimeOffset.Parse("2026-03-01T10:00:00+00:00", CultureInfo.InvariantCulture)), TestContext.Current.CancellationToken);
        await store.TryMarkInvalidCommandsByFirstTokenAsync("gti", TestContext.Current.CancellationToken);

        int lineCount = (await File.ReadAllLinesAsync(_historyPath, TestContext.Current.CancellationToken)).Length;

        Assert.Equal(0, await store.TryMarkInvalidCommandsByFirstTokenAsync("gti", TestContext.Current.CancellationToken));
        Assert.Equal(lineCount, (await File.ReadAllLinesAsync(_historyPath, TestContext.Current.CancellationToken)).Length);
    }

    [Fact]
    public async Task TryMarkInvalidCommandsByFirstTokenAsync_WithABlankToken_DoesNothing()
    {
        JsonlHistoryStore store = CreateStore();
        await store.AppendAsync(CreateEntry("gti status", DateTimeOffset.Parse("2026-03-01T10:00:00+00:00", CultureInfo.InvariantCulture)), TestContext.Current.CancellationToken);

        Assert.Equal(0, await store.TryMarkInvalidCommandsByFirstTokenAsync("   ", TestContext.Current.CancellationToken));
        Assert.False((await store.GetRecentAsync(10, TestContext.Current.CancellationToken)).Single().IsInvalidCommand);
    }

    /// <summary>
    /// The load-time backfill: an exit-127 line written before the flag existed comes back flagged,
    /// because the information was on disk the whole time and was simply not being read.
    /// </summary>
    [Fact]
    public async Task Load_BackfillsTheInvalidFlagForExitCode127()
    {
        // Written through the store so the line on disk is a real one - and with the flag left at its
        // default, which is exactly what a pre-flag record deserialises to.
        JsonlHistoryStore store = CreateStore();
        await store.AppendAsync(CreateEntry("gti status", DateTimeOffset.Parse("2026-03-01T10:00:00+00:00", CultureInfo.InvariantCulture), exitCode: 127), TestContext.Current.CancellationToken);
        await store.AppendAsync(CreateEntry("git status", DateTimeOffset.Parse("2026-03-01T10:01:00+00:00", CultureInfo.InvariantCulture), exitCode: 0), TestContext.Current.CancellationToken);
        await store.AppendAsync(CreateEntry("dotnet test", DateTimeOffset.Parse("2026-03-01T10:02:00+00:00", CultureInfo.InvariantCulture), exitCode: 1), TestContext.Current.CancellationToken);

        IReadOnlyList<CommandHistoryEntry> entries = await CreateStore().GetRecentAsync(10, TestContext.Current.CancellationToken);

        Assert.True(entries.Single(entry => entry.CommandText == "gti status").IsInvalidCommand);

        // Exactly the live capture rule and no wider: a command that ran and failed is not a typo.
        Assert.False(entries.Single(entry => entry.CommandText == "git status").IsInvalidCommand);
        Assert.False(entries.Single(entry => entry.CommandText == "dotnet test").IsInvalidCommand);
    }

    /// <summary>
    /// A never-completed entry has no exit code at all, and an absent code is not a 127.
    /// </summary>
    [Fact]
    public async Task Load_DoesNotBackfillAnEntryWithNoExitCode()
    {
        JsonlHistoryStore store = CreateStore();
        await store.AppendAsync(CreateEntry("gti status", DateTimeOffset.Parse("2026-03-01T10:00:00+00:00", CultureInfo.InvariantCulture), exitCode: null), TestContext.Current.CancellationToken);

        Assert.False((await CreateStore().GetRecentAsync(10, TestContext.Current.CancellationToken)).Single().IsInvalidCommand);
    }

    [Theory]
    [InlineData("gti status", "gti")]
    [InlineData("   gti   status", "gti")]
    [InlineData("gti", "gti")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void FirstToken_IsTheLeadingWord(string commandText, string expected)
    {
        Assert.Equal(expected, CommandHistoryEntry.FirstToken(commandText));
    }

    private static CommandHistoryEntry CreateEntry(
        string commandText,
        DateTimeOffset? executedAt = null,
        int? exitCode = 0)
    {
        return new CommandHistoryEntry(
            Id: Guid.NewGuid().ToString("N"),
            CommandText: commandText,
            ExecutedAt: executedAt ?? DateTimeOffset.Parse("2026-03-01T10:00:00+00:00", CultureInfo.InvariantCulture),
            ShellKind: "pwsh",
            WorkingDirectory: @"C:\repo",
            ProfileId: "profile-1",
            SessionId: "session-1",
            HostId: null,
            ExitCode: exitCode,
            IsRemote: false,
            IsRedacted: false,
            Source: CommandCaptureSource.Heuristic,
            DurationMs: null);
    }
}
