using System.IO.Pipes;
using System.Runtime.InteropServices;
using Ntilde.Mux.Cli;

namespace Ntilde.Mux.Tests.Cli;

/// <summary>
/// <see cref="UnixChannelStdio"/> (codex D1, residual R1), Unix only: a pipe stands in for the exec channel's stdout,
/// its write end for fd 1, and <c>dup</c>s of it for the copies .NET's console streams hold. sshd sends the channel's
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
        int copy = dup(writeFd);
        try
        {
            Assert.True(copy >= 0);
            reader.ClientSafePipeHandle.Dispose();   // our handle to the client end; the writer stream's is the same
            writer.Dispose();

            Task<int> read = ReadAsync(reader);
            await Task.Delay(300, Ct);

            Assert.False(read.IsCompleted, "the reader saw EOF although a copy of the write end is open");
        }
        finally
        {
            _ = close(copy);   // now the reader's EOF comes, and its task ends
            reader.Dispose();
        }
    }

    [Fact]
    public async Task Every_copy_of_a_channel_descriptor_goes_to_dev_null_so_its_reader_sees_EOF()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix descriptors.");
        (AnonymousPipeServerStream reader, int writeFd) = Pipe(out AnonymousPipeClientStream writer);
        int copyA = dup(writeFd);
        int copyB = dup(writeFd);
        try
        {
            Assert.True(copyA >= 0 && copyB >= 0);

            UnixChannelStdio.End([writeFd], stdinFd: -1);

            // EOF, every copy still open - and the read end, the same pipe's other end in this process, untouched.
            Assert.Equal(0, await ReadAsync(reader).WaitAsync(Patient, Ct));
            byte[] late = "x"u8.ToArray();
            Assert.Equal(1, (int)write(copyA, ref late[0], 1));                 // a late write goes nowhere, and does not fail
            Assert.Equal(0, await ReadAsync(reader).WaitAsync(Patient, Ct));
        }
        finally
        {
            _ = close(copyA);
            _ = close(copyB);
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

    [DllImport("libc", SetLastError = true)]
    private static extern int dup(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern nint write(int fd, ref byte buffer, nuint count);
}
