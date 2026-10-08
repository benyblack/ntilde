using System.Globalization;
using System.Text;

namespace Ntilde.Shell.Mux;

/// <summary>
/// Where the local daemon's executable runs from (Phase 5 spec R9). On a Windows Velopack install, Velopack's apply
/// kills every process whose image is under the install root, so the daemon runs from a copy at
/// <c>&lt;app-data&gt;\bin\&lt;version&gt;\</c> - Ntilde.exe, the DLLs beside it (rusty_pty.dll, conpty.dll, ...) and the
/// <c>&lt;arch&gt;\OpenConsole.exe</c> hosts conpty.dll starts - staged once per version. Elsewhere, and for dev
/// builds, the running executable itself.
/// </summary>
/// <remarks>
/// What the copy holds is everything <c>mux serve</c> loads from beside its executable. The release bundle is NativeAOT
/// (release.yml publishes with <c>PublishAot</c> and packs that folder as <c>current\</c>), so every managed assembly is
/// inside Ntilde.exe, and there is no runtimeconfig, deps file, <c>runtimes\</c> folder or satellite assembly to carry.
/// The native libraries sit flat beside it: rusty_pty.dll (the PTY), conpty.dll (portable-pty loads it from the
/// application directory), rusty_ssh.dll, and the GUI's Skia/HarfBuzz/ANGLE DLLs, which cost little and keep the rule
/// "every DLL" simple. conpty.dll starts <c>&lt;its directory&gt;\&lt;arch&gt;\OpenConsole.exe</c>: without those every
/// shell's console host would still run from the install root and die with the update. ICU is the system's. Fonts,
/// themes and ntilde.com stay behind: the daemon renders nothing, and the fonts are also embedded resources.
/// </remarks>
internal static class MuxDaemonImage
{
    /// <summary>Under the app-data root: one copy per version, <c>&lt;version&gt;</c> or <c>&lt;version&gt;-&lt;n&gt;</c>.</summary>
    internal const string DirectoryName = "bin";

    /// <summary>In a copy, written last: one line per file, its size and its path relative to the copy.</summary>
    internal const string CompleteFileName = ".complete";

    /// <summary>A staging folder older than this is a crashed stager's, not one at work: pruning deletes it.</summary>
    internal static readonly TimeSpan StaleStagingAge = TimeSpan.FromHours(1);

    private const string CurrentDirectoryName = "current";
    private const string UpdateExeName = "Update.exe";
    private const string ConsoleHostFileName = "OpenConsole.exe";

    /// <summary>The console hosts a bundle can carry (Ntilde.App.csproj's NtildeRequiredConPtyHost).</summary>
    private static readonly string[] ConsoleHostArchitectures = ["arm64", "x64", "x86"];

    /// <summary>How many <c>&lt;version&gt;-&lt;n&gt;</c> folders a re-pack under the same version may try.</summary>
    private const int MaxRepacks = 16;

    /// <summary>How often the rename into place is tried, and how long apart: about a second in all.</summary>
    private const int MoveAttempts = 5;

    private static readonly TimeSpan MoveRetryDelay = TimeSpan.FromMilliseconds(250);

    private static int s_pruneStarted;

    /// <summary>The install root when exePath is <c>&lt;root&gt;\current\&lt;exe&gt;</c> and <c>&lt;root&gt;\Update.exe</c> exists; else null.</summary>
    public static string? VelopackInstallRoot(string exePath, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        if (string.IsNullOrEmpty(exePath)) return null;
        string? current = Path.GetDirectoryName(exePath);
        if (string.IsNullOrEmpty(current) || !string.Equals(Path.GetFileName(current), CurrentDirectoryName, StringComparison.OrdinalIgnoreCase)) return null;
        string? root = Path.GetDirectoryName(current);
        if (string.IsNullOrEmpty(root)) return null;
        return fileExists(Path.Combine(root, UpdateExeName)) ? root : null;
    }

