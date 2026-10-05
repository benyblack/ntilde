using System.Globalization;
using System.Text;

namespace Ntilde.Mux.Transport;

/// <summary>
/// The line <c>ntilde-mux proxy --stdio</c> writes to its stdout before the first frame (Phase 4 spec
/// §8.1): <c>NTILDE-MUX-PROXY 1 &lt;daemonPid&gt;\n</c>, in ASCII. Whatever an exec channel's stdout
/// carries before that line (rc files, the MOTD) is noise that <see cref="StdioMuxTransport"/> skips.
/// </summary>
public static class MuxProxyPreamble
{
    public const string Prefix = "NTILDE-MUX-PROXY";

    /// <summary>The preamble's own version: the proxy is a byte pump, so this changes only if the line does.</summary>
    public const int Version = 1;

    /// <summary>How much stdout the client scans for the preamble before it gives up.</summary>
    public const int MaxScanBytes = 64 * 1024;

    public static byte[] Format(int daemonPid) =>
        Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{Prefix} {Version} {daemonPid}\n"));
}

/// <summary>
/// The remote side never greeted (Phase 4 spec §8.1): its output ended, ran past
/// <see cref="MuxProxyPreamble.MaxScanBytes"/> or the timeout without the preamble, or the preamble
/// named another version. <see cref="CapturedText"/> is what it printed instead (control characters
/// escaped, the last 2 KiB at most), so the user sees why - <c>bash: ntilde-mux: not found</c>, say.
/// </summary>
public sealed class MuxProxyHandshakeException : IOException
{
    public MuxProxyHandshakeException(string reason, string capturedText, Exception? inner = null)
        : base(Describe(reason, capturedText), inner)
    {
        CapturedText = capturedText;
    }

    /// <summary>What the remote side printed, escaped by <see cref="StdioMuxTransport.Escape"/>.</summary>
    public string CapturedText { get; }

    private static string Describe(string reason, string capturedText) =>
        capturedText.Length == 0 ? $"{reason}; the remote side printed nothing." : $"{reason}; the remote side printed: {capturedText}";
}

/// <summary>A greeted proxy: the stream to hand <see cref="MuxClient.ConnectAsync"/>, and the daemon's pid from the preamble.</summary>
public sealed record StdioMuxConnection(Stream Stream, int DaemonPid);

/// <summary>
/// The GUI's side of <c>ntilde-mux proxy --stdio</c> (Phase 4 spec §8.1): finds the proxy's preamble
/// in an exec channel's stdout and returns a duplex stream over that channel. The protocol itself is
/// <see cref="MuxClient"/>'s, spoken through the proxy to the remote daemon.
/// </summary>
public static class StdioMuxTransport
{
    /// <summary>The scan reads at most this much at a time.</summary>
    private const int ChunkBytes = 4096;

    /// <summary>How much of the captured output a failure carries.</summary>
    private const int CapturedTailBytes = 2048;

    private static ReadOnlySpan<byte> LinePrefix => "NTILDE-MUX-PROXY "u8;

