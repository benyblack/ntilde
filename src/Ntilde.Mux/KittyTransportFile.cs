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
            // Refused before the file system is touched at all: on Windows a UNC or device path would start network or device operations on the parser's thread just to be probed.
            // On Unix the path is NOT normalised first: the kernel resolves ".." after the link that precedes it (jump/.. is the parent of jump's target), and Path.GetFullPath would
            // collapse it before the link is looked at and open another file than the program meant. Canonicalize applies "." and ".." as it resolves. Windows normalises lexically.
            string full = Normalize(path), temp = Path.GetFullPath(Path.GetTempPath());
            if (IsNetworkOrDevicePath(full) || !full.StartsWith(temp, comparison)) return null;
            string? root = Canonicalize(temp), real = Canonicalize(full);
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
        if (stream is null || !stream.CanSeek) return null;
        // Taken ONCE, from the OPENED handle, and used for the check, the allocation and the read: the file can grow at any moment, and a second query could allocate past the cap.
        long length = stream.Length;
        if (length > maxBytes) return null;
        var bytes = new byte[length];
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

            string parent = Join(root, resolved);
            string? target = LinkTargetOf(Path.Combine(parent, part));
            if (target is null)
            {
                resolved.Add(part);
                continue;
            }

            string? next = ++hops > MaxLinkHops ? null : FollowTarget(parent, target);
            if (next is null) return null;
            // Resolve the link's target from the start, then whatever followed the link on the original path.
            root = Path.GetPathRoot(next) ?? string.Empty;
            resolved.Clear();
            foreach (string item in Split(next[root.Length..]).Reverse()) pending.Push(item);
        }

        return Join(root, resolved);
    }

    private static string? LinkTargetOf(string current) => (Directory.Exists(current) ? new DirectoryInfo(current) : (FileSystemInfo)new FileInfo(current)).LinkTarget;

    /// <summary>Where a link found in <paramref name="parent"/> leads, or null when that is a share or a device: a link must not send the walk there.</summary>
    private static string? FollowTarget(string parent, string target)
    {
        string next = Normalize(Path.IsPathRooted(target) ? target : Path.Combine(parent, target));
        return IsNetworkOrDevicePath(next) ? null : next;
    }

    /// <summary>Windows normalises a path lexically (as its file system does); Unix leaves it for <see cref="Canonicalize"/>, which resolves "." and ".." the way the kernel does.</summary>
    private static string Normalize(string path) => OperatingSystem.IsWindows() ? Path.GetFullPath(path) : path;

    /// <summary>A Windows UNC share or device namespace path (<c>\\host\share</c>, <c>\\.\COM1</c>, <c>\\?\...</c>); never a path on a Unix filesystem.</summary>
    private static bool IsNetworkOrDevicePath(string full) => OperatingSystem.IsWindows() && full.StartsWith(@"\\", StringComparison.Ordinal);

    private static string[] Split(string path) => path.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

    private static string Join(string root, List<string> parts) => parts.Count == 0 ? root : Path.Combine(root, Path.Combine(parts.ToArray()));
}
