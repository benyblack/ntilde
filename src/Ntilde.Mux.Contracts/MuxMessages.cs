using System.Text.Json;

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
    public bool Faulted { get; init; }
}

public sealed record ListSessionsResult { public IReadOnlyList<SessionSummary> Sessions { get; init; } = []; }

public sealed record AttachParams
{
    public Guid SessionId { get; init; }
    public int MaxScrollbackRows { get; init; }
    public required MuxPresentation Presentation { get; init; }
}

public sealed record SessionIdParams { public Guid SessionId { get; init; } }

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
