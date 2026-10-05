using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Ntilde.Platform.Ssh.Native;

namespace Ntilde.Platform.Tests.Ssh.Exec;

/// <summary>
/// An exec-mode <see cref="INativeSshInterop"/> that plays back queued events and records every call:
/// the native layer as <see cref="Ntilde.Platform.Ssh.Exec.NativeSshExecTransport"/> sees it, with no
/// network and no native library.
/// </summary>
internal sealed class ScriptedNativeSshInterop : INativeSshInterop
{
    private readonly ConcurrentQueue<NativeSshEvent> _events = new();
    private readonly List<byte[]> _writes = [];
    private readonly List<(NativeSshResponseKind Kind, string PayloadJson)> _submissions = [];
    private int _execCalls;
    private int _polls;
    private int _dequeued;
    private int _sendEofCount;
    private int _closeCount;
    private Thread? _pollThread;

    /// <summary>A handle that owns nothing: disposing it marks it closed without calling the native close.</summary>
    public NovaSshSafeHandle Handle { get; } = new(new IntPtr(7), ownsHandle: false);

    /// <summary>Thrown from <see cref="Exec"/> instead of returning <see cref="Handle"/>.</summary>
    public Exception? ExecFailure { get; set; }

    /// <summary>Runs on every <see cref="SendEof"/>: a command that ends on stdin's EOF queues its exit here.</summary>
    public Action<ScriptedNativeSshInterop>? OnSendEof { get; set; }

    public NativeSshConnectionOptions? ExecOptions { get; private set; }
    public string? ExecCommand { get; private set; }
    public int ExecCalls => Volatile.Read(ref _execCalls);
    public int Polls => Volatile.Read(ref _polls);
    public int Dequeued => Volatile.Read(ref _dequeued);
    public int SendEofCount => Volatile.Read(ref _sendEofCount);
    public int CloseCount => Volatile.Read(ref _closeCount);
    public Thread? PollThread => Volatile.Read(ref _pollThread);

    public IReadOnlyList<byte[]> Writes
    {
        get { lock (_writes) return _writes.ToArray(); }
    }

    public IReadOnlyList<(NativeSshResponseKind Kind, string PayloadJson)> Submissions
    {
        get { lock (_submissions) return _submissions.ToArray(); }
    }

    public void Enqueue(params NativeSshEvent[] events)
    {
        foreach (NativeSshEvent next in events)
        {
            _events.Enqueue(next);
        }
    }

    public static NativeSshEvent Connected() =>
        new(NativeSshEventKind.Connected, """{"host":"example.com","port":2200,"user":"alice"}"""u8.ToArray(), flags: NativeSshEventFlags.Json);

    public static NativeSshEvent Stdout(string text) => NativeSshEvent.Data(Encoding.UTF8.GetBytes(text));

    public static NativeSshEvent Stderr(string text) => NativeSshEvent.ExtendedData(Encoding.UTF8.GetBytes(text));

    /// <summary>The worker's failure event, shaped as rusty_ssh queues it: <c>{"message":"…"}</c>, status -1.</summary>
    public static NativeSshEvent Error(string message)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("message", message);
            writer.WriteEndObject();
        }

        return new NativeSshEvent(NativeSshEventKind.Error, buffer.ToArray(), statusCode: -1, flags: NativeSshEventFlags.Json);
    }

    public static NativeSshEvent HostKeyPrompt() =>
        new(NativeSshEventKind.HostKeyPrompt,
            """{"host":"example.com","port":2200,"algorithm":"ssh-ed25519","fingerprint":"SHA256:test"}"""u8.ToArray(),
            flags: NativeSshEventFlags.Json);

    public static NativeSshEvent PasswordPrompt() =>
        new(NativeSshEventKind.PasswordPrompt, """{"prompt":"Password:"}"""u8.ToArray(), flags: NativeSshEventFlags.Json);

    public static NativeSshEvent Closed() => NativeSshEvent.Closed("""{"reason":"session-ended"}"""u8.ToArray());

    public NovaSshSafeHandle Exec(NativeSshConnectionOptions options, string command)
    {
        Interlocked.Increment(ref _execCalls);
        ExecOptions = options;
        ExecCommand = command;
        if (ExecFailure is not null)
        {
            throw ExecFailure;
        }

        return Handle;
    }

    public NativeSshEvent? PollEvent(NovaSshSafeHandle sessionHandle)
    {
        Volatile.Write(ref _pollThread, Thread.CurrentThread);
        Interlocked.Increment(ref _polls);

        // As the real interop: a closed handle has no events.
        if (sessionHandle.IsClosed || !_events.TryDequeue(out NativeSshEvent? next))
        {
            return null;
        }

        Interlocked.Increment(ref _dequeued);
        return next;
    }

    public void Write(NovaSshSafeHandle sessionHandle, ReadOnlySpan<byte> data)
    {
        lock (_writes)
        {
            _writes.Add(data.ToArray());
        }
    }

    public void SendEof(NovaSshSafeHandle sessionHandle)
    {
        Interlocked.Increment(ref _sendEofCount);
        OnSendEof?.Invoke(this);
    }

    public void SubmitResponse(NovaSshSafeHandle sessionHandle, NativeSshResponseKind responseKind, ReadOnlySpan<byte> data)
    {
        lock (_submissions)
        {
            _submissions.Add((responseKind, Encoding.UTF8.GetString(data)));
        }
    }

    public void Close(NovaSshSafeHandle sessionHandle)
    {
        Interlocked.Increment(ref _closeCount);
        sessionHandle.Dispose();
    }

    public NovaSshSafeHandle Connect(NativeSshConnectionOptions options) =>
        throw new NotSupportedException("An exec transport never opens a shell session.");

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