    /// <summary>
    /// Reads <paramref name="remoteStdout"/> until a line <c>NTILDE-MUX-PROXY 1 &lt;pid&gt;</c> starts
    /// at the beginning of the output or after a <c>\n</c> (a <c>\r</c> before the <c>\n</c> is
    /// tolerated), discarding everything before it. The bytes after that line are the first frames:
    /// the returned stream reads them first.
    /// </summary>
    /// <remarks>
    /// On success the returned stream owns both streams. On failure they stay the caller's, who owns
    /// the channel (and its stderr and exit status); a read the timeout abandoned may still be pending
    /// on <paramref name="remoteStdout"/> until the caller closes it.
    /// </remarks>
    /// <param name="remoteStdout">The exec channel's stdout: the proxy's stdout.</param>
    /// <param name="remoteStdin">The exec channel's stdin: the proxy's stdin.</param>
    /// <param name="preambleTimeout">How long the whole scan may take (<see cref="Timeout.InfiniteTimeSpan"/> for no limit).</param>
    /// <exception cref="MuxProxyHandshakeException">No preamble: EOF, the 64 KiB bound, the timeout, or another version.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async Task<StdioMuxConnection> ConnectAsync(Stream remoteStdout, Stream remoteStdin, TimeSpan preambleTimeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(remoteStdout);
        ArgumentNullException.ThrowIfNull(remoteStdin);
        if (preambleTimeout <= TimeSpan.Zero && preambleTimeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(preambleTimeout), preambleTimeout, "The preamble timeout must be positive or infinite.");
        }

        using var timeout = new CancellationTokenSource(preambleTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        byte[] buffer = new byte[ChunkBytes];
        int length = 0;
        int lineStart = 0; // the first line not yet complete: every line before it was examined
        while (true)
        {
            // The scan stops past MaxScanBytes, so the buffer never needs more than that plus one chunk.
            if (buffer.Length - length < ChunkBytes) Array.Resize(ref buffer, Math.Min(buffer.Length * 2, MuxProxyPreamble.MaxScanBytes + ChunkBytes));

            // Bounded by the deadline even when the stream ignores cancellation (a synchronous pipe's
            // ReadAsync does): such a read is abandoned, and its fault, if it ever faults, observed.
            int read;
            Task<int>? pending = null;
            try
            {
                pending = remoteStdout.ReadAsync(buffer.AsMemory(length, ChunkBytes), linked.Token).AsTask();
                read = await pending.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                Observe(pending);
                throw new MuxProxyHandshakeException(
                    string.Create(CultureInfo.InvariantCulture, $"No ntilde-mux proxy greeting within {preambleTimeout.TotalSeconds:0.###} s"),
                    Escape(buffer.AsSpan(0, length)));
            }
            catch (OperationCanceledException)
            {
                Observe(pending);
                throw;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                throw new MuxProxyHandshakeException($"Reading the remote output failed ({ex.Message})", Escape(buffer.AsSpan(0, length)), ex);
            }

            if (read == 0)
            {
                throw new MuxProxyHandshakeException("The remote command ended without the ntilde-mux proxy's greeting", Escape(buffer.AsSpan(0, length)));
            }

            length += read;
            switch (Scan(buffer.AsSpan(0, length), ref lineStart, out int version, out int pid, out int frameStart))
            {
                case ScanResult.Found:
                    byte[] leftover = buffer.AsSpan(frameStart, length - frameStart).ToArray();
                    return new StdioMuxConnection(new DuplexStdioStream(leftover, remoteStdout, remoteStdin), pid);
                case ScanResult.OtherVersion:
                    throw new MuxProxyHandshakeException(
                        string.Create(CultureInfo.InvariantCulture, $"The ntilde-mux proxy speaks an unsupported proxy version {version} (this client speaks {MuxProxyPreamble.Version})"),
                        Escape(buffer.AsSpan(0, length)));
            }

            if (length > MuxProxyPreamble.MaxScanBytes)
            {
                throw new MuxProxyHandshakeException(
                    $"No ntilde-mux proxy greeting in the first {MuxProxyPreamble.MaxScanBytes / 1024} KiB of the remote output", Escape(buffer.AsSpan(0, length)));
            }
        }
    }

    /// <summary>An abandoned read may still fault when the caller closes the stream: observed, so it is not an unobserved task exception.</summary>
    private static void Observe(Task<int>? read) =>
        _ = read?.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private enum ScanResult
    {
        NeedMore,
        Found,
        OtherVersion,
    }

    /// <summary>
    /// Examines every complete line from <paramref name="lineStart"/> on, and moves it past each one
    /// that is not the preamble. A line that merely starts with the prefix but is not
    /// <c>&lt;prefix&gt; &lt;digits&gt; &lt;digits&gt;</c> is noise like any other.
    /// </summary>
    private static ScanResult Scan(ReadOnlySpan<byte> data, ref int lineStart, out int version, out int pid, out int frameStart)
    {
        version = 0;
        pid = 0;
        frameStart = 0;
        while (true)
        {
            int newline = data[lineStart..].IndexOf((byte)'\n');
            if (newline < 0) return ScanResult.NeedMore;

            int next = lineStart + newline + 1;
            ReadOnlySpan<byte> line = data[lineStart..(next - 1)];
            if (!line.IsEmpty && line[^1] == (byte)'\r') line = line[..^1];
            lineStart = next;
            if (!line.StartsWith(LinePrefix) || !TryParseFields(line[LinePrefix.Length..], out version, out pid)) continue;

            frameStart = next;
            return version == MuxProxyPreamble.Version ? ScanResult.Found : ScanResult.OtherVersion;
        }
    }

    /// <summary><c>&lt;version&gt; &lt;pid&gt;</c>: two runs of ASCII digits and one space.</summary>
    private static bool TryParseFields(ReadOnlySpan<byte> fields, out int version, out int pid)
    {
        pid = 0;
        version = 0;
        int space = fields.IndexOf((byte)' ');
        return space > 0
            && int.TryParse(fields[..space], NumberStyles.None, CultureInfo.InvariantCulture, out version)
            && int.TryParse(fields[(space + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out pid);
    }

    /// <summary>
    /// What a failed scan captured, made safe to show: the last 2 KiB (never starting inside a UTF-8
    /// sequence), decoded as UTF-8, with every control character - line breaks and escape sequences
    /// included - written as <c>\xNN</c>.
    /// </summary>
    internal static string Escape(ReadOnlySpan<byte> captured)
    {
        if (captured.Length > CapturedTailBytes)
        {
            captured = captured[^CapturedTailBytes..];
            int skip = 0;
            while (skip < 3 && skip < captured.Length && (captured[skip] & 0xC0) == 0x80) skip++;
            captured = captured[skip..];
        }

        string text = Encoding.UTF8.GetString(captured);
        var escaped = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsControl(c)) escaped.Append("\\x").Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
            else escaped.Append(c);
        }

        return escaped.ToString();
    }
}

