using System.Globalization;
using System.Runtime.InteropServices;

namespace Ntilde.Mux.Cli;

/// <summary>
/// Ends this process's stdout and stderr for good, on Unix, where <c>ntilde-mux proxy --stdio</c> runs (codex D1,
/// residual R1). sshd sends the exec channel's EOF only once every descriptor of the child's stdout - and of its
/// stderr - is closed, and closing the proxy's stdout stream closes none of them: .NET's console streams each hold a
/// <c>dup</c> of their descriptor (<c>Console.OpenStandardOutput</c>, <c>Console.Out</c>, <c>Console.Error</c>), and
/// fds 1 and 2 themselves stay open until the process exits. So the client would see a dropped connection end only
/// when the proxy exits. Here every descriptor of this process that refers to the same open file as fd 1 or fd 2 is
/// pointed at <c>/dev/null</c> with <c>dup2</c> - never <c>close</c>, which would let a later open take fd 1 or 2 -
/// after a socket among them is half-closed for writing. Whatever is written to stdout or stderr afterwards goes
/// nowhere.
/// </summary>
internal static class UnixChannelStdio
{
    private const int StdinFd = 0;
    private const int StdoutFd = 1;
    private const int StderrFd = 2;

    /// <summary><c>O_WRONLY</c>, on Linux and macOS alike.</summary>
    private const int OpenWriteOnly = 1;

    /// <summary><c>SHUT_WR</c>, on Linux and macOS alike.</summary>
    private const int ShutdownWrite = 1;

    /// <summary><c>F_GETFL</c>, on Linux and macOS alike.</summary>
    private const int GetStatusFlags = 3;

    /// <summary><c>O_ACCMODE</c>, on Linux and macOS alike.</summary>
    private const int AccessModeMask = 3;

    /// <summary>Room for <c>struct stat</c>: 144 bytes on linux-x64 and osx-arm64, 128 on linux-arm64.</summary>
    private const int StatBytes = 256;

    /// <summary>Where a process's open descriptors are listed: Linux, then macOS.</summary>
    private static readonly string[] DescriptorDirectories = ["/proc/self/fd", "/dev/fd"];

    /// <summary>The proxy's own: fds 1 and 2 end; a file stdin also refers to is left alone.</summary>
    public static void EndStdoutAndStderr() => End([StdoutFd, StderrFd], StdinFd);

    /// <summary>
    /// Ends <paramref name="channelFds"/> for whoever reads them: each socket among them is half-closed for writing,
    /// then every descriptor of this process that refers to the same open file as one of them - each itself, and each
    /// copy - is pointed at <c>/dev/null</c>. A file <paramref name="stdinFd"/> refers to as well is left alone (a
    /// terminal, or a socket that carries stdin too, whose half-close already ended its output). Never throws: without
    /// the libc calls (an unsupported libc), the process's exit still ends them.
    /// </summary>
    internal static void End(IReadOnlyList<int> channelFds, int stdinFd)
    {
        ArgumentNullException.ThrowIfNull(channelFds);
        try
        {
            EndCore(channelFds, stdinFd);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Nothing to call it with: the process's exit closes them a moment later.
        }
    }

    private static void EndCore(IReadOnlyList<int> channelFds, int stdinFd)
    {
        FileIdentity? stdin = IdentityOf(stdinFd);
        var ending = new HashSet<FileIdentity>();
        foreach (int fd in channelFds)
        {
            _ = shutdown(fd, ShutdownWrite); // a pipe or a terminal answers ENOTSOCK: nothing to half-close there
            if (IdentityOf(fd) is { } identity && identity != stdin) ending.Add(identity);
        }

        if (ending.Count == 0) return;

        int devNull = open("/dev/null\0"u8.ToArray(), OpenWriteOnly);
        if (devNull < 0) return;
        try
        {
            foreach (int fd in OpenDescriptors(channelFds))
            {
                if (fd != devNull && IdentityOf(fd) is { } identity && ending.Contains(identity)) _ = dup2(devNull, fd);
            }
        }
        finally
        {
            _ = close(devNull);
        }
    }

    /// <summary>
    /// This process's open descriptors, from <c>/proc/self/fd</c> (Linux) or <c>/dev/fd</c> (macOS); just
    /// <paramref name="fallback"/> when neither can be listed.
    /// </summary>
    private static IReadOnlyList<int> OpenDescriptors(IReadOnlyList<int> fallback)
    {
        foreach (string directory in DescriptorDirectories)
        {
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var fds = new List<int>(entries.Length);
            foreach (string entry in entries)
            {
                if (int.TryParse(Path.GetFileName(entry), NumberStyles.None, CultureInfo.InvariantCulture, out int fd)) fds.Add(fd);
            }

            if (fds.Count > 0) return fds;
        }

        return fallback;
    }

    /// <summary>
    /// The open file <paramref name="fd"/> refers to, by device, inode and access mode; null when it is not open. A
    /// pipe's two ends share one inode, so the mode is what tells this process's copies of the write end (fd 1's) from
    /// a read end of the same pipe. <c>st_ino</c> is the 64-bit field at offset 8 in every <c>struct stat</c> this runs
    /// with (linux-x64, linux-arm64, osx-arm64's 64-bit inode one); <c>st_dev</c>, at offset 0, is 64 bits on Linux and
    /// 32 on macOS.
    /// </summary>
    private static FileIdentity? IdentityOf(int fd)
    {
        if (fd < 0) return null;
        byte[] stat = new byte[StatBytes];
        if (fstat(fd, stat) != 0) return null;
        int flags = fcntl(fd, GetStatusFlags);
        if (flags < 0) return null;
        ulong device = OperatingSystem.IsMacOS() ? BitConverter.ToUInt32(stat, 0) : BitConverter.ToUInt64(stat, 0);
        return new FileIdentity(device, BitConverter.ToUInt64(stat, 8), flags & AccessModeMask);
    }

    private readonly record struct FileIdentity(ulong Device, ulong Inode, int AccessMode);

    [DllImport("libc", SetLastError = true)]
    private static extern int fstat(int fd, byte[] buffer);

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int fd, int command);

    [DllImport("libc", SetLastError = true)]
    private static extern int shutdown(int socket, int how);

    [DllImport("libc", SetLastError = true)]
    private static extern int open(byte[] path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int dup2(int oldfd, int newfd);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);
}
