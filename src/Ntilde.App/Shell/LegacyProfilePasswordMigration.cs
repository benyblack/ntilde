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
        VaultService? vault = null;
        foreach (string candidate in new[] { path, path + ".bak" })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                JsonNode? document = JsonNode.Parse(File.ReadAllText(candidate));
                if (document is not JsonObject root || root["Profiles"] is not JsonArray profiles) continue;
                bool changed = false;
                foreach (JsonObject profile in profiles.OfType<JsonObject>())
                {
                    if (!profile.TryGetPropertyValue("Password", out JsonNode? password)) continue;
                    if (password is not null)
                    {
                        using var secret = SecretBuffer.FromText(password.GetValue<string>());
                        if (secret.Length > 0)
                        {
                            if (!Guid.TryParse(profile["Id"]?.GetValue<string>(), out Guid id) || id == Guid.Empty) return false;
                            vault ??= vaultFactory();
                            if (!vault.PersistenceAvailable) return false;
                            var target = new TerminalProfile { Id = id };
                            using var existing = vault.GetSshPasswordBufferForProfile(target);
                            if (existing is null || existing.Length == 0)
                            {
                                vault.SetSshPasswordBufferForProfile(target, secret);
                                using var verified = vault.GetSshPasswordBufferForProfile(target);
                                if (verified is null || !verified.ContentEquals(secret)) return false;
                            }
                        }
                    }
                    profile.Remove("Password");
                    changed = true;
                }
                if (changed)
                {
                    // A normal settings save would back up the plaintext we just migrated.
                    AtomicFile.WriteAllTextWithoutBackup(candidate, document.ToJsonString());
                }
            }
            catch (JsonException) { /* The settings recovery path owns malformed files. */ }
            catch (Exception)
            {
                // Never log credential-bearing JSON or native error text during migration.
                return false;
            }
        }
        return true;
    }
}
