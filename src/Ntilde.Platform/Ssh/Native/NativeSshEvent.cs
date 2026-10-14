namespace Ntilde.Platform.Ssh.Native;

[Flags]
public enum NativeSshEventFlags
{
    None = 0,
    Json = 1,
    Binary = 2
}

public enum NativeSshEventKind
{
    None = 0,
    Connected = 1,
    Data = 2,
    HostKeyPrompt = 3,
    PasswordPrompt = 4,
    PassphrasePrompt = 5,
    KeyboardInteractivePrompt = 6,

    /// <summary>
    /// The remote command's exit code, in StatusCode; queued before <see cref="Closed"/>. A shell
    /// session and an exec session (Phase 4 spec §8.3) both report it. Absent when the command died
    /// of a signal, so Closed without it means "no exit status".
    /// </summary>
    ExitStatus = 7,
    Error = 8,
    Closed = 9,
    ForwardChannelData = 10,
    ForwardChannelEof = 11,
    ForwardChannelClosed = 12,

    /// <summary>
    /// The server opened a forwarded-tcpip channel for a remote forward this session requested.
    /// StatusCode carries the channel id; the JSON payload names the listener the connection
    /// arrived on and its originator, so the forward session can match it to a rule and dial the
    /// local destination.
    /// </summary>
    ForwardChannelIncoming = 13,

    /// <summary>
    /// An exec session's stderr (Phase 4 spec §8.3): raw bytes, StatusCode 1 (the SSH extended-data
    /// type for stderr). Only exec sessions send it; a shell session's stderr arrives in
    /// <see cref="Data"/> through its PTY.
    /// </summary>
    ExtendedData = 14,

    /// <summary>
    /// An exec session's remote command closed its stdout and stderr: the channel's EOF (codex D1, residual R1).
    /// Empty. Queued after the data it follows; its <see cref="ExitStatus"/> and <see cref="Closed"/> come once the
    /// command has exited, which may be later. Only exec sessions send it.
    /// </summary>
    Eof = 15
}

public enum NativeSshResponseKind
{
    HostKeyDecision = 1,
    Password = 2,
    Passphrase = 3,
    KeyboardInteractive = 4
}

public sealed class NativeSshEvent
{
    public NativeSshEvent(
        NativeSshEventKind kind,
        byte[] payload,
        int statusCode = 0,
        NativeSshEventFlags flags = NativeSshEventFlags.None)
    {
        Kind = kind;
        Payload = payload ?? Array.Empty<byte>();
        StatusCode = statusCode;
        Flags = flags;
    }

    public NativeSshEventKind Kind { get; }
    public byte[] Payload { get; }
    public int StatusCode { get; }
    public NativeSshEventFlags Flags { get; }

    public static NativeSshEvent Data(byte[] payload) =>
        new(NativeSshEventKind.Data, payload, flags: NativeSshEventFlags.Binary);

    /// <summary>Stderr bytes from an exec session, as the native layer queues them (StatusCode 1, binary).</summary>
    public static NativeSshEvent ExtendedData(byte[] payload) =>
        new(NativeSshEventKind.ExtendedData, payload, 1, NativeSshEventFlags.Binary);

    public static NativeSshEvent ExitStatus(int statusCode, byte[]? payload = null) =>
        new(NativeSshEventKind.ExitStatus, payload ?? Array.Empty<byte>(), statusCode, NativeSshEventFlags.Json);

    public static NativeSshEvent Closed(byte[]? payload = null) =>
        new(NativeSshEventKind.Closed, payload ?? Array.Empty<byte>(), flags: NativeSshEventFlags.Json);

    public static NativeSshEvent ForwardChannelData(int channelId, byte[] payload) =>
        new(NativeSshEventKind.ForwardChannelData, payload, channelId, NativeSshEventFlags.Binary);

    public static NativeSshEvent ForwardChannelEof(int channelId) =>
        new(NativeSshEventKind.ForwardChannelEof, Array.Empty<byte>(), channelId, NativeSshEventFlags.Json);

    public static NativeSshEvent ForwardChannelClosed(int channelId) =>
        new(NativeSshEventKind.ForwardChannelClosed, Array.Empty<byte>(), channelId, NativeSshEventFlags.Json);

    public static NativeSshEvent ForwardChannelIncoming(int channelId, byte[] payload) =>
        new(NativeSshEventKind.ForwardChannelIncoming, payload, channelId, NativeSshEventFlags.Json);
}
