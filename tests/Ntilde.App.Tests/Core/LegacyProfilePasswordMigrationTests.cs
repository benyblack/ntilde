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
        Assert.True(TerminalSettings.LoadFromPath(SettingsPath, vault).CredentialMigrationPending);
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

    private sealed class DiscardingStore(bool available) : ISecretStore
    {
        public bool IsAvailable => available;
        public string? Read(string key) => null;
        public void Write(string key, string value) { }
        public bool Delete(string key) => false;
    }
}
