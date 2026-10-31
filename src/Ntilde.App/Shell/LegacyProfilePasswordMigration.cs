using System.Text.Json;
using System.Text.Json.Nodes;
using Ntilde.Platform.Security;

namespace Ntilde.Shell;

/// <summary>
/// Imports pre-keyring profile passwords, then removes them from settings and its recovery copy.
/// Existing keyring credentials take precedence. A failed/locked keyring leaves the file intact.
/// </summary>
internal static class LegacyProfilePasswordMigration
{
    internal static bool TryMigrate(string path, Func<VaultService> vaultFactory)
    {
        var vault = new Lazy<VaultService>(vaultFactory);
        return TryMigrateFile(path, vault) && TryMigrateFile(path + ".bak", vault);
    }

    private static bool TryMigrateFile(string path, Lazy<VaultService> vault)
    {
        if (!File.Exists(path)) return true;
        try
        {
            JsonNode? document = JsonNode.Parse(File.ReadAllText(path));
            if (document is not JsonObject root || root["Profiles"] is not JsonArray profiles) return true;
            bool changed = false;
            foreach (JsonObject profile in profiles.OfType<JsonObject>())
            {
                if (!profile.TryGetPropertyValue("Password", out JsonNode? password)) continue;
                if (!TryStorePassword(profile, password, vault)) return false;
                profile.Remove("Password");
                changed = true;
            }
            // A normal settings save would back up the plaintext we just migrated.
            if (changed) AtomicFile.WriteAllTextWithoutBackup(path, document.ToJsonString());
            return true;
        }
        catch (JsonException) { return true; } // The settings recovery path owns malformed files.
        catch (Exception)
        {
            // Never log credential-bearing JSON or native error text during migration.
            return false;
        }
    }

    private static bool TryStorePassword(JsonObject profile, JsonNode? password, Lazy<VaultService> vault)
    {
        if (password is null) return true;
        using var secret = SecretBuffer.FromText(password.GetValue<string>());
        if (secret.Length == 0) return true;
        // Missing/invalid ids have no usable references; establish one stable id in the
        // sanitized file, just as TerminalProfile's default initializer does on load.
        string? storedId = profile["Id"] is JsonValue idValue && idValue.TryGetValue<string>(out var text) ? text : null;
        if (!Guid.TryParse(storedId, out Guid id) || id == Guid.Empty) id = Guid.NewGuid();
        profile["Id"] = id.ToString("D");
        if (!vault.Value.PersistenceAvailable) return false;
        var target = new TerminalProfile
        {
            Id = id,
            Name = profile["Name"]?.GetValue<string>() ?? string.Empty,
            SshUser = profile["SshUser"]?.GetValue<string>() ?? string.Empty,
            SshHost = profile["SshHost"]?.GetValue<string>() ?? string.Empty,
            Type = ConnectionType.SSH
        };
        using var existing = vault.Value.GetSshPasswordBufferForProfile(target, strictRead: true);
        if (existing is { Length: > 0 }) return true;
        vault.Value.SetSshPasswordBufferForProfile(target, secret);
        using var verified = vault.Value.GetSshPasswordBufferForProfile(target, strictRead: true);
        return verified is not null && verified.ContentEquals(secret);
    }
}