/// <summary>
/// The exec channel as one stream (Phase 4 spec §8.1): reads drain the bytes that followed the
/// preamble, then the remote stdout; writes go to the remote stdin. Reads come from one thread at a
/// time (<see cref="MuxClient"/>'s reader), as with any stream.
/// </summary>
internal sealed class DuplexStdioStream : Stream
{
    private readonly Stream _stdout;
    private readonly Stream _stdin;
    private byte[] _leftover;
    private int _leftoverOffset;
    private int _disposed;

    public DuplexStdioStream(byte[] leftover, Stream remoteStdout, Stream remoteStdin)
    {
        _leftover = leftover;
        _stdout = remoteStdout;
        _stdin = remoteStdin;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        int n = TakeLeftover(buffer);
        return n > 0 ? n : _stdout.Read(buffer);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int n = TakeLeftover(buffer.Span);
        return n > 0 ? ValueTask.FromResult(n) : _stdout.ReadAsync(buffer, cancellationToken);
    }

    private int TakeLeftover(Span<byte> destination)
    {
        int available = _leftover.Length - _leftoverOffset;
        if (available == 0 || destination.IsEmpty) return 0;
        int n = Math.Min(available, destination.Length);
        _leftover.AsSpan(_leftoverOffset, n).CopyTo(destination);
        _leftoverOffset += n;
        if (_leftoverOffset == _leftover.Length)
        {
            _leftover = [];
            _leftoverOffset = 0;
        }

        return n;
    }

    /// <summary>
    /// Every write is flushed: <see cref="MuxClient"/> never flushes, and a process's stdin can be
    /// buffered (a FileStream on Windows), which would hold a frame back indefinitely.
    /// </summary>
    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        Write(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _stdin.Write(buffer);
        _stdin.Flush();
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _stdin.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        await _stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public override void Flush() => _stdin.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _stdin.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>
    /// Stdin first: its EOF is what makes the proxy exit, which closes the channel. Then stdout. A
    /// synchronous pipe's read end does not unblock a pending read when disposed; the proxy's exit
    /// (its stdout's EOF) is what ends that read.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            CloseQuietly(_stdin);
            CloseQuietly(_stdout);
        }

        base.Dispose(disposing);
    }

    private static void CloseQuietly(Stream stream)
    {
        try { stream.Dispose(); }
        catch (IOException) { /* flushing to a channel already gone: closing it is all that was left */ }
    }
}
