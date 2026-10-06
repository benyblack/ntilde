using System.IO.Pipes;
using System.Text;
using System.Text.RegularExpressions;
using Ntilde.Mux.Transport;

namespace Ntilde.Mux.Tests.Transport;

/// <summary>
/// The client half of <c>ntilde-mux proxy --stdio</c> (Phase 4 spec §8.1): the preamble scan over an
/// exec channel's stdout, which rc files and the MOTD may have written to first, and the duplex stream
/// it hands to <see cref="MuxClient"/>. Real anonymous pipes stand in wherever a real EOF matters.
/// </summary>
public sealed class StdioMuxTransportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Far longer than any of these tests needs: only the bound or an EOF ends their scans.</summary>
    private static readonly TimeSpan Patient = TimeSpan.FromSeconds(30);

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    /// <summary>A remote stdout that carries these bytes and then ends.</summary>
    private static MemoryStream RemoteStdout(params byte[][] parts) => new(parts.SelectMany(p => p).ToArray());

    /// <summary>Reads to EOF three bytes at a time, so the leftover is drained across several reads.</summary>
    private static byte[] ReadToEnd(Stream s)
    {
        var all = new List<byte>();
        byte[] buffer = new byte[3];
        int n;
        while ((n = s.Read(buffer)) > 0) all.AddRange(buffer.AsSpan(0, n).ToArray());
        return all.ToArray();
    }

    [Fact]
    public void The_preamble_is_one_ascii_line_with_the_version_and_the_pid()
    {
        Assert.Equal("NTILDE-MUX-PROXY 1 4242\n", Encoding.ASCII.GetString(MuxProxyPreamble.Format(4242)));
    }

    [Fact]
    public async Task Preamble_after_noise_is_found_and_leftover_bytes_reach_the_client()
    {
        byte[] firstFrameBytes = [0x01, 0x05, 0x00, 0x00, 0x00, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o', (byte)'\n', 0x00];
        using MemoryStream stdout = RemoteStdout(
            Utf8("Welcome to host\r\nlast login…\n"), MuxProxyPreamble.Format(4242), firstFrameBytes);

        StdioMuxConnection connection = await StdioMuxTransport.ConnectAsync(stdout, new MemoryStream(), Patient, Ct);

        Assert.Equal(4242, connection.DaemonPid);
        Assert.Equal(firstFrameBytes, ReadToEnd(connection.Stream));
    }

    [Fact]
    public async Task A_preamble_split_across_reads_and_ending_in_CRLF_is_found()
    {
        using var stdout = new TrickleStream(RemoteStdout(Utf8("motd\nNTILDE-MUX-PROXY 1 77\r\n"), [9, 8]));

        StdioMuxConnection connection = await StdioMuxTransport.ConnectAsync(stdout, new MemoryStream(), Patient, Ct);

        Assert.Equal(77, connection.DaemonPid);
        Assert.Equal(new byte[] { 9, 8 }, ReadToEnd(connection.Stream));
    }

    [Fact]
    public async Task No_preamble_before_EOF_throws_with_the_captured_text()
    {
        using MemoryStream stdout = RemoteStdout(Utf8("bash: ntilde-mux: not found\n"));

        var ex = await Assert.ThrowsAsync<MuxProxyHandshakeException>(
            () => StdioMuxTransport.ConnectAsync(stdout, new MemoryStream(), Patient, Ct));

        Assert.Contains("bash: ntilde-mux: not found", ex.Message, StringComparison.Ordinal);
        Assert.Contains("bash: ntilde-mux: not found", ex.CapturedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stream_disposed_mid_scan_throws_the_handshake_exception_with_the_captured_text()
    {
        using var stdout = new DisposedAfterStream(Utf8("bash: connection noise\n"));

        var ex = await Assert.ThrowsAsync<MuxProxyHandshakeException>(
            () => StdioMuxTransport.ConnectAsync(stdout, new MemoryStream(), Patient, Ct));

        Assert.IsType<ObjectDisposedException>(ex.InnerException);
        Assert.Contains("bash: connection noise", ex.CapturedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task More_than_64KiB_of_noise_throws()
    {
        // The remote stdout stays open: only the bound can end this scan.
        (Stream remote, Stream local) = InMemoryDuplexPipe.Create(256 * 1024);
        using (remote)
        using (local)
        {
            const string Line = "a motd line that the remote shell keeps printing\n";
            byte[] noise = Utf8(string.Concat(Enumerable.Repeat(Line, (MuxProxyPreamble.MaxScanBytes / Line.Length) + 100)));
            remote.Write(noise);

            var ex = await Assert.ThrowsAsync<MuxProxyHandshakeException>(
                () => StdioMuxTransport.ConnectAsync(local, Stream.Null, Patient, Ct));

            Assert.Contains("64 KiB", ex.Message, StringComparison.Ordinal);
            // The last 2 KiB at most: about 2048 / 50 lines, never all of them. (The scan stops at a
            // read's end, so the tail may end mid-line.)
            Assert.InRange(Regex.Count(ex.CapturedText, "motd"), 1, (2048 / Line.Length) + 1);
            Assert.Contains(@"printing\x0A", ex.CapturedText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_preamble_in_the_middle_of_a_line_is_not_accepted()
    {
        using MemoryStream midLine = RemoteStdout(Utf8("echo says: "), MuxProxyPreamble.Format(7), Utf8("and then EOF\n"));

        var ex = await Assert.ThrowsAsync<MuxProxyHandshakeException>(
            () => StdioMuxTransport.ConnectAsync(midLine, new MemoryStream(), Patient, Ct));

        Assert.Contains("echo says: NTILDE-MUX-PROXY 1 7", ex.CapturedText, StringComparison.Ordinal);

        // The same line, then the real preamble at the start of the next line.
        using MemoryStream thenReal = RemoteStdout(Utf8("echo says: "), MuxProxyPreamble.Format(7), MuxProxyPreamble.Format(8));
        Assert.Equal(8, (await StdioMuxTransport.ConnectAsync(thenReal, new MemoryStream(), Patient, Ct)).DaemonPid);
    }

    [Fact]
    public async Task Another_proxy_version_is_refused()
    {
        using MemoryStream stdout = RemoteStdout(Utf8("NTILDE-MUX-PROXY 2 99\n"));

        var ex = await Assert.ThrowsAsync<MuxProxyHandshakeException>(
            () => StdioMuxTransport.ConnectAsync(stdout, new MemoryStream(), Patient, Ct));

        Assert.Contains("unsupported proxy version", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_preamble_within_the_timeout_throws_with_the_captured_text()
    {
        // A prompt that waits for an answer nobody gives: the stream neither ends nor grows.
        (Stream remote, Stream local) = InMemoryDuplexPipe.Create(4096);
        using (remote)
        using (local)
        {
            remote.Write(Utf8("Password: "));

            var ex = await Assert.ThrowsAsync<MuxProxyHandshakeException>(
                () => StdioMuxTransport.ConnectAsync(local, Stream.Null, TimeSpan.FromMilliseconds(200), Ct));

            Assert.Contains("Password: ", ex.CapturedText, StringComparison.Ordinal);
            Assert.Contains("within", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Cancelling_is_a_cancellation_not_a_handshake_failure()
    {
        (Stream remote, Stream local) = InMemoryDuplexPipe.Create(4096);
        using (remote)
        using (local)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            Task<StdioMuxConnection> connecting = StdioMuxTransport.ConnectAsync(local, Stream.Null, Patient, cts.Token);
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting);
        }
    }

    [Fact]
    public async Task Disposing_the_stream_closes_stdin_first()
    {
        var order = new List<string>();
        using var stdinPipe = new AnonymousPipeServerStream(PipeDirection.Out);
        using var proxyReadsStdin = new AnonymousPipeClientStream(PipeDirection.In, stdinPipe.ClientSafePipeHandle);
        var stdin = new ObservedStream(stdinPipe, "stdin", order);
        var stdout = new ObservedStream(RemoteStdout(MuxProxyPreamble.Format(1)), "stdout", order);
        StdioMuxConnection connection = await StdioMuxTransport.ConnectAsync(stdout, stdin, Patient, Ct);
        connection.Stream.Write("x"u8);
        connection.Stream.Flush();

        connection.Stream.Dispose();

        Assert.Equal(["stdin", "stdout"], order);
        byte[] got = new byte[8];
        Assert.Equal(1, proxyReadsStdin.Read(got));   // what was written before
        Assert.Equal(0, proxyReadsStdin.Read(got));   // then a real EOF: the proxy's stdin has ended
    }

    [Fact]
    public async Task Remote_stdout_EOF_ends_the_stream_after_the_leftover()
    {
        using var stdoutPipe = new AnonymousPipeServerStream(PipeDirection.In);
        var proxyWritesStdout = new AnonymousPipeClientStream(PipeDirection.Out, stdoutPipe.ClientSafePipeHandle);
        proxyWritesStdout.Write([.. MuxProxyPreamble.Format(5), 1, 2, 3]);
        proxyWritesStdout.Flush();
        StdioMuxConnection connection = await StdioMuxTransport.ConnectAsync(stdoutPipe, Stream.Null, Patient, Ct);

        proxyWritesStdout.Dispose();   // the proxy exited

        Assert.Equal(new byte[] { 1, 2, 3 }, ReadToEnd(connection.Stream));
        connection.Stream.Dispose();
    }

    /// <summary>
    /// MuxClient never flushes, and a process's stdin can be buffered (a FileStream on Windows): a
    /// frame must not sit in that buffer.
    /// </summary>
    [Fact]
    public async Task Writes_reach_a_buffered_stdin_without_an_explicit_flush()
    {
        var reachedStdin = new MemoryStream();
        StdioMuxConnection connection = await StdioMuxTransport.ConnectAsync(
            RemoteStdout(MuxProxyPreamble.Format(1)), new BufferedStream(reachedStdin, 4096), Patient, Ct);

        connection.Stream.Write([1, 2, 3]);
        await connection.Stream.WriteAsync(new byte[] { 4 }, Ct);

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, reachedStdin.ToArray());
    }

    [Fact]
    public void The_duplex_stream_reads_and_writes_but_does_not_seek()
    {
        using var stream = new DuplexStdioStream([], Stream.Null, Stream.Null);
        Assert.True(stream.CanRead);
        Assert.True(stream.CanWrite);
        Assert.False(stream.CanSeek);
    }

    [Fact]
    public void Escape_keeps_printable_text_and_escapes_control_characters()
    {
        // UTF-8 is decoded (the ellipsis survives); ESC, CR and LF become \xNN.
        Assert.Equal("a\\x1B[31mb\\x0D\\x0Ac…", StdioMuxTransport.Escape(Utf8("a\u001b[31mb\r\nc…")));
    }

    [Fact]
    public void Escape_keeps_only_the_last_2KiB()
    {
        string escaped = StdioMuxTransport.Escape(Utf8(new string('a', 3000) + "TAIL"));

        Assert.Equal(2048, escaped.Length);
        Assert.EndsWith("TAIL", escaped, StringComparison.Ordinal);
    }

    /// <summary>Returns its bytes once, then throws <see cref="ObjectDisposedException"/>: a channel closed under a pending read.</summary>
    private sealed class DisposedAfterStream(byte[] noise) : Stream
    {
        private bool _served;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(_served, this); // "disposed" once its noise has been read
            _served = true;
            noise.CopyTo(buffer, offset);
            return noise.Length;
        }
    }

    /// <summary>Returns at most one byte per read: a preamble that arrives a byte at a time.</summary>
    private sealed class TrickleStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, 1));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Passes everything through to <paramref name="inner"/> and records when it is disposed.</summary>
    private sealed class ObservedStream(Stream inner, string name, List<string> disposals) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                disposals.Add(name);
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
