using System.Text.Json;
using Ntilde.Shell;
using Ntilde.Shell.Secrets;

namespace Ntilde.Tests.Core;

public sealed class LegacyProfilePasswordMigrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ntilde-121-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _profileId = Guid.NewGuid();
    private string SettingsPath => Path.Combine(_directory, "settings.json");

    public LegacyProfilePasswordMigrationTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private void WriteLegacy(string password)
    {
        string json = JsonSerializer.Serialize(new { Profiles = new[] { new { Id = _profileId, Name = "old", SshUser = "ops", SshHost = "prod.internal", Password = password } }, FontSize = 17 });
        File.WriteAllText(SettingsPath, json);
        File.WriteAllText(SettingsPath + ".bak", json);
    }

    [Fact]
    public void Verified_migration_removes_plaintext_from_primary_and_recovery_copy()
    {
        WriteLegacy("café-🔑");
        var vault = new VaultService(new InMemorySecretStore());
        Assert.True(LegacyProfilePasswordMigration.TryMigrate(SettingsPath, () => vault));
        using var saved = vault.GetSshPasswordBufferForProfile(new TerminalProfile { Id = _profileId });
        Assert.Equal("café-🔑", saved?.DangerousGetString());
        foreach (string path in new[] { SettingsPath, SettingsPath + ".bak" })
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.False(document.RootElement.GetProperty("Profiles")[0].TryGetProperty("Password", out _));
            Assert.Equal(17, document.RootElement.GetProperty("FontSize").GetInt32());
        }
        Assert.True(LegacyProfilePasswordMigration.TryMigrate(SettingsPath, () => throw new InvalidOperationException("No keyring access on a clean file")));
        Assert.Null(typeof(TerminalProfile).GetProperty("Password"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unavailable_or_silently_failed_store_preserves_original_files(bool available)
    {
        WriteLegacy("keep-me");
        string original = File.ReadAllText(SettingsPath);
        var vault = new VaultService(new DiscardingStore(available));
        Assert.False(LegacyProfilePasswordMigration.TryMigrate(SettingsPath, () => vault));
        Assert.Equal(original, File.ReadAllText(SettingsPath));
        Assert.Equal(original, File.ReadAllText(SettingsPath + ".bak"));
        TerminalSettings settings = TerminalSettings.LoadFromPath(SettingsPath, vault);
        Assert.True(settings.CredentialMigrationPending);
        settings.FontSize = 25;
        Assert.False(settings.TrySaveToPath(SettingsPath, () => vault, out string error));
        Assert.NotEmpty(error);
        Assert.Equal(original, File.ReadAllText(SettingsPath));
        Assert.Equal(original, File.ReadAllText(SettingsPath + ".bak"));
    }

    [Fact]
    public void Successful_recovery_keeps_the_migrated_backup_usable()
    {
        WriteLegacy("from-backup");
        File.WriteAllText(SettingsPath, "{broken");
        var vault = new VaultService(new InMemorySecretStore());
        TerminalSettings settings = TerminalSettings.LoadFromPath(SettingsPath, vault);
        Assert.False(settings.CredentialMigrationPending);
        foreach (string path in new[] { SettingsPath, SettingsPath + ".bak" })
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.False(document.RootElement.GetProperty("Profiles")[0].TryGetProperty("Password", out _));
        }
        Assert.Equal("from-backup", vault.GetSecret(VaultService.GetCanonicalSshProfileKey(_profileId)));
    }

    [Fact]
    public void Recovery_does_not_overwrite_the_only_credential_when_the_store_is_locked()
    {
        WriteLegacy("only-in-the-backup");
        string backup = File.ReadAllText(SettingsPath + ".bak");
        File.WriteAllText(SettingsPath, "{broken");
        var vault = new VaultService(new DiscardingStore(available: false));
        TerminalSettings settings = TerminalSettings.LoadFromPath(SettingsPath, vault);
        Assert.True(settings.CredentialMigrationPending);
        Assert.Equal(backup, File.ReadAllText(SettingsPath + ".bak"));
        Assert.Equal("{broken", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Newer_keyring_credential_takes_precedence_over_legacy_and_backup()
    {
        WriteLegacy("old");
        var vault = new VaultService(new InMemorySecretStore());
        vault.SetSecret(VaultService.GetCanonicalSshProfileKey(_profileId), "new");
        Assert.True(LegacyProfilePasswordMigration.TryMigrate(SettingsPath, () => vault));
        Assert.Equal("new", vault.GetSecret(VaultService.GetCanonicalSshProfileKey(_profileId)));
        foreach (string path in new[] { SettingsPath, SettingsPath + ".bak" })
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.False(document.RootElement.GetProperty("Profiles")[0].TryGetProperty("Password", out _));
        }
    }

    [Theory]
    [InlineData("SSH:old:ops@prod.internal")]
    [InlineData("SSH:ops@prod.internal")]
    public void Migration_preserves_a_newer_name_keyed_credential(string key)
    {
        WriteLegacy("stale-plaintext");
        var vault = new VaultService(new InMemorySecretStore());
        vault.SetSecret(key, "newer-keyring-value");
        Assert.True(LegacyProfilePasswordMigration.TryMigrate(SettingsPath, () => vault));
        Assert.Equal("newer-keyring-value", vault.GetSecret(VaultService.GetCanonicalSshProfileKey(_profileId)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("invalid-id")]
    public void Migration_establishes_a_stable_id_for_legacy_profiles_without_a_valid_one(string? oldId)
    {
        WriteLegacy("saved-password");
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        var profile = root["Profiles"]![0]!.AsObject();
        if (oldId is null) profile.Remove("Id"); else profile["Id"] = oldId;
        File.WriteAllText(SettingsPath, root.ToJsonString());
        var vault = new VaultService(new InMemorySecretStore());
        Assert.True(LegacyProfilePasswordMigration.TryMigrate(SettingsPath, () => vault));
        using var sanitized = JsonDocument.Parse(File.ReadAllText(SettingsPath));
        Guid id = sanitized.RootElement.GetProperty("Profiles")[0].GetProperty("Id").GetGuid();
        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal("saved-password", vault.GetSecret(VaultService.GetCanonicalSshProfileKey(id)));
        Assert.True(LegacyProfilePasswordMigration.TryMigrate(SettingsPath, () => vault));
        using var repeated = JsonDocument.Parse(File.ReadAllText(SettingsPath));
        Assert.Equal(id, repeated.RootElement.GetProperty("Profiles")[0].GetProperty("Id").GetGuid());
    }

    [Fact]
    public void A_keyring_read_error_cannot_be_mistaken_for_a_missing_credential()
    {
        WriteLegacy("stale-plaintext");
        string original = File.ReadAllText(SettingsPath);
        var store = new ReadFailureStore();
        var vault = new VaultService(store);
        Assert.False(LegacyProfilePasswordMigration.TryMigrate(SettingsPath, () => vault));
        Assert.Equal(0, store.Writes);
        Assert.Equal(original, File.ReadAllText(SettingsPath));
        Assert.Null(vault.GetSshPasswordBufferForProfile(new TerminalProfile { Id = _profileId }));
        Assert.False(vault.HasSavedPassword(new TerminalProfile { Id = _profileId }));
    }

    [Fact]
    public void A_save_after_unlock_preserves_the_good_backup_of_a_malformed_primary()
    {
        WriteLegacy("in-backup");
        File.WriteAllText(SettingsPath, "{broken");
        var locked = new VaultService(new DiscardingStore(false));
        TerminalSettings settings = TerminalSettings.LoadFromPath(SettingsPath, locked);
        Assert.True(settings.CredentialMigrationPending);
        var unlocked = new VaultService(new InMemorySecretStore());
        Assert.True(settings.TrySaveToPath(SettingsPath, () => unlocked, out string error), error);
        using var backup = JsonDocument.Parse(File.ReadAllText(SettingsPath + ".bak"));
        Assert.Equal(17, backup.RootElement.GetProperty("FontSize").GetInt32());
        Assert.False(backup.RootElement.GetProperty("Profiles")[0].TryGetProperty("Password", out _));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Primary_and_backup_share_the_same_repaired_profile_identity(bool primaryValid, bool backupValid)
    {
        WriteLegacy("same-profile");
        foreach ((string path, bool valid) in new[] { (SettingsPath, primaryValid), (SettingsPath + ".bak", backupValid) })
        {
            if (valid) continue;
            var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            document["Profiles"]![0]!.AsObject().Remove("Id");
            File.WriteAllText(path, document.ToJsonString());
        }
        var vault = new VaultService(new InMemorySecretStore());
        Assert.True(LegacyProfilePasswordMigration.TryMigrate(SettingsPath, () => vault));
        using var primary = JsonDocument.Parse(File.ReadAllText(SettingsPath));
        using var backup = JsonDocument.Parse(File.ReadAllText(SettingsPath + ".bak"));
        Guid id = primary.RootElement.GetProperty("Profiles")[0].GetProperty("Id").GetGuid();
        Assert.Equal(id, backup.RootElement.GetProperty("Profiles")[0].GetProperty("Id").GetGuid());
        if (primaryValid || backupValid) Assert.Equal(_profileId, id);
    }

    [Fact]
    public void A_primary_without_a_password_keeps_the_identity_used_to_migrate_its_backup()
    {
        WriteLegacy("backup-only");
        foreach (string path in new[] { SettingsPath, SettingsPath + ".bak" })
        {
            var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            var profile = document["Profiles"]![0]!.AsObject();
            profile.Remove("Id");
            if (path == SettingsPath) profile.Remove("Password");
            File.WriteAllText(path, document.ToJsonString());
        }
        var vault = new VaultService(new InMemorySecretStore());
        Assert.True(LegacyProfilePasswordMigration.TryMigrate(SettingsPath, () => vault));
        using var primary = JsonDocument.Parse(File.ReadAllText(SettingsPath));
        Guid id = primary.RootElement.GetProperty("Profiles")[0].GetProperty("Id").GetGuid();
        Assert.Equal("backup-only", vault.GetSecret(VaultService.GetCanonicalSshProfileKey(id)));
    }

    [Fact]
    public void A_retry_after_keyring_verification_failure_reuses_the_same_credential_id()
    {
        WriteLegacy("retry-safe");
        foreach (string path in new[] { SettingsPath, SettingsPath + ".bak" })
        {
            var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            document["Profiles"]![0]!.AsObject().Remove("Id");
            File.WriteAllText(path, document.ToJsonString());
        }
        var store = new VerificationReadFailureStore();
        var vault = new VaultService(store);
        Assert.False(LegacyProfilePasswordMigration.TryMigrate(SettingsPath, () => vault));
        Assert.Single(store.Values);
        store.RefuseVerification = false;
        Assert.True(LegacyProfilePasswordMigration.TryMigrate(SettingsPath, () => vault));
        Assert.Single(store.Values);
        using var documentAfterRetry = JsonDocument.Parse(File.ReadAllText(SettingsPath));
        Guid id = documentAfterRetry.RootElement.GetProperty("Profiles")[0].GetProperty("Id").GetGuid();
        Assert.Equal("retry-safe", store.Values[VaultService.GetCanonicalSshProfileKey(id)]);
    }

    [Fact]
    public void Non_secret_schema_errors_do_not_block_recovery_from_a_backup_with_a_password()
    {
        WriteLegacy("recoverable");
        File.WriteAllText(SettingsPath, """{"Profiles":[{"Name":"old","SshPort":"invalid"}]}""");
        var vault = new VaultService(new InMemorySecretStore());
        TerminalSettings settings = TerminalSettings.LoadFromPath(SettingsPath, vault);
        Assert.False(settings.CredentialMigrationPending);
        Assert.Equal(17, settings.FontSize);
        Assert.Equal("recoverable", vault.GetSecret(VaultService.GetCanonicalSshProfileKey(_profileId)));
    }

    private sealed class VerificationReadFailureStore : ISecretStore
    {
        public Dictionary<string, string> Values { get; } = new();
        public bool RefuseVerification { get; set; } = true;
        public bool IsAvailable => true;
        public string? Read(string key)
        {
            if (RefuseVerification && Values.Count > 0) throw new InvalidOperationException("verification read failed");
            return Values.GetValueOrDefault(key);
        }
        public void Write(string key, string value) => Values[key] = value;
        public bool Delete(string key) => Values.Remove(key);
    }

    private sealed class ReadFailureStore : ISecretStore
    {
        public int Writes { get; private set; }
        public bool IsAvailable => true;
        public string? Read(string key) => throw new InvalidOperationException("keyring locked after availability probe");
        public void Write(string key, string value) => Writes++;
        public bool Delete(string key) => false;
    }

    private sealed class DiscardingStore(bool available) : ISecretStore
    {
        public bool IsAvailable => available;
        public string? Read(string key) => null;
        public void Write(string key, string value) { }
        public bool Delete(string key) => false;
    }
}
