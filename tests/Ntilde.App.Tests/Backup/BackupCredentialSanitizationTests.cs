using System.IO.Compression;
using System.Text.Json;
using Ntilde.Backup;
using Ntilde.Shell;
using Ntilde.Shell.Backup;
using Ntilde.Shell.Secrets;

namespace Ntilde.Tests.Backup;

public sealed class BackupCredentialSanitizationTests
{
    private const string Secret = "SYNTHETIC-718-LEGACY-PASSWORD";

    [Theory]
    [InlineData("export")]
    [InlineData("cli")]
    [InlineData("auto")]
    [InlineData("pre-import")]
    [InlineData("pre-restore")]
    public void EveryBundlePath_RemovesLegacyPasswordsWithoutChangingPendingMigrationSources(string action)
    {
        using var tree = BackupTestTree.CreatePopulated();
        string settings = $$$"""{"FontSize":17,"Profiles":[{"Id":"31474923-8673-4aac-a70d-4e814a79a2a1","Name":"legacy","Type":1,"SshHost":"example.invalid","Password":"{{{Secret}}}","ThemeName":"KeepMe"}]}""";
        tree.WriteFile("settings.json", settings);
        tree.WriteFile("settings.json.bak", settings);
        TerminalSettings loaded = TerminalSettings.LoadFromPath(Path.Combine(tree.Root, "settings.json"), new VaultService(new UnavailableSecretStore()));
        Assert.True(loaded.CredentialMigrationPending);
        var service = new BackupService(tree.Root);
        string bundle = Path.Combine(tree.Root, "export.ntildebackup");
        if (action == "export") Assert.True(service.Export(bundle).Success);
        else if (action == "cli")
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(0, BackupCommand.Execute(["backup", "export", bundle], output, error, tree.Root));
        }
        else
        {
            SnapshotReason reason = action switch { "auto" => SnapshotReason.Auto, "pre-import" => SnapshotReason.PreImport, _ => SnapshotReason.PreRestore };
            SnapshotInfo? snapshot = service.Snapshot(reason);
            Assert.NotNull(snapshot);
            bundle = snapshot.FilePath;
        }

        string archived = ReadSettings(bundle);
        Assert.DoesNotContain(Secret, archived, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(archived);
        Assert.Equal(17, document.RootElement.GetProperty("FontSize").GetInt32());
        Assert.Equal("KeepMe", document.RootElement.GetProperty("Profiles")[0].GetProperty("ThemeName").GetString());
        Assert.Equal(settings, tree.ReadFile("settings.json"));
        Assert.Equal(settings, tree.ReadFile("settings.json.bak"));
    }

    [Fact]
    public void DuplicateEscapedAndCaseVariantCredentialKeys_AreAllRemoved()
    {
        using var tree = BackupTestTree.CreatePopulated();
        string settings = $$$"""{"Profiles":[{"Name":"one","Password":"{{{Secret}}}","Password":"{{{Secret}}}","P\u0061ssword":{"secret":"{{{Secret}}}"}}],"profiles":[{"NAME":"two","PASSWORD":"{{{Secret}}}"}],"Other":{"Keep":true}}""";
        tree.WriteFile("settings.json", settings);
        string bundle = Path.Combine(tree.Root, "export.ntildebackup");
        Assert.True(new BackupService(tree.Root).Export(bundle).Success);
        string archived = ReadSettings(bundle);
        Assert.DoesNotContain(Secret, archived, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(archived);
        Assert.True(document.RootElement.GetProperty("Other").GetProperty("Keep").GetBoolean());
        Assert.Equal(settings, tree.ReadFile("settings.json"));
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"Profiles\":\"SYNTHETIC-718-LEGACY-PASSWORD\"}")]
    [InlineData("{\"Profiles\":[\"SYNTHETIC-718-LEGACY-PASSWORD\"]}")]
    public void UnsafeSettings_FailClosedAndPreserveExistingBundle(string settings)
    {
        using var tree = BackupTestTree.CreatePopulated();
        tree.WriteFile("settings.json", settings);
        string bundle = Path.Combine(tree.Root, "existing.ntildebackup");
        File.WriteAllText(bundle, "previous bundle");
        BackupOutcome outcome = new BackupService(tree.Root).Export(bundle);
        Assert.False(outcome.Success);
        Assert.DoesNotContain(Secret, outcome.Message, StringComparison.Ordinal);
        Assert.Equal("previous bundle", File.ReadAllText(bundle));
        Assert.Empty(Directory.GetFiles(tree.Root, "*.tmp"));
        Assert.Equal(settings, tree.ReadFile("settings.json"));
    }

    [Fact]
    public void AutoDedupe_DoesNotFingerprintPasswordOnlyChanges()
    {
        using var tree = BackupTestTree.CreatePopulated();
        tree.WriteFile("settings.json", """{"FontSize":17,"Profiles":[{"Name":"legacy","Password":"first"}]}""");
        var service = new BackupService(tree.Root);
        SnapshotInfo? first = service.Snapshot(SnapshotReason.Auto);
        Assert.NotNull(first);
        tree.WriteFile("settings.json", """{"FontSize":17,"Profiles":[{"Name":"legacy","Password":"second"}]}""");
        Assert.Null(service.Snapshot(SnapshotReason.Auto));
        Assert.Single(service.ListSnapshots());
        tree.WriteFile("settings.json", """{"FontSize":18,"Profiles":[{"Name":"legacy","Password":"second"}]}""");
        SnapshotInfo? changed = service.Snapshot(SnapshotReason.Auto);
        Assert.NotNull(changed);
        Assert.NotEqual(first.ContentHash, changed.ContentHash);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Utf16Settings_AreSanitizedAndSafeBytesOtherwiseStayUnchanged(bool legacyPassword)
    {
        using var tree = BackupTestTree.CreatePopulated();
        string text = legacyPassword ? """{"Profiles":[{"Name":"ñ","Password":"secret"}]}""" : """{ "FontSize" : 17, "Profiles" : [{"Name":"ñ"}] }""";
        string path = Path.Combine(tree.Root, "settings.json");
        File.WriteAllText(path, text, System.Text.Encoding.Unicode);
        byte[] original = File.ReadAllBytes(path);
        string bundle = Path.Combine(tree.Root, "export.ntildebackup");
        Assert.True(new BackupService(tree.Root).Export(bundle).Success);
        Assert.DoesNotContain("secret", ReadSettings(bundle), StringComparison.Ordinal);
        if (!legacyPassword)
        {
            using var zip = ZipFile.OpenRead(bundle);
            using var stream = zip.GetEntry("settings/settings.json")!.Open();
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            Assert.Equal(original, bytes.ToArray());
        }
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    private static string ReadSettings(string bundle)
    {
        using var zip = ZipFile.OpenRead(bundle);
        using var reader = new StreamReader(zip.GetEntry("settings/settings.json")!.Open());
        return reader.ReadToEnd();
    }

    private sealed class UnavailableSecretStore : ISecretStore
    {
        public bool IsAvailable => false;
        public string? Read(string key) => throw new InvalidOperationException("No secret-store access during export.");
        public void Write(string key, string value) => throw new InvalidOperationException("No secret-store access during export.");
        public bool Delete(string key) => throw new InvalidOperationException("No secret-store access during export.");
    }
}
