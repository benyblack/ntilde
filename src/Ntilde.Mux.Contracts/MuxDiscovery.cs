using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ntilde.Mux.Contracts;

/// <summary>
/// Where the daemon's endpoint and descriptor live (spec §3). Same root rule as
/// <c>AgentHostDiscovery</c>/<c>AppPaths</c>, duplicated deliberately: this assembly is a
/// zero-reference leaf.
/// </summary>
public static class MuxDiscovery
{
    private const string AppName = "ntilde";
    public const string RootOverrideEnvVar = "NTILDE_APPDATA_ROOT";
    public const string DirectoryName = "mux";
    public const string DescriptorFileName = "mux-endpoint.json";
    public const string SocketFileName = "mux.sock";

    public static string GetRootDirectory()
    {
        string? overrideRoot = Environment.GetEnvironmentVariable(RootOverrideEnvVar);
        if (!string.IsNullOrWhiteSpace(overrideRoot)) return Path.GetFullPath(overrideRoot);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
    }

    public static string GetDescriptorPath() => GetDescriptorPath(GetRootDirectory());

    public static string GetDescriptorPath(string root) => Path.Combine(root, DirectoryName, DescriptorFileName);

    public static string GetDefaultEndpoint() => GetDefaultEndpoint(GetRootDirectory());

    public static string GetDefaultEndpoint(string root)
    {
        string suffix = $"{SanitizedUser()}-{RootHash(root)}";
        if (OperatingSystem.IsWindows()) return "ntilde-mux-" + suffix;

        string preferred = Path.Combine(root, DirectoryName, SocketFileName);
        int budget = OperatingSystem.IsMacOS() ? 103 : 107; // sun_path minus the terminating NUL
        if (Encoding.UTF8.GetByteCount(preferred) <= budget) return preferred;

        // Too long for sun_path. $XDG_RUNTIME_DIR first: per-user, 0700, made by the login manager -
        // whereas the temp directory is world-writable, so a name derived from the user and root is
        // predictable there and another user can create it first (the daemon then refuses it for its
        // mode or owner: a denial of service, not a takeover). Only an absolute runtime dir that
        // exists, and only if the result still fits.
        string dirName = "ntilde-mux-" + suffix;
        string? runtimeDir = Environment.GetEnvironmentVariable(RuntimeDirEnvVar);
        if (!string.IsNullOrWhiteSpace(runtimeDir) && Path.IsPathRooted(runtimeDir) && Directory.Exists(runtimeDir))
        {
            string inRuntimeDir = Path.Combine(runtimeDir, dirName, SocketFileName);
            if (Encoding.UTF8.GetByteCount(inRuntimeDir) <= budget) return inRuntimeDir;
        }

        return Path.Combine(Path.GetTempPath(), dirName, SocketFileName);
    }

    private const string RuntimeDirEnvVar = "XDG_RUNTIME_DIR";

    private static string SanitizedUser()
    {
        string sanitized = string.Concat(Environment.UserName.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit));
        return sanitized.Length == 0 ? "user" : sanitized;
    }

    private static string RootHash(string root)
    {
        // Trimmed: "C:\x\" and "C:\x" are one root, and clients derive it back from the descriptor path.
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (OperatingSystem.IsWindows()) full = full.ToUpperInvariant(); // case-insensitive paths
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(full));
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    /// <summary>Temp file in the same directory, then an atomic replace. No .bak: a stale descriptor is harmless.</summary>
    public static void WriteDescriptor(string path, MuxEndpointDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        CreatePrivateDirectory(dir);
        string temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(descriptor, MuxJsonContext.Default.MuxEndpointDescriptor));
            // Windows refuses to replace a file someone has open (MoveFileEx: access denied), even
            // one opened with FILE_SHARE_DELETE; readers hold it for microseconds, so retry briefly.
            RetryWhileBusy(() => File.Move(temp, path, overwrite: true));
        }
        catch
        {
            try { File.Delete(temp); }
            catch (IOException) { /* best effort: a leftover .tmp is harmless, and the original error matters more */ }
            catch (UnauthorizedAccessException) { /* best effort, as above */ }
            throw;
        }
    }

    public static bool TryReadDescriptor(string path, [NotNullWhen(true)] out MuxEndpointDescriptor? descriptor)
    {
        descriptor = null;
        try
        {
            if (!File.Exists(path)) return false;
            // Share Delete (and ReadWrite): on Windows a reader opened without FILE_SHARE_DELETE -
            // File.ReadAllText's FileShare.Read - makes the owner's DeleteFile fail with a sharing
            // violation and its atomic replace (File.Move overwrite) fail with access denied. The
            // delete is best-effort, so kill-server's own polling could leave a descriptor naming a
            // live pid behind and then wait for it to go away until it timed out (PR #489 CI).
            // Unix unlink/rename ignore open handles, so this is a no-op there.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            descriptor = JsonSerializer.Deserialize(reader.ReadToEnd(), MuxJsonContext.Default.MuxEndpointDescriptor);
            return descriptor is { Endpoint.Length: > 0 };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            descriptor = null;
            return false;
        }
    }

    /// <summary>Readable, and its pid is alive under the recorded process name (guards pid reuse).</summary>
    public static bool TryReadLiveDescriptor(string path, [NotNullWhen(true)] out MuxEndpointDescriptor? descriptor)
    {
        if (TryReadDescriptor(path, out descriptor) && IsProcessAlive(descriptor.Pid, descriptor.ProcessName)) return true;
        descriptor = null;
        return false;
    }

    public static bool IsProcessAlive(int pid, string processName)
    {
        if (pid <= 0) return false;
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited && string.Equals(process.ProcessName, processName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    public static void DeleteDescriptorIfOwned(string path, int pid)
    {
        if (!TryReadDescriptor(path, out MuxEndpointDescriptor? d) || d.Pid != pid) return;
        try { RetryWhileBusy(() => File.Delete(path)); }
        catch (IOException) { /* best effort: a stale descriptor is harmless (its pid is checked on read) */ }
        catch (UnauthorizedAccessException) { /* best effort, as above */ }
    }

    /// <summary>
    /// Creates a missing descriptor directory owner-only (0700) off Windows. On Linux/macOS the
    /// default socket endpoint lives in this same directory, and the daemon refuses to serve from
    /// one that already exists with any other mode (UnixSocketMuxListener.EnsurePrivateDirectory) -
    /// so a descriptor written first (a crashed daemon's leftover, a client, a test) must not
    /// leave it at the umask default (typically 0755). An existing directory is left untouched:
    /// judging it is the daemon's job, not a writer's. Windows has ACLs, not POSIX modes.
    /// </summary>
    private static void CreatePrivateDirectory(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(dir);
            return;
        }

        if (Directory.Exists(dir)) return;
        const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Directory.CreateDirectory(dir, OwnerOnly);
        // CreateDirectory's mode is filtered by the umask, which can only remove bits: re-assert it.
        File.SetUnixFileMode(dir, OwnerOnly);
    }

    private static void RetryWhileBusy(Action fileOperation)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                fileOperation();
                return;
            }
            catch (Exception ex) when (attempt < 20 && OperatingSystem.IsWindows() && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(10);
            }
        }
    }
}
