using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ntilde.Mux.Contracts;

public sealed record MuxRequest
{
    /// <summary>0 = no response wanted.</summary>
    public long Id { get; init; }
    public required string Method { get; init; }
    public JsonElement? Params { get; init; }
}

public sealed record MuxResponse
{
    /// <summary>0 with an <see cref="Error"/> = connection-level, sent just before the server closes.</summary>
    public long Id { get; init; }
    public JsonElement? Result { get; init; }
    public MuxError? Error { get; init; }
}

public sealed record MuxNotification
{
    public required string Method { get; init; }
    public JsonElement? Params { get; init; }
}

public sealed record MuxError
{
    public required string Code { get; init; }
    public required string Message { get; init; }
}

public sealed record MuxEmpty;

public sealed record HelloParams
{
    public int MinVersion { get; init; }
    public int MaxVersion { get; init; }
    public string ClientKind { get; init; } = string.Empty;

    /// <summary>
    /// Phase 4 (spec §2.5, additive; an older daemon ignores it): a stable id of the GUI instance.
    /// A hello carrying the same id as a live connection closes that connection first - after an SSH
    /// link drops, the daemon still holds the old half-open one with the GUI's sinks attached. Null
    /// never evicts; longer than 64 characters is ignored.
    /// </summary>
    public string? ClientInstanceId { get; init; }
}

public sealed record WelcomeResult
{
    public int Version { get; init; }
    public bool ForceConPtyFiltering { get; init; }
}

/// <summary>What the client looks like: applied to the mux parser so its replies describe it.</summary>
public sealed record MuxPresentation
{
    public int Cols { get; init; }
    public int Rows { get; init; }
    public float CellWidthPx { get; init; }
    public float CellHeightPx { get; init; }
    /// <summary>ARGB as <c>TermColor.ToUint()</c>; null = parser default.</summary>
    public uint? DefaultFg { get; init; }
    public uint? DefaultBg { get; init; }
    public bool KittyKeyboardEnabled { get; init; } = true;
}

/// <summary>The local fields of <c>TerminalSessionRequest</c>; SSH is not spawnable through the mux (spec §9.2).</summary>
public sealed record SpawnParams
{
    public required string Command { get; init; }
    public string Arguments { get; init; } = string.Empty;
    public string StartingDirectory { get; init; } = string.Empty;
    public int Cols { get; init; } = 80;
    public int Rows { get; init; } = 24;
    public IReadOnlyDictionary<string, string>? EnvironmentOverrides { get; init; }
    public bool SkipPowerShellPostLaunchInit { get; init; }
    public string Title { get; init; } = string.Empty;
}

public sealed record SpawnResult { public Guid SessionId { get; init; } }

public sealed record SessionSummary
{
    public Guid SessionId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Command { get; init; } = string.Empty;
    public string? Arguments { get; init; }
    public int Cols { get; init; }
    public int Rows { get; init; }
    public bool Running { get; init; }
    public int? ExitCode { get; init; }
    public int AttachedClients { get; init; }

    /// <summary>
    /// <see cref="AttachedClients"/> without read-only observers (v2). Omitted when 0, so a v1 peer sees
    /// exactly the v1 shape; a reader tells "0" from "not sent" by the negotiated version.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int InteractiveClients { get; init; }

    public bool Faulted { get; init; }

    /// <summary>The last OSC 7 directory the mux parser saw; null when none (or a v1 daemon).</summary>
    public string? Cwd { get; init; }

    /// <summary>The detach that left the session with no subscribers was a user detach; cleared by the next attach. Startup adoption skips these.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] // only when true: a v1 peer sees exactly the v1 shape
    public bool DetachedByUser { get; init; }
}

public sealed record ListSessionsResult { public IReadOnlyList<SessionSummary> Sessions { get; init; } = []; }

public sealed record AttachParams
{
    public Guid SessionId { get; init; }
    public int MaxScrollbackRows { get; init; }
    public required MuxPresentation Presentation { get; init; }

    /// <summary>
    /// <see cref="MuxAttachModes"/> wire string; null (absent) = shared, the v1 shape. A string, not a
    /// JSON enum: an unknown value then gets a request-level error rather than a malformed-params close.
    /// </summary>
    public string? Mode { get; init; }
}

public sealed record SessionIdParams { public Guid SessionId { get; init; } }

/// <summary>
/// Params of <c>detach</c>: <see cref="SessionIdParams"/> plus an optional, additive field (the
/// wire shape without it is exactly the old one).
/// </summary>
public sealed record DetachParams
{
    public Guid SessionId { get; init; }

