using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Ntilde.Backup;

namespace Ntilde.Tests.Backup;

public sealed class SnapshotCaptureTests
{
    [Theory]
    [InlineData(SnapshotReason.Auto)]
    [InlineData(SnapshotReason.PreImport)]
    [InlineData(SnapshotReason.PreRestore)]
    public void Concurrent_save_cannot_label_an_archive_with_other_content(SnapshotReason reason)
    {
        using var tree = BackupTestTree.CreateEmpty();
        const string before = "{\"FontSize\":14}";
        const string after = "{\"FontSize\":24}";
        tree.WriteFile("settings.json", before);
        var clock = new SaveOnFirstReadClock(() => tree.WriteFile("settings.json", after));
        var service = new BackupService(tree.Root, clock);
        SnapshotInfo first = Assert.IsType<SnapshotInfo>(service.Snapshot(reason));
        string captured;
        using (var zip = ZipFile.OpenRead(first.FilePath))
        using (var reader = new StreamReader(zip.GetEntry("settings/settings.json")!.Open()))
            captured = reader.ReadToEnd();
        Assert.True(captured == before || captured == after);

        // Whichever revision was captured, dedupe must agree with its actual bytes.
        tree.WriteFile("settings.json", captured);
        clock.Advance();
        Assert.Null(service.Snapshot(SnapshotReason.Auto));
        tree.WriteFile("settings.json", captured == before ? after : before);
        clock.Advance();
        SnapshotInfo changed = Assert.IsType<SnapshotInfo>(service.Snapshot(SnapshotReason.Auto));
        Assert.NotEqual(first.ContentHash, changed.ContentHash);
        Assert.Equal(2, service.ListSnapshots().Count);
        Assert.Empty(Directory.GetFiles(service.BackupsDirectory, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Hash_preserves_catalog_order_normalized_path_order_and_exact_bytes()
    {
        using var tree = BackupTestTree.CreateEmpty();
        tree.WriteFile("settings.json", "{ \"FontSize\": 14 }");
        tree.WriteFile(Path.Combine("themes", "z.json"), "last");
        tree.WriteFile(Path.Combine("themes", "a", "theme.json"), "first");
        tree.WriteFile(Path.Combine("ssh", "profiles.json"), "profiles");
        var service = new BackupService(tree.Root);
        SnapshotInfo snapshot = Assert.IsType<SnapshotInfo>(service.Snapshot(SnapshotReason.Auto));
        const string legacyHashInput = "settings/settings.json\n{ \"FontSize\": 14 }themes/a/theme.json\nfirstthemes/z.json\nlastconnections/profiles.json\nprofiles";
        string expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(legacyHashInput))).ToLowerInvariant()[..16];
        Assert.Equal(expected, snapshot.ContentHash);
    }

    [Fact]
    public void Capture_failure_leaves_no_snapshot_or_temporary_bundle()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root)
        {
            SimulateExportFailureForTest = () => new IOException("capture failed")
        };
        Assert.Null(service.Snapshot(SnapshotReason.PreImport));
        Assert.Empty(service.ListSnapshots());
        if (Directory.Exists(service.BackupsDirectory))
            Assert.Empty(Directory.GetFiles(service.BackupsDirectory, "*", SearchOption.AllDirectories));
    }

    private sealed class SaveOnFirstReadClock(Action save) : TimeProvider
    {
        private Action? _save = save;
        private DateTimeOffset _now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        public void Advance() => _now += TimeSpan.FromMinutes(1);
        public override DateTimeOffset GetUtcNow()
        {
            Action? pending = Interlocked.Exchange(ref _save, null);
            pending?.Invoke();
            return _now;
        }
    }
}
