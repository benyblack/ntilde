using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Ntilde.Mux.TextClient;

namespace Ntilde.Mux.Tests.TextClient;

public sealed class UnixConsoleSurfaceTests
{
    // Linux values. fcntl is variadic, which is only safe to P/Invoke like this on x86-64/arm64 Linux.
    private const int FSetFl = 4;
    private const int ONonBlock = 0x800;

    [DllImport("libc", SetLastError = true)]
    private static extern int pipe(int[] fds);

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int fd, int cmd, int arg);

    [DllImport("libc", SetLastError = true)]
    private static extern nint write(int fd, byte[] buffer, nuint count);

    [DllImport("libc")]
    private static extern int close(int fd);

    /// <summary>
    /// A stdin someone else made O_NONBLOCK reports EAGAIN when it has nothing yet. That is not
    /// end-of-input: the read must wait (poll) and deliver the bytes that arrive later.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("linux")]
    public void Eagain_waits_instead_of_closing_input()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux: the test sets O_NONBLOCK through fcntl, which is variadic");

        var fds = new int[2];
        Assert.Equal(0, pipe(fds));
        try
        {
            Assert.Equal(0, fcntl(fds[0], FSetFl, ONonBlock));
            var buffer = new byte[16];
            int result = int.MinValue;
            var reader = new Thread(() => result = UnixConsoleSurface.ReadFd(fds[0], buffer, buffer.Length)) { IsBackground = true };
            reader.Start();

            Assert.False(reader.Join(200), "ReadFd returned before any input arrived (EAGAIN treated as end of input).");
            Assert.Equal(2, write(fds[1], "hi"u8.ToArray(), 2));

            Assert.True(reader.Join(TimeSpan.FromSeconds(5)), "ReadFd did not wake when input arrived.");
            Assert.Equal(2, result);
            Assert.Equal("hi", System.Text.Encoding.ASCII.GetString(buffer, 0, 2));
        }
        finally
        {
            _ = close(fds[0]);
            _ = close(fds[1]);
        }
    }
}