    /// <summary>
    /// Set when the detach undoes one specific attach (the caller gave up on it, or refused its
    /// snapshot): the server ignores the detach if this connection has since sent a newer attach
    /// for the session, because that attach already reset the subscription and a detach landing
    /// after it would silently remove the newer one. Null = detach unconditionally.
    /// </summary>
    public long? AttachRequestId { get; init; }

    /// <summary>
    /// True when the user detached on purpose ("Pane: Detach", or Detach in the shared-close prompt).
    /// Null (absent) = an ordinary detach: the v1 shape. Clients send it only on v2 (spec §7.7).
    /// </summary>
    public bool? UserDetached { get; init; }
}

public sealed record ResizeParams
{
    public Guid SessionId { get; init; }
    public int Cols { get; init; }
    public int Rows { get; init; }
    public MuxPresentation? Presentation { get; init; }
}

public sealed record SessionInfoResult
{
    public bool Running { get; init; }
    public int? ExitCode { get; init; }
    public bool HasActiveChildProcesses { get; init; }
    public int? Pid { get; init; }
    public string? Title { get; init; }
    public string? Cwd { get; init; }
    public int? AttachedClients { get; init; }

    /// <summary>
    /// <see cref="AttachedClients"/> without read-only observers (Phase 4 spec §3, carry-over 9). The
    /// server fills it for a v2 peer only and null is never written, so a v1 peer sees the v1 shape.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? InteractiveClients { get; init; }
}

public sealed record StartRecordingParams
{
    public Guid SessionId { get; init; }
    public required string Path { get; init; }
}

public sealed record EnableFlightRecordingParams
{
    public Guid SessionId { get; init; }
    public long MaxBytes { get; init; }
}

/// <summary>Bytes, not a path, so a remote daemon (Phase 4) needs no change.</summary>
public sealed record ExportFlightResult
{
    public bool Exported { get; init; }
    public byte[]? Bytes { get; init; }
    public int EventCount { get; init; }
    public long FirstEventMs { get; init; }
    public long LastEventMs { get; init; }
    public bool TruncatedAtStart { get; init; }
}

public sealed record ExitedNotification
{
    public Guid SessionId { get; init; }
    public int ExitCode { get; init; }
}

/// <summary>Params of <see cref="MuxMethods.Faulted"/>.</summary>
public sealed record FaultedNotification
{
    public Guid SessionId { get; init; }

    /// <summary>Human-readable reason, for logs and the pane's banner; not machine-parsed. May be absent.</summary>
    public string? Message { get; init; }
}

/// <summary>Params of <see cref="MuxMethods.SessionChanged"/>: the session's facts after the change.</summary>
public sealed record SessionChangedNotification
{
    public Guid SessionId { get; init; }
    public int AttachedClients { get; init; }

    /// <summary>
    /// <see cref="AttachedClients"/> without read-only observers (Phase 4 spec §3, carry-over 9). 0 is
    /// written; null is not, so null on receipt means an older daemon that does not send it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? InteractiveClients { get; init; }

    public string Title { get; init; } = string.Empty;
    public string? Cwd { get; init; }
}

/// <summary>Params of <see cref="MuxMethods.Killed"/>. <see cref="ByClientKind"/> is the killer's hello <c>clientKind</c>.</summary>
public sealed record KilledNotification
{
    public Guid SessionId { get; init; }
    public string ByClientKind { get; init; } = string.Empty;
}

/// <summary>
/// <c>mux/mux-endpoint.json</c> (spec §3): how a client finds a running daemon, and enough to tell a
/// live daemon from a recycled pid (<see cref="ProcessName"/>).
/// </summary>
public sealed record MuxEndpointDescriptor
{
    public int MinVersion { get; init; }
    public int MaxVersion { get; init; }
    /// <summary>Pipe name (Windows) or absolute socket path (Linux/macOS).</summary>
    public required string Endpoint { get; init; }
    public int Pid { get; init; }
    public required string ProcessName { get; init; }
    /// <summary>
    /// OS-specific start token from MuxDiscovery.GetProcessStartToken: /proc starttime clock ticks on
    /// Linux, UTC ticks elsewhere; only ever compared with a token taken on the same host. A recycled
    /// pid can carry the same process name (another ntilde, say), so the name alone cannot prove the
    /// process is still this daemon; the token can. Absent from descriptors written by older daemons.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? StartTime { get; init; }
}
