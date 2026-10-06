using System.Collections.Concurrent;
using System.Text;
using Ntilde.Platform.Ssh.Native;

namespace Ntilde.Tests.Shell.Mux.Remote;

/// <summary>
/// The native layer as the real <c>NativeSshExecTransport</c> sees it, for one exec session that asks
/// its prompts and then waits: it plays back queued prompt events, records every response submitted and
/// every close, and nothing else happens. What reaches the server is what is submitted here.
/// </summary>
internal sealed class PromptingNativeSshInterop : INativeSshInterop
{
    private readonly ConcurrentQueue<NativeSshEvent> _events = new();
    private readonly List<(NativeSshResponseKind Kind, string PayloadJson)> _submissions = [];
    private int _closes;

    public PromptingNativeSshInterop(params NativeSshEvent[] prompts)
    {
        foreach (NativeSshEvent prompt in prompts) _events.Enqueue(prompt);
    }

    public static NativeSshEvent PasswordPrompt { get; } =
        new(NativeSshEventKind.PasswordPrompt, """{"prompt":"Password:"}"""u8.ToArray(), flags: NativeSshEventFlags.Json);

    /// <summary>An encrypted key's passphrase: asked before the key can be offered, so nothing has reached the server yet.</summary>
    public static NativeSshEvent PassphrasePrompt { get; } =
        new(NativeSshEventKind.PassphrasePrompt, """{"prompt":"Enter passphrase for key '/home/nova/.ssh/id_ed25519':"}"""u8.ToArray(), flags: NativeSshEventFlags.Json);

    /// <summary>The native layer's failure, as it queues one before <see cref="NativeSshEvent.Closed"/>; <paramref name="message"/> has no quote or backslash.</summary>
    public static NativeSshEvent Error(string message) =>
        new(NativeSshEventKind.Error, Encoding.UTF8.GetBytes($$"""{"message":"{{message}}"}"""), flags: NativeSshEventFlags.Json);

    public IReadOnlyList<(NativeSshResponseKind Kind, string PayloadJson)> Submissions
    {
        get { lock (_submissions) return _submissions.ToArray(); }
    }

    public int Closes => Volatile.Read(ref _closes);

    // The public constructor's handle is "invalid" (0), so disposing it never calls the native close.
    public NovaSshSafeHandle Exec(NativeSshConnectionOptions options, string command) => new();

    public NativeSshEvent? PollEvent(NovaSshSafeHandle sessionHandle) =>
        !sessionHandle.IsClosed && _events.TryDequeue(out NativeSshEvent? next) ? next : null;

    public void SubmitResponse(NovaSshSafeHandle sessionHandle, NativeSshResponseKind responseKind, ReadOnlySpan<byte> data)
    {
        lock (_submissions) _submissions.Add((responseKind, Encoding.UTF8.GetString(data)));
    }

    public void Close(NovaSshSafeHandle sessionHandle)
    {
        Interlocked.Increment(ref _closes);
        sessionHandle.Dispose();
    }

    public void SendEof(NovaSshSafeHandle sessionHandle)
    {
    }

    public void Write(NovaSshSafeHandle sessionHandle, ReadOnlySpan<byte> data)
    {
    }

    public NovaSshSafeHandle Connect(NativeSshConnectionOptions options) => throw new NotSupportedException();

    public IReadOnlyList<NativeRemotePathEntry> ListRemoteDirectory(NativeSshConnectionOptions connectionOptions, string remotePath, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public void RunSftpTransfer(NativeSshConnectionOptions connectionOptions, NativeSftpTransferOptions transferOptions, Action<NativeSftpTransferProgress>? progress, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public void Resize(NovaSshSafeHandle sessionHandle, int cols, int rows) => throw new NotSupportedException();

    public int OpenDirectTcpIp(NovaSshSafeHandle sessionHandle, NativePortForwardOpenOptions options) => throw new NotSupportedException();

    public void WriteChannel(NovaSshSafeHandle sessionHandle, int channelId, ReadOnlySpan<byte> data) => throw new NotSupportedException();

    public void SendChannelEof(NovaSshSafeHandle sessionHandle, int channelId) => throw new NotSupportedException();

    public void CloseChannel(NovaSshSafeHandle sessionHandle, int channelId) => throw new NotSupportedException();
}
