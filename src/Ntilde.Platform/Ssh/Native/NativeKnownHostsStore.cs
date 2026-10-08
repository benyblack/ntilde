using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Ntilde.Platform.Ssh.Storage;

namespace Ntilde.Platform.Ssh.Native;

public enum NativeKnownHostMatch
{
    Unknown = 0,
    Trusted = 1,
    Mismatch = 2
}

public sealed class NativeKnownHostsStore
{
    // One lock object per full store path, process-wide, so every instance on a path serialises
    // (two windows, the mux handler, tests). Known limit: two *processes* (two app instances)
    // writing at once are not coordinated. The tmp+rename write means neither can leave a torn
    // file, but one can lose the other's latest entry. rusty_ssh also reads this file directly.
    private static readonly ConcurrentDictionary<string, object> PathLocks =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, NativeKnownHostsStore> Instances =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private const int MaxCorruptFiles = 5;

    private readonly object _syncRoot;
    private readonly string _storeFilePath;

    public NativeKnownHostsStore(string storeFilePath)
    {
        _storeFilePath = Path.GetFullPath(storeFilePath ?? throw new ArgumentNullException(nameof(storeFilePath)));
        _syncRoot = PathLocks.GetOrAdd(_storeFilePath, static _ => new object());
    }

    /// <summary>One shared instance per full store path.</summary>
    public static NativeKnownHostsStore ForPath(string storeFilePath)
    {
        string full = Path.GetFullPath(storeFilePath ?? throw new ArgumentNullException(nameof(storeFilePath)));
        return Instances.GetOrAdd(full, static p => new NativeKnownHostsStore(p));
    }

    public string StoreFilePath => _storeFilePath;

    public NativeKnownHostMatch CheckHost(string host, int port, string algorithm, string fingerprint)
    {
        lock (_syncRoot)
        {
            KnownHostEntry? existing = LoadEntriesLocked().FirstOrDefault(entry =>
                string.Equals(entry.Host, NormalizeHost(host), StringComparison.OrdinalIgnoreCase) &&
                entry.Port == NormalizePort(port));

            if (existing == null)
            {
                return NativeKnownHostMatch.Unknown;
            }

            bool matches = string.Equals(existing.Algorithm, NormalizeAlgorithm(algorithm), StringComparison.Ordinal) &&
                           string.Equals(existing.Fingerprint, HostKeyFingerprintFormatter.Normalize(fingerprint), StringComparison.Ordinal);

            return matches ? NativeKnownHostMatch.Trusted : NativeKnownHostMatch.Mismatch;
        }
    }

    public void TrustHost(string host, int port, string algorithm, string fingerprint)
    {
        lock (_syncRoot)
        {
            List<KnownHostEntry> entries = LoadEntriesLocked();
            string normalizedHost = NormalizeHost(host);
            int normalizedPort = NormalizePort(port);
            string normalizedAlgorithm = NormalizeAlgorithm(algorithm);
            string normalizedFingerprint = HostKeyFingerprintFormatter.Normalize(fingerprint);

            int existingIndex = entries.FindIndex(entry =>
                string.Equals(entry.Host, normalizedHost, StringComparison.OrdinalIgnoreCase) &&
                entry.Port == normalizedPort);

            var replacement = new KnownHostEntry
            {
                Host = normalizedHost,
                Port = normalizedPort,
                Algorithm = normalizedAlgorithm,
                Fingerprint = normalizedFingerprint,
                TrustedAtUtc = DateTime.UtcNow
            };

            if (existingIndex >= 0)
            {
                entries[existingIndex] = replacement;
            }
            else
            {
                entries.Add(replacement);
            }

            PersistEntriesLocked(entries);
        }
    }

    private List<KnownHostEntry> LoadEntriesLocked()
    {
        if (!File.Exists(_storeFilePath))
        {
            return new List<KnownHostEntry>();
        }

        try
        {
            string json = File.ReadAllText(_storeFilePath);
            return JsonSerializer.Deserialize(json, SshJsonContext.Default.ListKnownHostEntry) ?? new List<KnownHostEntry>();
        }
        catch (JsonException ex)
        {
            // Never let the next TrustHost overwrite an unparseable store: keep it aside.
            KeepCorruptFileAside(ex);
            return new List<KnownHostEntry>();
        }
        catch
        {
            return new List<KnownHostEntry>();
        }
    }

    private void KeepCorruptFileAside(Exception cause)
    {
        try
        {
            string stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
            string target = $"{_storeFilePath}.corrupt-{stamp}";
            for (int n = 1; File.Exists(target); n++)
            {
                target = $"{_storeFilePath}.corrupt-{stamp}-{n}";
            }

            File.Move(_storeFilePath, target);
            Debug.WriteLine($"[KnownHosts] Unparseable store moved to {target}: {cause.Message}");
            PruneCorruptFiles();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[KnownHosts] Could not set aside unparseable store: {ex.Message}");
        }
    }

    private void PruneCorruptFiles()
    {
        try
        {
            string? directory = Path.GetDirectoryName(_storeFilePath);
            if (string.IsNullOrEmpty(directory))
            {
                return;
            }

            var stale = new DirectoryInfo(directory)
                .GetFiles(Path.GetFileName(_storeFilePath) + ".corrupt-*")
                .OrderByDescending(f => f.CreationTimeUtc)
                .ThenByDescending(f => f.Name, StringComparer.Ordinal)
                .Skip(MaxCorruptFiles);
            foreach (FileInfo file in stale)
            {
                try { file.Delete(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void PersistEntriesLocked(List<KnownHostEntry> entries)
    {
        string? directory = Path.GetDirectoryName(_storeFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var ordered = entries
            .OrderBy(entry => entry.Host, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Port)
            .ToList();

        string json = JsonSerializer.Serialize(ordered, SshJsonContext.Default.ListKnownHostEntry);

        // Write beside the store then rename over it, so a reader (this app, or rusty_ssh) never
        // sees a truncated file.
        string tmp = $"{_storeFilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tmp, json);
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(tmp, _storeFilePath, overwrite: true);
                    return;
                }
                catch (Exception ex) when (OperatingSystem.IsWindows()
                                           && attempt < 4
                                           && ex is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(20);
                }
            }
        }
        finally
        {
            try { File.Delete(tmp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
    private static string NormalizeHost(string host) => host?.Trim() ?? string.Empty;

    private static string NormalizeAlgorithm(string algorithm) => algorithm?.Trim() ?? string.Empty;

    private static int NormalizePort(int port) => port > 0 ? port : 22;
}
