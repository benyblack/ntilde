using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
        try
        {
            MigrationFile[] files = [ReadFile(path), ReadFile(path + ".bak")];
            var context = new MigrationContext(files.SelectMany(file => file.Profiles), vaultFactory);
            foreach (MigrationFile file in files)
            {
                if (!MigratePasswords(file, context)) return false;
            }
            foreach (MigrationFile file in files) ApplyRepairedIds(file, context);
            foreach (MigrationFile file in files)
            {
                // A normal settings save would back up the plaintext we just migrated.
                if (file.Changed && file.Document is { } document)
                    AtomicFile.WriteAllTextWithoutBackup(file.Path, document.ToJsonString());
            }
            return true;
        }
        catch (Exception)
        {
            // Never log credential-bearing JSON or native error text during migration.
            return false;
        }
    }

    private static MigrationFile ReadFile(string path)
    {
        JsonObject? document = null;
        try
        {
            if (File.Exists(path)) document = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (JsonException) { /* The settings recovery path owns malformed files. */ }
        return new MigrationFile(path, document, ReadProfiles(document));
    }

    private static List<ProfileEntry> ReadProfiles(JsonObject? document)
    {
        if (document?["Profiles"] is not JsonArray profiles) return [];
        var occurrences = new Dictionary<ProfileIdentity, int>();
        var result = new List<ProfileEntry>();
        foreach (JsonObject profile in profiles.OfType<JsonObject>())
        {
            var identity = new ProfileIdentity(
                IdentityText(profile, "Name"), IdentityText(profile, "SshHost"), IdentityText(profile, "SshUser"),
                profile["SshPort"] is JsonValue portValue && portValue.TryGetValue<int>(out int port) ? port : 22, IdentityText(profile, "Command"), 0);
            occurrences.TryGetValue(identity, out int occurrence);
            occurrences[identity] = occurrence + 1;
            result.Add(new ProfileEntry(profile, identity with { Occurrence = occurrence }));
        }
        return result;
    }

    private static bool MigratePasswords(MigrationFile file, MigrationContext context)
    {
        foreach (ProfileEntry entry in file.Profiles)
        {
            if (!entry.Profile.TryGetPropertyValue("Password", out JsonNode? password)) continue;
            if (!TryStorePassword(entry, password, context)) return false;
            entry.Profile.Remove("Password");
            file.Changed = true;
        }
        return true;
    }

    private static void ApplyRepairedIds(MigrationFile file, MigrationContext context)
    {
        foreach (ProfileEntry entry in file.Profiles)
        {
            if (!context.NeedsIdentity.Contains(entry.Identity) || TryReadId(entry.Profile, out _)) continue;
            entry.Profile["Id"] = context.Ids[entry.Identity].ToString("D");
            file.Changed = true;
        }
    }

    private static bool TryStorePassword(ProfileEntry entry, JsonNode? password, MigrationContext context)
    {
        if (password is null) return true;
        using var secret = SecretBuffer.FromText(password.GetValue<string>());
        if (secret.Length == 0) return true;
        Guid id = context.IdFor(entry);
        VaultService vault = context.Vault.Value;
        if (!vault.PersistenceAvailable) return false;
        var target = new TerminalProfile
        {
            Id = id,
            Name = Text(entry.Profile, "Name"),
            SshUser = Text(entry.Profile, "SshUser"),
            SshHost = Text(entry.Profile, "SshHost"),
            Type = ConnectionType.SSH
        };
        using var existing = vault.GetSshPasswordBufferForProfile(target, strictRead: true);
        if (existing is { Length: > 0 }) return true;
        vault.SetSshPasswordBufferForProfile(target, secret);
        using var verified = vault.GetSshPasswordBufferForProfile(target, strictRead: true);
        return verified is not null && verified.ContentEquals(secret);
    }

    // Identity discovery also sees non-secret entries in an otherwise malformed primary. Those
    // must remain the settings recovery path's responsibility rather than blocking backup migration.
    private static string IdentityText(JsonObject profile, string field) =>
        profile[field] is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;

    private static string Text(JsonObject profile, string field) => profile[field]?.GetValue<string>() ?? string.Empty;

    private static bool TryReadId(JsonObject profile, out Guid id)
    {
        string? text = profile["Id"] is JsonValue value && value.TryGetValue<string>(out var candidate) ? candidate : null;
        return Guid.TryParse(text, out id) && id != Guid.Empty;
    }

    private sealed class MigrationContext
    {
        public Dictionary<ProfileIdentity, Guid> Ids { get; } = new();
        public HashSet<ProfileIdentity> NeedsIdentity { get; } = new();
        public Lazy<VaultService> Vault { get; }

        public MigrationContext(IEnumerable<ProfileEntry> profiles, Func<VaultService> vaultFactory)
        {
            Vault = new Lazy<VaultService>(vaultFactory);
            // A valid identity in either copy wins before any missing identity is generated.
            foreach (ProfileEntry entry in profiles)
                if (TryReadId(entry.Profile, out Guid id)) Ids.TryAdd(entry.Identity, id);
        }

        public Guid IdFor(ProfileEntry entry)
        {
            NeedsIdentity.Add(entry.Identity);
            if (TryReadId(entry.Profile, out Guid id)) return id;
            if (Ids.TryGetValue(entry.Identity, out id)) return id;
            // Derive a retry-stable id from non-secret metadata. If the keyring write succeeds but
            // verification/file replacement fails, retrying cannot leave an unreachable credential.
            ProfileIdentity key = entry.Identity;
            string input = string.Create(CultureInfo.InvariantCulture,
                $"Ntilde.LegacySshProfile/v1|{key.Name.Length}:{key.Name}|{key.Host.Length}:{key.Host}|{key.User.Length}:{key.User}|{key.Port}|{key.Command.Length}:{key.Command}|{key.Occurrence}");
            id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(input)).AsSpan(0, 16));
            Ids[entry.Identity] = id;
            return id;
        }
    }

    private sealed record MigrationFile(string Path, JsonObject? Document, List<ProfileEntry> Profiles)
    {
        public bool Changed { get; set; }
    }
    private sealed record ProfileEntry(JsonObject Profile, ProfileIdentity Identity);
    private readonly record struct ProfileIdentity(string Name, string Host, string User, int Port, string Command, int Occurrence);
}
