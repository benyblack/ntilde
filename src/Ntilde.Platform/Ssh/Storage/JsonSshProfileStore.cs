using System.Text.Json;
using Ntilde.Platform.Ssh.Models;
using Ntilde.VT;

namespace Ntilde.Platform.Ssh.Storage;

public sealed class JsonSshProfileStore : ISshProfileStore
{
    private const int CurrentSchemaVersion = 1;

    private readonly object _syncRoot = new();
    private readonly string _storeFilePath;
    private readonly Dictionary<int, ISshProfileStoreMigration> _migrations;
    private readonly Func<string, string> _readAllText;
    private readonly Action<string, string> _moveCorruptStore;

    public JsonSshProfileStore(string? storeFilePath = null, IEnumerable<ISshProfileStoreMigration>? migrations = null)
        : this(storeFilePath, migrations, File.ReadAllText, File.Move)
    {
    }

    internal JsonSshProfileStore(
        string? storeFilePath,
        IEnumerable<ISshProfileStoreMigration>? migrations,
        Func<string, string> readAllText,
        Action<string, string> moveCorruptStore)
    {
        _readAllText = readAllText ?? throw new ArgumentNullException(nameof(readAllText));
        _moveCorruptStore = moveCorruptStore ?? throw new ArgumentNullException(nameof(moveCorruptStore));
        _storeFilePath = string.IsNullOrWhiteSpace(storeFilePath)
            ? GetDefaultStorePath()
            : Path.GetFullPath(storeFilePath);

        _migrations = (migrations ?? Array.Empty<ISshProfileStoreMigration>())
            .GroupBy(m => m.SourceSchemaVersion)
            .ToDictionary(group => group.Key, group => group.First());
    }

    public string StoreFilePath => _storeFilePath;

    /// <remarks>
    /// Resolved through <see cref="PlatformAppPaths"/> so that redirecting NTILDE_APPDATA_ROOT
    /// moves SSH profiles with everything else. Reading LocalApplicationData directly here is what
    /// let a redirected process write the machine's real profile file (#406).
    /// </remarks>
    public static string GetDefaultStorePath()
        => Path.Combine(PlatformAppPaths.SshDirectory, "profiles.json");

    public IReadOnlyList<SshProfile> GetProfiles()
    {
        lock (_syncRoot)
        {
            var document = LoadDocumentLocked(out _);
            return document.Profiles
                .OrderBy(profile => profile.Id)
                .Select(CloneProfile)
                .ToArray();
        }
    }

    public SshProfile? GetProfile(Guid profileId)
    {
        lock (_syncRoot)
        {
            var document = LoadDocumentLocked(out _);
            var profile = document.Profiles.FirstOrDefault(existing => existing.Id == profileId);
            return profile is null ? null : CloneProfile(profile);
        }
    }

    /// <summary>Saves a profile after reading the existing store or successfully quarantining corruption.</summary>
    /// <exception cref="IOException">The existing store could not be read or quarantined; no replacement is written.</exception>
    public void SaveProfile(SshProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        lock (_syncRoot)
        {
            var document = LoadDocumentLocked(out bool mayOverwrite, forMutation: true);
            var normalized = NormalizeProfile(profile);
            int existingIndex = document.Profiles.FindIndex(existing => existing.Id == normalized.Id);

            if (existingIndex >= 0)
            {
                document.Profiles[existingIndex] = normalized;
            }
            else
            {
                document.Profiles.Add(normalized);
            }

            PersistDocumentLocked(document, mayOverwrite);
        }
    }

    /// <summary>Deletes an existing profile, returning false only when the loaded store has no matching profile.</summary>
    /// <exception cref="IOException">The existing store could not be read or quarantined; no replacement is written.</exception>
    public bool DeleteProfile(Guid profileId)
    {
        lock (_syncRoot)
        {
            var document = LoadDocumentLocked(out bool mayOverwrite, forMutation: true);
            bool removed = document.Profiles.RemoveAll(profile => profile.Id == profileId) > 0;

            if (removed)
            {
                PersistDocumentLocked(document, mayOverwrite);
            }

            return removed;
        }
    }

