using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Ntilde.Mux;

/// <summary>
/// The one reader behind Kitty file transport (<c>t=f</c>, #887): the App's direct panes read with it, and the persistent-session daemon reads with it on
/// its host (only the daemon can: the path names a file there). The path comes from the child's byte stream, so it is hostile: absolute paths only, confined
/// to the user's temp directory (where clients like terminal-browser stage raw RGBA frames), resolved through every symbolic link before the confinement is
/// checked (and the resolved path is what is opened), regular files only (a FIFO would block the reader for as long as no writer opens it), size-capped,
/// and every failure degrades to null (which the parser answers as an unavailable payload).
/// A program that can create links in the user's temp directory can still swap one between the check and the open; it already runs as that user.
/// </summary>
public static class KittyTransportFile
{
    /// <summary>
    /// Hard cap on a single read. A 2000x2000 RGBA frame, the parser's pixel guardrail, is 16 MB, so 64 MB leaves generous headroom for legitimate staged frames while
    /// keeping a hostile path from pulling an arbitrary file into memory.
    /// </summary>
    public const long MaxFileBytes = 64L * 1024 * 1024;

    /// <summary>The most symbolic links one path may pass through, so a loop of links is refused instead of followed.</summary>
    private const int MaxLinkHops = 40;

    /// <summary>Reads <paramref name="path"/> under the temp-directory confinement and <see cref="MaxFileBytes"/>; null for anything refused or unreadable.</summary>
    public static byte[]? Read(string? path) => Read(path, MaxFileBytes);

    /// <summary>As <see cref="Read(string?)"/> with a smaller cap, for a caller whose transport carries less (a multiplexer frame).</summary>
    public static byte[]? Read(string? path, long maxBytes)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return null;
        try
        {
            // Casing follows the platform's filesystem rules: ignore-case is only correct where the filesystem itself is case-insensitive - on Unix, /TMP/frame.rgba
            // is a different directory from /tmp/frame.rgba, and accepting it as "under temp" would let a case-variant path escape the confinement boundary.
            StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            string? root = Canonicalize(Path.GetFullPath(Path.GetTempPath())), real = Canonicalize(Path.GetFullPath(path));
            if (root is null || real is null) return null;
            string prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
            if (!real.StartsWith(prefix, comparison)) return null;
            return ReadRegular(real, maxBytes);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static byte[]? ReadRegular(string real, long maxBytes)
    {
        using FileStream? stream = OpenRegular(real);
        // Re-checked on the OPENED handle: the file can grow between a size probe and the open, and the allocation below trusts the length blindly.
        if (stream is null || !stream.CanSeek || stream.Length > maxBytes) return null;
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

    /// <summary>
    /// Opens a file for reading without ever blocking on a special file. On Unix the open is non-blocking, so a FIFO with no writer opens at once, and only a stream that can seek
    /// (a regular file) is kept. On Windows the writer (e.g. terminal-browser's frame ring) keeps the file open while it cycles frames, so a plain share-read open gets rejected
    /// with a sharing violation: the open tolerates concurrent writers, and the path is only sent after the frame write completes.
    /// </summary>
    private static FileStream? OpenRegular(string real)
    {
        if (OperatingSystem.IsWindows()) return new FileStream(real, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())) return null;
        int flags = OperatingSystem.IsMacOS() ? 0x4 | 0x1000000 : 0x800 | 0x80000; // O_NONBLOCK | O_CLOEXEC
        byte[] name = Encoding.UTF8.GetBytes(real + "\0");
        int fd = Open(name, flags);
        if (fd < 0) return null;
        var handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        try
        {
            var stream = new FileStream(handle, FileAccess.Read, bufferSize: 1, isAsync: false);
            if (stream.CanSeek) return stream;
            stream.Dispose();
            return null;
        }
        catch (Exception)
        {
            handle.Dispose();
            return null;
        }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(byte[] path, int flags);

    /// <summary>
    /// The path with every symbolic link (or reparse point) on it resolved, component by component, so nothing that is later opened can lead out of what was checked: a lexical prefix
    /// check alone would let <c>/tmp/x -> /etc</c> read <c>/tmp/x/passwd</c>, and following only the final link would miss <c>/tmp/image -> /tmp/escape/hosts</c> with <c>/tmp/escape -> /etc</c>.
    /// Null for a loop of links or more than <see cref="MaxLinkHops"/> of them.
    /// </summary>
    private static string? Canonicalize(string full)
    {
        string root = Path.GetPathRoot(full) ?? string.Empty;
        var pending = new Stack<string>(Split(full[root.Length..]).Reverse());
        var resolved = new List<string>();
        int hops = 0;
        while (pending.Count > 0)
        {
            string part = pending.Pop();
            if (part == ".") continue;
            if (part == "..")
            {
                if (resolved.Count > 0) resolved.RemoveAt(resolved.Count - 1);
                continue;
            }

            string parent = Join(root, resolved), current = Path.Combine(parent, part);
            FileSystemInfo node = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            string? target = node.LinkTarget;
            if (target is null)
            {
                resolved.Add(part);
                continue;
            }

            if (++hops > MaxLinkHops) return null;
            string next = Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(parent, target));
            // Resolve the link's target from the start, then whatever followed the link on the original path.
            root = Path.GetPathRoot(next) ?? string.Empty;
            resolved.Clear();
            foreach (string item in Split(next[root.Length..]).Reverse()) pending.Push(item);
        }

        return Join(root, resolved);
    }

    private static string[] Split(string path) => path.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

    private static string Join(string root, List<string> parts) => parts.Count == 0 ? root : Path.Combine(root, Path.Combine(parts.ToArray()));
}