    /// <summary>
    /// Stages the copy if needed and returns its executable; returns <paramref name="exePath"/> when no copy is needed or
    /// staging failed (logged: the daemon then dies with the next update, as before Phase 5). Never throws.
    /// </summary>
    /// <remarks>
    /// The copy is built in <c>bin\.&lt;version&gt;.&lt;guid&gt;.tmp\</c>, its <see cref="CompleteFileName"/> written last,
    /// then renamed into place, so a folder under its version's name is always whole. One that exists is reused when its
    /// <see cref="CompleteFileName"/> lists exactly this install's files at their sizes and they are all there. One whose
    /// list names other sizes holds another build under the same version (a re-pack): it may be running a daemon, so it
    /// is left alone and the next <c>&lt;version&gt;-&lt;n&gt;</c> is used. One without a valid list, or missing a file,
    /// is replaced, unless a daemon runs from it. A stager that loses the rename to another uses the winner's copy.
    /// </remarks>
    public static string Resolve(string exePath, string appDataRoot, string version, IMuxImageFileSystem fs, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(log);
        string? installRoot = VelopackInstallRoot(exePath, fs.FileExists);
        if (installRoot is null) return exePath;

        if (!IsFolderName(version))
        {
            log($"[Mux] the multiplexer daemon runs from the install folder, and an update will stop it: this build's version '{version}' cannot name a folder for its own copy");
            return exePath;
        }

        string bin = Path.Combine(appDataRoot, DirectoryName);
        try
        {
            if (IsSameOrUnder(bin, installRoot))
            {
                log($"[Mux] the multiplexer daemon runs from the install folder, and an update will stop it: the app-data root {appDataRoot} is inside the install root {installRoot}");
                return exePath;
            }

            IReadOnlyList<ImageFile> image = ListImage(exePath, fs);
            string exeName = Path.GetFileName(exePath);
            for (int n = 0; n <= MaxRepacks; n++)
            {
                string copy = Path.Combine(bin, n == 0 ? version : version + "-" + n.ToString(CultureInfo.InvariantCulture));
                if (TryUse(copy, image, exeName, fs, log) is { } exe) return exe;
            }

            log($"[Mux] the multiplexer daemon runs from the install folder, and an update will stop it: no free folder for its own copy under {bin}");
        }
        catch (Exception ex)
        {
            // Anything: the fallback is the daemon as it ran before Phase 5, and a throw here would start no daemon at all.
            log($"[Mux] the multiplexer daemon runs from the install folder, and an update will stop it: its own copy under {bin} could not be staged: {ex.Message}");
        }

        return exePath;
    }