    private SshStoreDocument LoadDocumentLocked(out bool mayOverwrite, bool forMutation = false)
    {
        // An absent or successfully quarantined store authorizes creation only. If a file
        // appears before persistence, it must be read before it may be replaced.
        mayOverwrite = false;
        string json;
        try
        {
            // File.Exists also returns false for some access failures. Only a failed open
            // that explicitly reports absence authorizes creating a fresh store.
            json = _readAllText(_storeFilePath);
        }
        catch (FileNotFoundException) { return CreateNewDocument(); }
        catch (DirectoryNotFoundException) { return CreateNewDocument(); }
        catch (Exception ex) { return HandleLoadFailure(ex, forMutation); }

        try
        {
            var document = JsonSerializer.Deserialize(json, SshJsonContext.Default.SshStoreDocument)
                ?? throw new JsonException("SSH profile store must contain a JSON object.");
            document.Profiles ??= new List<SshProfile>();

            if (document.SchemaVersion <= 0)
            {
                document.SchemaVersion = CurrentSchemaVersion;
            }
            else if (document.SchemaVersion < CurrentSchemaVersion)
            {
                document = ApplyMigrationsLocked(document);
            }

            SortProfiles(document.Profiles);
            mayOverwrite = true;
            return document;
        }
        catch (JsonException ex)
        {
            try
            {
                QuarantineCorruptStoreLocked();
            }
            catch (Exception quarantineError)
            {
                return HandleLoadFailure(quarantineError, forMutation);
            }
            TerminalLogger.Log($"[JsonSshProfileStore] Parsed JSON is corrupt. Store quarantined. Error: {ex.Message}");
            return CreateNewDocument();
        }
        catch (Exception ex)
        {
            return HandleLoadFailure(ex, forMutation);
        }
    }

    private SshStoreDocument HandleLoadFailure(Exception error, bool forMutation)
    {
        TerminalLogger.Log($"[JsonSshProfileStore] Failed to load or quarantine store; existing file preserved: {error.Message}");
        if (forMutation)
        {
            throw new IOException(
                $"Cannot update SSH profiles at '{_storeFilePath}': the existing store could not be read or quarantined. " +
                "Its contents have been preserved. Retry after resolving the file access error.", error);
        }

        // Read-only listings retain their tolerant behavior. A mutation always reloads
        // strictly; this empty fallback must never become the basis of a write.
        return CreateNewDocument();
    }

