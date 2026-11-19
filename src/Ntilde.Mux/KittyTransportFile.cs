using System;
using System.IO;

namespace Ntilde.Mux;

/// <summary>
/// The one reader behind Kitty file transport (<c>t=f</c>, #887): the App's direct panes read with it, and the persistent-session daemon reads with it on
/// its host (only the daemon can: the path names a file there). The path comes from the child's byte stream, so it is hostile: absolute paths only, confined
/// to the user's temp directory (where clients like terminal-browser stage raw RGBA frames), nothing reached through a symbolic link that leaves it, regular
/// files only, size-capped, and every failure degrades to null (which the parser answers as an unavailable payload).
/// </summary>
public static class KittyTransportFile
{
    /// <summary>
    /// Hard cap on a single read. A 2000x2000 RGBA frame, the parser's pixel guardrail, is 16 MB, so 64 MB leaves generous headroom for legitimate staged frames while
    /// keeping a hostile path from pulling an arbitrary file into memory.
    /// </summary>
    public const long MaxFileBytes = 64L * 1024 * 1024;

    /// <summary>Reads <paramref name="path"/> under the temp-directory confinement and <see cref="MaxFileBytes"/>; null for anything refused or unreadable.</summary>
    public static byte[]? Read(string? path) => Read(path, MaxFileBytes);

    /// <summary>As <see cref="Read(string?)"/> with a smaller cap, for a caller whose transport carries less (a multiplexer frame).</summary>
    public static byte[]? Read(string? path, long maxBytes)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return null;
        string candidate, tempRoot;
        try
        {
            candidate = Path.GetFullPath(path);
            tempRoot = Path.GetFullPath(Path.GetTempPath());
        }
        catch (Exception)
        {
            return null;
        }

        // Casing follows the platform's filesystem rules: ignore-case is only correct where the filesystem itself is case-insensitive - on Unix, /TMP/frame.rgba
        // is a different directory from /tmp/frame.rgba, and accepting it as "under temp" would let a case-variant path escape the confinement boundary.
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!candidate.StartsWith(tempRoot, comparison) || !StaysUnder(tempRoot, candidate, comparison)) return null;
        try
        {
            var info = new FileInfo(candidate);
            if (!info.Exists || info.Length > maxBytes) return null;

            // The writer (e.g. terminal-browser's frame ring) keeps the file open while it cycles frames, so a plain File.ReadAllBytes' FileShare.Read gets rejected
            // with a sharing violation. Open tolerating concurrent writers: the path is only sent after the frame write completes, so the bytes read are the finished frame.
            using var stream = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            // Re-check the cap on the OPENED handle: the file can grow between the FileInfo probe above and this open, and the allocation below trusts the length blindly.
            if (stream.Length > maxBytes) return null;
            var bytes = new byte[stream.Length];
            int read = 0;
            while (read < bytes.Length)
            {
                int n = stream.Read(bytes, read, bytes.Length - read);
                if (n <= 0) break;
                read += n;
            }

            return read == bytes.Length ? bytes : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Walks the path from the temp directory down, and refuses it when any component is a symbolic link (or reparse point) whose final target is not itself under
    /// the temp directory: a lexical prefix check alone would let <c>/tmp/x -> /etc</c> read <c>/tmp/x/passwd</c>.
    /// </summary>
    private static bool StaysUnder(string tempRoot, string candidate, StringComparison comparison)
    {
        try
        {
            string realRoot = Path.TrimEndingDirectorySeparator(ResolveFinal(tempRoot) ?? tempRoot);
            string current = Path.TrimEndingDirectorySeparator(tempRoot);
            foreach (string part in candidate[tempRoot.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                FileSystemInfo node = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                if (node.LinkTarget is null) continue;
                string? target = ResolveFinal(current);
                if (target is null || !(Path.TrimEndingDirectorySeparator(target) + Path.DirectorySeparatorChar).StartsWith(realRoot + Path.DirectorySeparatorChar, comparison)) return false;
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? ResolveFinal(string path)
    {
        FileSystemInfo node = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        return node.LinkTarget is null ? node.FullName : node.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
    }
}
