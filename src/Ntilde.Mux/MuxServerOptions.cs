using Ntilde.Mux.Contracts;

namespace Ntilde.Mux;

public sealed class MuxServerOptions
{
    public int MinProtocolVersion { get; init; } = MuxProtocol.MinSupportedVersion;
    public int MaxProtocolVersion { get; init; } = MuxProtocol.MaxSupportedVersion;

    /// <summary>Passed to every session's parser and reported in Welcome so clients parse identically.</summary>
    public bool ForceConPtyFiltering { get; init; } = OperatingSystem.IsWindows();

    /// <summary>Server-side cap on an attach's <c>maxScrollbackRows</c> (spec §7).</summary>
    public int MaxAttachScrollbackRows { get; init; } = 20_000;

    /// <summary>Bytes queued-but-unwritten per client before it is disconnected as too slow.</summary>
    public long ClientSendBudgetBytes { get; init; } = 16L * 1024 * 1024;

    public int MaxSnapshotBytes { get; init; } = MuxProtocol.MaxFrameBytes - MuxFrames.SnapshotHeaderBytes;

    public int MaxInboundFrameBytes { get; init; } = MuxProtocol.MaxFrameBytes;

    public Action<string>? Log { get; init; }
}
