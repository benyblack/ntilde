using Ntilde.Mux.Contracts;

namespace Ntilde.Mux;

public sealed class MuxClientOptions
{
    public int MinProtocolVersion { get; init; } = MuxProtocol.MinSupportedVersion;
    public int MaxProtocolVersion { get; init; } = MuxProtocol.MaxSupportedVersion;
    public string ClientKind { get; init; } = "ntilde";
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public MuxAttachLimits AttachLimits { get; init; } = new();
    public Action<string>? Log { get; init; }
}

/// <summary>
/// What an attaching client is willing to adopt (closes #473). A rejection ceiling, never a clamp:
/// Cols is the base of every side-table key, so shrinking it would re-point entries, not refuse them.
/// Checked before <c>SnapshotReceived</c>, which is the only path to <c>TerminalStateTransfer.Restore</c>.
/// </summary>
public sealed class MuxAttachLimits
{
    /// <summary>Checked before deserialization.</summary>
    public int MaxSnapshotBytes { get; init; } = MuxProtocol.MaxFrameBytes;

    /// <summary>Visible grid, cols × rows.</summary>
    public long MaxCells { get; init; } = 1_000_000;

    public int MaxScrollbackRows { get; init; } = 50_000;
}