    private void QuarantineCorruptStoreLocked()
    {
        string directory = Path.GetDirectoryName(_storeFilePath) ?? string.Empty;
        string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture); // Gregorian in every locale
        string backupPath = Path.Combine(directory, $"profiles.json.corrupt.{timestamp}.json");
        _moveCorruptStore(_storeFilePath, backupPath);
    }

    private SshStoreDocument ApplyMigrationsLocked(SshStoreDocument document)
    {
        int guard = 0;
        var snapshot = ToSnapshot(document);

        while (snapshot.SchemaVersion < CurrentSchemaVersion && guard < 16)
        {
            if (!_migrations.TryGetValue(snapshot.SchemaVersion, out ISshProfileStoreMigration? migration))
            {
                break;
            }

            snapshot = migration.Migrate(snapshot);
            guard++;
        }

        if (snapshot.SchemaVersion < CurrentSchemaVersion)
        {
            snapshot = new SshProfileStoreSnapshot
            {
                SchemaVersion = CurrentSchemaVersion,
                Profiles = snapshot.Profiles.Select(CloneProfile).ToList()
            };
        }

        return FromSnapshot(snapshot);
    }

    private void PersistDocumentLocked(SshStoreDocument document, bool mayOverwrite)
    {
        document.SchemaVersion = CurrentSchemaVersion;
        SortProfiles(document.Profiles);

        string? directory = Path.GetDirectoryName(_storeFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonSerializer.Serialize(document, SshJsonContext.Default.SshStoreDocument);

        string tempPath = Path.Combine(directory ?? string.Empty, $"profiles.json.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tempPath, json);

        try
        {
            File.Move(tempPath, _storeFilePath, overwrite: mayOverwrite);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // Best effort cleanup
                }
            }
        }
    }

    private static void SortProfiles(List<SshProfile> profiles)
    {
        profiles.Sort((left, right) => left.Id.CompareTo(right.Id));
    }

    private static SshStoreDocument CreateNewDocument()
    {
        return new SshStoreDocument
        {
            SchemaVersion = CurrentSchemaVersion,
            Profiles = new List<SshProfile>()
        };
    }

    private static SshProfile NormalizeProfile(SshProfile profile)
    {
        var normalized = CloneProfile(profile);
        normalized.BackendKind = Enum.IsDefined(normalized.BackendKind)
            ? normalized.BackendKind
            : SshBackendKind.OpenSsh;
        normalized.Name = normalized.Name?.Trim() ?? string.Empty;
        normalized.GroupPath = normalized.GroupPath?.Trim() ?? string.Empty;
        normalized.Notes = normalized.Notes?.Trim() ?? string.Empty;
        normalized.AccentColor = normalized.AccentColor?.Trim() ?? string.Empty;
        normalized.Tags = (normalized.Tags ?? new List<string>())
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToList();
        normalized.Host = normalized.Host?.Trim() ?? string.Empty;
        normalized.User = normalized.User?.Trim() ?? string.Empty;
        normalized.Port = normalized.Port > 0 ? normalized.Port : 22;
        normalized.IdentityFilePath = normalized.IdentityFilePath?.Trim() ?? string.Empty;
        SshProfileNormalizer.NormalizeRememberPasswordPreference(normalized);
        normalized.WorkingDirectory = normalized.WorkingDirectory?.Trim() ?? string.Empty;
        normalized.RemoteShellKind = Enum.IsDefined(normalized.RemoteShellKind)
            ? normalized.RemoteShellKind
            : RemoteShellKind.Auto;
        normalized.ServerAliveIntervalSeconds = normalized.ServerAliveIntervalSeconds > 0
            ? normalized.ServerAliveIntervalSeconds
            : 30;
        normalized.ServerAliveCountMax = normalized.ServerAliveCountMax > 0
            ? normalized.ServerAliveCountMax
            : 3;
        normalized.ExtraSshArgs = normalized.ExtraSshArgs?.Trim() ?? string.Empty;
        normalized.JumpHops = normalized.JumpHops.Select(NormalizeJumpHop).ToList();
        normalized.Forwards = normalized.Forwards.Select(NormalizeForward).ToList();
        normalized.MuxOptions = NormalizeMuxOptions(normalized.MuxOptions);

        if (string.IsNullOrWhiteSpace(normalized.Host))
        {
            throw new ArgumentException("SSH profile host cannot be empty.", nameof(profile));
        }

        return normalized;
    }

    private static SshJumpHop NormalizeJumpHop(SshJumpHop hop)
    {
        return new SshJumpHop
        {
            Host = hop.Host?.Trim() ?? string.Empty,
            User = hop.User?.Trim() ?? string.Empty,
            Port = hop.Port > 0 ? hop.Port : 22
        };
    }

    private static PortForward NormalizeForward(PortForward forward)
    {
        return new PortForward
        {
            Kind = forward.Kind,
            BindAddress = forward.BindAddress?.Trim() ?? string.Empty,
            SourcePort = forward.SourcePort,
            DestinationHost = forward.DestinationHost?.Trim() ?? string.Empty,
            DestinationPort = forward.DestinationPort
        };
    }

    private static SshMuxOptions NormalizeMuxOptions(SshMuxOptions? mux)
    {
        mux ??= new SshMuxOptions();
        return new SshMuxOptions
        {
            Enabled = mux.Enabled,
            ControlMasterAuto = mux.ControlMasterAuto,
            ControlPath = mux.ControlPath?.Trim() ?? string.Empty,
            ControlPersistSeconds = mux.ControlPersistSeconds,
            PersistRemoteSessions = mux.PersistRemoteSessions,
            RemoteDaemonPath = mux.RemoteDaemonPath?.Trim() ?? string.Empty,
            RemoteDaemonVersion = mux.RemoteDaemonVersion?.Trim() ?? string.Empty,
            RemoteDaemonRid = mux.RemoteDaemonRid?.Trim() ?? string.Empty
        };
    }

    private static SshProfile CloneProfile(SshProfile profile)
    {
        return new SshProfile
        {
            Id = profile.Id,
            BackendKind = profile.BackendKind,
            Name = profile.Name,
            GroupPath = profile.GroupPath,
            Notes = profile.Notes,
            AccentColor = profile.AccentColor,
            Tags = (profile.Tags ?? new List<string>()).ToList(),
            Host = profile.Host,
            User = profile.User,
            Port = profile.Port,
            AuthMode = profile.AuthMode,
            IdentityFilePath = profile.IdentityFilePath,
            RememberPasswordInVault = profile.RememberPasswordInVault,
            JumpHops = profile.JumpHops.Select(CloneJumpHop).ToList(),
            Forwards = profile.Forwards.Select(CloneForward).ToList(),
            MuxOptions = CloneMuxOptions(profile.MuxOptions),
            ServerAliveIntervalSeconds = profile.ServerAliveIntervalSeconds,
            ServerAliveCountMax = profile.ServerAliveCountMax,
            ExtraSshArgs = profile.ExtraSshArgs,
            WorkingDirectory = profile.WorkingDirectory,
            RemoteShellKind = profile.RemoteShellKind,
            AllowAgentAccess = profile.AllowAgentAccess
        };
    }

    private static SshJumpHop CloneJumpHop(SshJumpHop hop)
    {
        return new SshJumpHop
        {
            Host = hop.Host,
            User = hop.User,
            Port = hop.Port
        };
    }

    private static PortForward CloneForward(PortForward forward)
    {
        return new PortForward
        {
            Kind = forward.Kind,
            BindAddress = forward.BindAddress,
            SourcePort = forward.SourcePort,
            DestinationHost = forward.DestinationHost,
            DestinationPort = forward.DestinationPort
        };
    }

    private static SshMuxOptions CloneMuxOptions(SshMuxOptions? mux)
    {
        mux ??= new SshMuxOptions();
        return new SshMuxOptions
        {
            Enabled = mux.Enabled,
            ControlMasterAuto = mux.ControlMasterAuto,
            ControlPath = mux.ControlPath,
            ControlPersistSeconds = mux.ControlPersistSeconds,
            PersistRemoteSessions = mux.PersistRemoteSessions,
            RemoteDaemonPath = mux.RemoteDaemonPath,
            RemoteDaemonVersion = mux.RemoteDaemonVersion,
            RemoteDaemonRid = mux.RemoteDaemonRid
        };
    }

    private static SshProfileStoreSnapshot ToSnapshot(SshStoreDocument document)
    {
        return new SshProfileStoreSnapshot
        {
            SchemaVersion = document.SchemaVersion,
            Profiles = document.Profiles.Select(CloneProfile).ToList()
        };
    }

    private static SshStoreDocument FromSnapshot(SshProfileStoreSnapshot snapshot)
    {
        return new SshStoreDocument
        {
            SchemaVersion = snapshot.SchemaVersion,
            Profiles = snapshot.Profiles.Select(CloneProfile).ToList()
        };
    }
}

internal sealed class SshStoreDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<SshProfile> Profiles { get; set; } = new();
}
