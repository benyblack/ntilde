namespace Ntilde.CommandAssist.Storage;

/// <summary>
/// Serializes cooperating history stores across processes on a stable file name. The file is
/// never deleted or moved: on Unix a replacement inode would make two different locks.
/// </summary>
internal sealed class HistoryFileLease : IDisposable
{
    private readonly FileStream _stream;

    private HistoryFileLease(FileStream stream)
    {
        _stream = stream;
        if (stream.Length == 16)
        {
            Span<byte> bytes = stackalloc byte[16];
            stream.ReadExactly(bytes);
            Revision = new Guid(bytes);
        }
    }

    internal Guid Revision { get; private set; }

    internal static async Task<HistoryFileLease> AcquireAsync(string path, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileStream stream;
            try
            {
                // OpenOrCreate must not truncate before Unix takes its flock. None is
                // exclusive on Windows and Unix; other share modes are not an exclusive gate.
                stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
            }
            catch (IOException error) when (IsSharingViolation(error))
            {
                await Task.Delay(20, cancellationToken);
                continue;
            }
            try { return new HistoryFileLease(stream); }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
    }

    /// <summary>
    /// Invalidate peers BEFORE changing the log. A process dying between these steps causes
    /// a harmless reload, never a peer trusting a stale index after a successful data write.
    /// </summary>
    internal Guid PublishMutation()
    {
        Guid revision = Guid.NewGuid();
        Span<byte> bytes = stackalloc byte[16];
        revision.TryWriteBytes(bytes);
        _stream.Position = 0;
        _stream.Write(bytes);
        _stream.SetLength(bytes.Length);
        _stream.Flush();
        Revision = revision;
        return revision;
    }

    private static bool IsSharingViolation(IOException error)
    {
        // .NET reports Win32 sharing/lock violations on Windows and the native
        // EWOULDBLOCK errno on Unix: EAGAIN=11 on Linux, EWOULDBLOCK=35 on macOS.
        // Permanent I/O errors must surface instead of being retried indefinitely.
        return OperatingSystem.IsWindows()
            ? (error.HResult & 0xffff) is 32 or 33
            : error.HResult is 11 or 35;
    }

    public void Dispose() => _stream.Dispose();
}