    /// <summary>
    /// Deletes <c>&lt;app-data&gt;\bin\&lt;v&gt;</c> folders other than the current version's (<c>&lt;v&gt;</c> and its
    /// re-packs, <c>&lt;v&gt;-&lt;n&gt;</c>), and staging folders a crashed stager left. A folder in use (a running older
    /// daemon) fails to delete and is kept whole. Best effort, never throws; the caller runs it off the UI thread.
    /// </summary>
    public static void PruneOldCopies(string appDataRoot, string currentVersion, IMuxImageFileSystem fs, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(log);
        // Without a version there is nothing to keep, and every copy - a running daemon's included - would look old.
        if (!IsFolderName(currentVersion)) return;

        string bin = Path.Combine(appDataRoot, DirectoryName);
        IReadOnlyList<string> folders;
        try
        {
            if (!fs.DirectoryExists(bin)) return;
            folders = fs.GetDirectories(bin);
        }
        catch (Exception ex)
        {
            log($"[Mux] could not list the multiplexer daemon's copies in {bin}: {ex.Message}");
            return;
        }

        foreach (string folder in folders)
        {
            string name = Path.GetFileName(folder);
            try
            {
                if (name.StartsWith('.'))
                {
                    // A stager's folder: at work while fresh, a crashed one's once stale. Nothing ever runs from one.
                    if (DateTime.UtcNow - fs.GetLastWriteTimeUtc(folder) >= StaleStagingAge) fs.DeleteDirectory(folder);
                    continue;
                }

                if (IsCurrentVersion(name, currentVersion)) continue;

                if (TryDeleteCopy(folder, fs, out string? kept)) log($"[Mux] deleted the multiplexer daemon's copy {folder}: no daemon runs from it");
                else log($"[Mux] kept the multiplexer daemon's copy {folder}: {kept}");
            }
            catch (Exception ex)
            {
                log($"[Mux] could not delete the multiplexer daemon's copy {folder}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// The resolver the App's spawners take: <see cref="Resolve"/> for this build's version on Windows (the only place
    /// with a Velopack install root to leave); elsewhere the executable itself.
    /// </summary>
    internal static Func<string, string> ResolverFor(string appDataRoot, Action<string>? log)
    {
        Action<string> sink = log ?? (static _ => { });
        return exePath => OperatingSystem.IsWindows()
            ? Resolve(exePath, appDataRoot, AppVersionInfo.Version, MuxImageFileSystem.Instance, sink)
            : exePath;
    }

    /// <summary>
    /// Once per process, on the thread pool: <see cref="PruneOldCopies"/> for this build's version - when this process
    /// runs from a Windows Velopack install, the only kind that stages copies. A dev build sharing the app-data root
    /// never deletes an installed build's copies.
    /// </summary>
    internal static void StartPruningOnce(string appDataRoot, Action<string>? log)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (Environment.ProcessPath is not { } exe || VelopackInstallRoot(exe, File.Exists) is null) return;
        if (Interlocked.Exchange(ref s_pruneStarted, 1) != 0) return;
        Action<string> sink = log ?? (static _ => { });
        _ = Task.Run(() => PruneOldCopies(appDataRoot, AppVersionInfo.Version, MuxImageFileSystem.Instance, sink));
    }

    /// <summary>One file of the image: where it is in the install, where it goes in a copy, and its size.</summary>
    private sealed record ImageFile(string SourcePath, string RelativePath, long Length);

    private enum CopyState
    {
        /// <summary>No folder.</summary>
        Absent,

        /// <summary>Whole, and this install's files: run it.</summary>
        Usable,

        /// <summary>Whole, but another build's files under the same version: leave it, try the next name.</summary>
        OtherBuild,

        /// <summary>No valid <see cref="CompleteFileName"/>, or a file it lists is missing or changed: replace it.</summary>
        Broken,
    }

    /// <summary>The executable, every DLL beside it, and each <c>&lt;arch&gt;\OpenConsole.exe</c> there is.</summary>
    private static List<ImageFile> ListImage(string exePath, IMuxImageFileSystem fs)
    {
        string dir = Path.GetDirectoryName(exePath)!;
        var image = new List<ImageFile>();
        void Add(string path) => image.Add(new ImageFile(path, Path.GetRelativePath(dir, path), fs.GetFileLength(path)));

        Add(exePath);
        // The extension checked again: a "*.dll" search on Windows also matches longer extensions through 8.3 names.
        foreach (string dll in fs.GetFiles(dir, "*.dll").Where(f => string.Equals(Path.GetExtension(f), ".dll", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase))
        {
            Add(dll);
        }

        foreach (string arch in ConsoleHostArchitectures)
        {
            string host = Path.Combine(dir, arch, ConsoleHostFileName);
            if (fs.FileExists(host)) Add(host);
        }

        return image;
    }

    /// <summary>The copy's executable when <paramref name="copy"/> is (or now holds) this install's image; null to try the next name.</summary>
    private static string? TryUse(string copy, IReadOnlyList<ImageFile> image, string exeName, IMuxImageFileSystem fs, Action<string> log)
    {
        switch (Inspect(copy, image, fs))
        {
            case CopyState.Usable:
                return Path.Combine(copy, exeName);
            case CopyState.OtherBuild:
                return null;
            case CopyState.Broken:
                if (!TryDeleteCopy(copy, fs, out string? kept))
                {
                    log($"[Mux] kept the incomplete multiplexer daemon copy {copy}: {kept}; staging another");
                    return null;
                }

                return Stage(copy, image, exeName, fs, log);
            default:
                return Stage(copy, image, exeName, fs, log);
        }
    }

    private static CopyState Inspect(string copy, IReadOnlyList<ImageFile> image, IMuxImageFileSystem fs)
    {
        if (!fs.DirectoryExists(copy)) return CopyState.Absent;
        string complete = Path.Combine(copy, CompleteFileName);
        if (!fs.FileExists(complete) || ParseComplete(fs.ReadAllText(complete)) is not { } listed) return CopyState.Broken;
        if (listed.Count != image.Count || image.Any(f => !listed.TryGetValue(f.RelativePath, out long length) || length != f.Length)) return CopyState.OtherBuild;
        foreach (ImageFile f in image)
        {
            string path = Path.Combine(copy, f.RelativePath);
            if (!fs.FileExists(path) || fs.GetFileLength(path) != f.Length) return CopyState.Broken;
        }

        return CopyState.Usable;
    }

    /// <summary>Builds the copy beside <paramref name="copy"/> and renames it into place; null when another build won that name.</summary>
    private static string? Stage(string copy, IReadOnlyList<ImageFile> image, string exeName, IMuxImageFileSystem fs, Action<string> log)
    {
        string staging = Path.Combine(Path.GetDirectoryName(copy)!, "." + Path.GetFileName(copy) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            fs.CreateDirectory(staging);
            foreach (ImageFile f in image)
            {
                string target = Path.Combine(staging, f.RelativePath);
                fs.CreateDirectory(Path.GetDirectoryName(target)!);
                fs.CopyFile(f.SourcePath, target);
                long copied = fs.GetFileLength(target);
                if (copied != f.Length) throw new IOException($"{f.SourcePath} changed while it was copied ({f.Length} bytes listed, {copied} copied).");
            }

            fs.WriteAllText(Path.Combine(staging, CompleteFileName), FormatComplete(image));
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    fs.MoveDirectory(staging, copy);
                    break;
                }
                catch (IOException) when (fs.DirectoryExists(copy))
                {
                    // Another stager's rename landed first: its copy is as good as ours when it holds the same files.
                    DeleteQuietly(staging, fs);
                    return Inspect(copy, image, fs) == CopyState.Usable ? Path.Combine(copy, exeName) : null;
                }
                catch (Exception ex) when (attempt < MoveAttempts && ex is (IOException or UnauthorizedAccessException))
                {
                    // An antivirus scanning the fresh files can hold one open for a moment, and the rename fails meanwhile.
                    Thread.Sleep(MoveRetryDelay);
                }
            }
        }
        catch
        {
            DeleteQuietly(staging, fs);
            throw;
        }

        log($"[Mux] staged the multiplexer daemon's own copy at {copy}, so updates leave it running");
        return Path.Combine(copy, exeName);
    }

    /// <summary>
    /// Deletes a copy unless a daemon runs from it. Windows refuses to delete a running executable, but would let its
    /// folder be renamed or its other files go: so the executables at the top go first, and a refusal there leaves the
    /// copy whole. Null in <paramref name="kept"/> when it is gone; otherwise why not.
    /// </summary>
    private static bool TryDeleteCopy(string folder, IMuxImageFileSystem fs, out string? kept)
    {
        try
        {
            foreach (string exe in fs.GetFiles(folder, "*.exe"))
            {
                try
                {
                    fs.DeleteFile(exe);
                }
                catch (Exception ex) when (ex is (IOException and not DirectoryNotFoundException) or UnauthorizedAccessException)
                {
                    kept = "a daemon runs from it";
                    return false;
                }
            }

            fs.DeleteDirectory(folder);
        }
        catch (DirectoryNotFoundException)
        {
            // Gone already: another launch deleted it first.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            kept = ex.Message;
            return false;
        }

        kept = null;
        return true;
    }

    private static void DeleteQuietly(string folder, IMuxImageFileSystem fs)
    {
        try
        {
            fs.DeleteDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next launch's pruning (a stale staging folder).
        }
    }

    private static string FormatComplete(IReadOnlyList<ImageFile> image)
    {
        var text = new StringBuilder();
        foreach (ImageFile f in image) text.Append(f.Length.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(f.RelativePath).Append('\n');
        return text.ToString();
    }

    /// <summary>The sizes <see cref="CompleteFileName"/> lists by relative path; null when it is not a valid list.</summary>
    private static Dictionary<string, long>? ParseComplete(string text)
    {
        var listed = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in text.Split('\n'))
        {
            string entry = line.TrimEnd('\r');
            if (entry.Length == 0) continue;
            int tab = entry.IndexOf('\t', StringComparison.Ordinal);
            if (tab <= 0 || !long.TryParse(entry.AsSpan(0, tab), NumberStyles.None, CultureInfo.InvariantCulture, out long length)) return null;
            string path = entry[(tab + 1)..];
            if (path.Length == 0 || Path.IsPathRooted(path) || !listed.TryAdd(path, length)) return null;
        }

        return listed.Count > 0 ? listed : null;
    }

    /// <summary>A version that can name a folder of its own under <c>bin</c>: not empty, not hidden, no separators.</summary>
    private static bool IsFolderName(string? version) =>
        !string.IsNullOrWhiteSpace(version)
        && version[0] != '.'
        && !version.EndsWith('.')
        && version.Trim().Length == version.Length
        && version.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && version.IndexOfAny(['\\', '/']) < 0;

    /// <summary><paramref name="name"/> is <paramref name="version"/>'s copy: the version itself, or a re-pack of it (<c>-&lt;n&gt;</c>).</summary>
    private static bool IsCurrentVersion(string name, string version)
    {
        if (string.Equals(name, version, StringComparison.OrdinalIgnoreCase)) return true;
        if (!name.StartsWith(version + "-", StringComparison.OrdinalIgnoreCase)) return false;
        string n = name[(version.Length + 1)..];
        return n.Length > 0 && n.All(char.IsAsciiDigit);
    }

    private static bool IsSameOrUnder(string path, string directory)
    {
        string p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string d = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        return string.Equals(p, d, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>A thin seam over <see cref="File"/> and <see cref="Directory"/> for <see cref="MuxDaemonImage"/>; tests use an in-memory one.</summary>
internal interface IMuxImageFileSystem
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    long GetFileLength(string path);

    /// <summary>The files directly in <paramref name="directory"/> matching <paramref name="searchPattern"/> (<c>*</c> or <c>*.ext</c>).</summary>
    IReadOnlyList<string> GetFiles(string directory, string searchPattern);

    /// <summary>The directories directly in <paramref name="directory"/>.</summary>
    IReadOnlyList<string> GetDirectories(string directory);

    DateTime GetLastWriteTimeUtc(string path);

    void CreateDirectory(string path);

    /// <summary>Copies a file to a destination that must not exist yet.</summary>
    void CopyFile(string source, string destination);

    string ReadAllText(string path);

    void WriteAllText(string path, string contents);

    /// <summary>Renames a directory; throws <see cref="IOException"/> when <paramref name="destination"/> exists.</summary>
    void MoveDirectory(string source, string destination);

    void DeleteFile(string path);

    /// <summary>Deletes a directory and everything in it.</summary>
    void DeleteDirectory(string path);
}

/// <summary>The real file system.</summary>
internal sealed class MuxImageFileSystem : IMuxImageFileSystem
{
    public static MuxImageFileSystem Instance { get; } = new();

    private MuxImageFileSystem()
    {
    }

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public long GetFileLength(string path) => new FileInfo(path).Length;

    public IReadOnlyList<string> GetFiles(string directory, string searchPattern) => Directory.GetFiles(directory, searchPattern, SearchOption.TopDirectoryOnly);

    public IReadOnlyList<string> GetDirectories(string directory) => Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly);

    public DateTime GetLastWriteTimeUtc(string path) => Directory.GetLastWriteTimeUtc(path);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void CopyFile(string source, string destination) => File.Copy(source, destination, overwrite: false);

    public string ReadAllText(string path) => File.ReadAllText(path);

    public void WriteAllText(string path, string contents) => File.WriteAllText(path, contents);

    public void MoveDirectory(string source, string destination) => Directory.Move(source, destination);

    public void DeleteFile(string path) => File.Delete(path);

    public void DeleteDirectory(string path) => Directory.Delete(path, recursive: true);
}
