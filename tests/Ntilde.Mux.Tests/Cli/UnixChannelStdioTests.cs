using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Ntilde.Mux.Cli;

namespace Ntilde.Mux.Tests.Cli;

/// <summary>
/// <see cref="UnixChannelStdio"/> (codex D1, residual R1), Unix only: a pipe stands in for the exec channel's stdout,
/// its write end for fd 1, and close-on-exec copies of it (<see cref="CopyOf"/>) for the ones .NET's console streams hold. sshd sends the channel's
/// EOF only once every one of them is closed, so its reader here must see EOF while those copies are still open.
/// The test's own fds 1 and 2 are never touched.
/// </summary>
public sealed class UnixChannelStdioTests
{
    private static readonly TimeSpan Patient = TimeSpan.FromSeconds(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The read end, and the write end's descriptor (owned by <paramref name="writer"/>).</summary>
    private static (AnonymousPipeServerStream Reader, int WriteFd) Pipe(out AnonymousPipeClientStream writer)
    {
        var reader = new AnonymousPipeServerStream(PipeDirection.In);
        writer = new AnonymousPipeClientStream(PipeDirection.Out, reader.ClientSafePipeHandle);
        return (reader, (int)reader.ClientSafePipeHandle.DangerousGetHandle());
    }

    private static Task<int> ReadAsync(Stream reader)
    {
        byte[] buffer = new byte[16];
        return Task.Run(() => reader.Read(buffer, 0, buffer.Length));
    }

    /// <summary>Why closing the proxy's stdout stream was not enough: one copy of the descriptor keeps the pipe open.</summary>
    [Fact]
    public async Task Closing_one_descriptor_leaves_the_reader_waiting_while_a_copy_lives()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix descriptors.");
        (AnonymousPipeServerStream reader, int writeFd) = Pipe(out AnonymousPipeClientStream writer);
        SafeFileHandle copy = CopyOf(writeFd);
        try
        {
            reader.ClientSafePipeHandle.Dispose();   // our handle to the client end; the writer stream's is the same
            writer.Dispose();

            Task<int> read = ReadAsync(reader);
            await Task.Delay(300, Ct);

            Assert.False(read.IsCompleted, "the reader saw EOF although a copy of the write end is open");
        }
        finally
        {
            copy.Dispose();   // now the reader's EOF comes, and its task ends
            reader.Dispose();
        }
    }

    [Fact]
    public async Task Every_copy_of_a_channel_descriptor_goes_to_dev_null_so_its_reader_sees_EOF()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix descriptors.");
        (AnonymousPipeServerStream reader, int writeFd) = Pipe(out AnonymousPipeClientStream writer);
        SafeFileHandle copyA = CopyOf(writeFd);
        SafeFileHandle copyB = CopyOf(writeFd);
        try
        {
            UnixChannelStdio.End([writeFd], stdinFd: -1);

            // EOF, every copy still open - and the read end, the same pipe's other end in this process, untouched.
            Assert.Equal(0, await ReadAsync(reader).WaitAsync(Patient, Ct));
            byte[] late = "x"u8.ToArray();
            Assert.Equal(1, (int)write((int)copyA.DangerousGetHandle(), ref late[0], 1));   // a late write goes nowhere, and does not fail
            Assert.Equal(0, await ReadAsync(reader).WaitAsync(Patient, Ct));
        }
        finally
        {
            copyA.Dispose();
            copyB.Dispose();
            writer.Dispose();
            reader.Dispose();
        }
    }

    /// <summary>A file stdin refers to as well (a terminal, a socket carrying both ways) is left alone.</summary>
    [Fact]
    public async Task A_descriptor_that_is_also_stdin_is_left_alone()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix descriptors.");
        (AnonymousPipeServerStream reader, int writeFd) = Pipe(out AnonymousPipeClientStream writer);
        try
        {
            UnixChannelStdio.End([writeFd], stdinFd: writeFd);

            byte[] still = "y"u8.ToArray();
            Assert.Equal(1, (int)write(writeFd, ref still[0], 1));
            byte[] buffer = new byte[1];
            Assert.Equal(1, await Task.Run(() => reader.Read(buffer, 0, 1)).WaitAsync(Patient, Ct));
            Assert.Equal((byte)'y', buffer[0]);
        }
        finally
        {
            writer.Dispose();
            reader.Dispose();
        }
    }

    /// <summary>
    /// Final round: a <c>struct stat</c> whose inode is 0 - a macOS TCP socket's, a kqueue's - names no file: two such
    /// descriptors would otherwise look like one, and one of them be sent to /dev/null for the other.
    /// </summary>
    [Fact]
    public void An_inode_of_0_is_no_identity()
    {
        byte[] stat = new byte[256];
        BitConverter.GetBytes(66UL).CopyTo(stat, 0);   // a device, inode 0

        Assert.Null(UnixChannelStdio.IdentityFrom(stat, statusFlags: 1, macOS: true));
        Assert.Null(UnixChannelStdio.IdentityFrom(stat, statusFlags: 1, macOS: false));
        BitConverter.GetBytes(4242UL).CopyTo(stat, 8);
        Assert.Equal(new UnixChannelStdio.FileIdentity(66, 4242, 1), UnixChannelStdio.IdentityFrom(stat, statusFlags: 0x8001, macOS: false));
    }

    /// <summary>
    /// Final round: the <c>struct stat</c> offsets read here hold for linux-x64, linux-arm64 and osx-arm64 only; osx-x64's
    /// plain <c>fstat</c> fills the legacy struct, whose offset 8 is no inode. Elsewhere the helper does nothing.
    /// </summary>
    [Theory]
    [InlineData(true, false, Architecture.X64, true)]
    [InlineData(true, false, Architecture.Arm64, true)]
    [InlineData(false, true, Architecture.Arm64, true)]
    [InlineData(false, true, Architecture.X64, false)]
    [InlineData(true, false, Architecture.X86, false)]
    [InlineData(true, false, Architecture.Arm, false)]
    [InlineData(false, false, Architecture.X64, false)]
    public void Only_known_stat_layouts_are_read(bool linux, bool macOS, Architecture architecture, bool known)
    {
        Assert.Equal(known, UnixChannelStdio.KnowsStatLayout(linux, macOS, architecture));
    }

    /// <summary>
    /// A copy of <paramref name="fd"/> made close-on-exec atomically (final round), so a parallel test's child process
    /// never inherits the pipe's write end and holds the reader off its EOF. Opening <c>/dev/fd/N</c> rather than
    /// <c>fcntl(F_DUPFD_CLOEXEC)</c>: that call's third argument is variadic, which Apple arm64 passes on the stack, where
    /// a plain P/Invoke would not put it. On Linux it opens the same pipe afresh; on macOS it is a <c>dup</c>; with the
    /// same access mode either way, which is all the helper matches on.
    /// </summary>
    private static SafeFileHandle CopyOf(int fd) =>
        File.OpenHandle($"/dev/fd/{fd.ToString(System.Globalization.CultureInfo.InvariantCulture)}", FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

    [DllImport("libc", SetLastError = true)]
    private static extern nint write(int fd, ref byte buffer, nuint count);
}
