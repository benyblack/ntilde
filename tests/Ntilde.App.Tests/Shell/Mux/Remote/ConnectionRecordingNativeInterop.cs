using Ntilde.Platform.Ssh.Native;

namespace Ntilde.Tests.Shell.Mux.Remote;

/// <summary>
/// The native layer for a persisted remote tab's own connections (Phase 5 spec R8): a remote listing and an SFTP transfer.
/// Records the connection options each was given, and counts the listings; a listing returns one file. Anything else throws.
/// </summary>
internal sealed class ConnectionRecordingNativeInterop : INativeSshInterop
{
    private int _listings;

    /// <summary>The options of the latest listing; null before the first.</summary>
    public NativeSshConnectionOptions? Listed { get; private set; }

    /// <summary>How many listings were asked for.</summary>
    public int Listings => Volatile.Read(ref _listings);

    /// <summary>The options of the latest transfer; null before the first.</summary>
    public NativeSshConnectionOptions? Transferred { get; private set; }

    public IReadOnlyList<NativeRemotePathEntry> ListRemoteDirectory(NativeSshConnectionOptions connectionOptions, string remotePath, CancellationToken cancellationToken)
    {
        Listed = connectionOptions;
        Interlocked.Increment(ref _listings);
        return [new NativeRemotePathEntry("notes.txt", "/home/nova/notes.txt", false)];
    }

    public void RunSftpTransfer(
        NativeSshConnectionOptions connectionOptions,
        NativeSftpTransferOptions transferOptions,
        Action<NativeSftpTransferProgress>? progress,
        CancellationToken cancellationToken) => Transferred = connectionOptions;

    public NovaSshSafeHandle Connect(NativeSshConnectionOptions options) => throw new NotSupportedException();
    public NativeSshEvent? PollEvent(NovaSshSafeHandle sessionHandle) => throw new NotSupportedException();
    public void Write(NovaSshSafeHandle sessionHandle, ReadOnlySpan<byte> data) => throw new NotSupportedException();
    public void Resize(NovaSshSafeHandle sessionHandle, int cols, int rows) => throw new NotSupportedException();
    public int OpenDirectTcpIp(NovaSshSafeHandle sessionHandle, NativePortForwardOpenOptions options) => throw new NotSupportedException();
    public void WriteChannel(NovaSshSafeHandle sessionHandle, int channelId, ReadOnlySpan<byte> data) => throw new NotSupportedException();
    public void SendChannelEof(NovaSshSafeHandle sessionHandle, int channelId) => throw new NotSupportedException();
    public void CloseChannel(NovaSshSafeHandle sessionHandle, int channelId) => throw new NotSupportedException();
    public void SubmitResponse(NovaSshSafeHandle sessionHandle, NativeSshResponseKind responseKind, ReadOnlySpan<byte> data) => throw new NotSupportedException();
    public void Close(NovaSshSafeHandle sessionHandle) => throw new NotSupportedException();
}
