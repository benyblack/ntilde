namespace Ntilde.Mux.Contracts;

/// <summary>
/// Protocol constants for the multiplexer wire (spec §6). Frame = <c>u8 kind</c>,
/// <c>u32 payloadLength</c> little-endian, payload.
/// </summary>
public static class MuxProtocol
{
    public const int MinSupportedVersion = 1;
    public const int MaxSupportedVersion = 1;

    /// <summary>Largest payload a frame may announce. A 10k-row 80x24 snapshot measured 12.9 MB in Phase 0.</summary>
    public const int MaxFrameBytes = 64 * 1024 * 1024;

    public const int FrameHeaderBytes = 5;

    /// <summary>
    /// Real range negotiation (client and daemon builds drift): the highest version both sides
    /// speak, or null when the ranges do not overlap or either is inverted.
    /// </summary>
    public static int? NegotiateVersion(int serverMin, int serverMax, int clientMin, int clientMax)
    {
        if (serverMin > serverMax || clientMin > clientMax) return null;
        int high = Math.Min(serverMax, clientMax);
        int low = Math.Max(serverMin, clientMin);
        return high >= low ? high : null;
    }
}

public static class MuxErrorCodes
{
    public const string VersionMismatch = "version_mismatch";
    public const string UnknownSession = "unknown_session";
    public const string SessionExited = "session_exited";
    public const string FrameTooLarge = "frame_too_large";
    public const string SnapshotTooLarge = "snapshot_too_large";
    public const string ClientTooSlow = "client_too_slow";
    public const string ProtocolError = "protocol_error";
    public const string SpawnFailed = "spawn_failed";
    public const string Internal = "internal_error";
}

public static class MuxMethods
{
    public const string Hello = "hello";
    public const string ListSessions = "listSessions";
    public const string Spawn = "spawn";
    public const string Attach = "attach";
    public const string Detach = "detach";
    public const string Kill = "kill";
    public const string Resize = "resize";
    public const string SessionInfo = "sessionInfo";
    public const string StartRecording = "startRecording";
    public const string StopRecording = "stopRecording";
    public const string EnableFlightRecording = "enableFlightRecording";
    public const string DisableFlightRecording = "disableFlightRecording";
    public const string ExportFlight = "exportFlight";
    public const string Ping = "ping";

    /// <summary>Notification (server → client).</summary>
    public const string Exited = "exited";
}

public enum MuxFrameKind : byte
{
    Request = 0x01,
    Response = 0x02,
    Notification = 0x03,
    Input = 0x10,
    Output = 0x11,
    ResizeEvent = 0x12,
    Snapshot = 0x13,
}

/// <summary>A protocol-level failure carrying one of <see cref="MuxErrorCodes"/>.</summary>
public sealed class MuxProtocolException : Exception
{
    public MuxProtocolException() : this(MuxErrorCodes.ProtocolError, "Multiplexer protocol error.") { }
    public MuxProtocolException(string message) : this(MuxErrorCodes.ProtocolError, message) { }
    public MuxProtocolException(string message, Exception innerException) : this(MuxErrorCodes.ProtocolError, message, innerException) { }
    public MuxProtocolException(string code, string message) : base(message) => Code = code;
    public MuxProtocolException(string code, string message, Exception innerException) : base(message, innerException) => Code = code;

    public string Code { get; }
}
