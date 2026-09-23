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

    /// <summary>
    /// Stream bytes (Output, ResizeEvent, responses, notifications) queued-but-unwritten per client
    /// before it is disconnected as too slow. Snapshot frames are accounted separately, against
    /// <see cref="MaxQueuedSnapshotBytes"/>, so an attach never costs the connection its budget (spec §7).
    /// </summary>
    public long ClientSendBudgetBytes { get; init; } = 16L * 1024 * 1024;

    /// <summary>
    /// Sanity bound on snapshot bytes queued-but-unwritten per client. One snapshot is always
    /// accepted when none is queued (whatever its size); more are accepted while the total stays
    /// within this bound, so parallel attaches at GUI start-up fit. Over it: client_too_slow.
    /// </summary>
    public long MaxQueuedSnapshotBytes { get; init; } = 256L * 1024 * 1024;

    public int MaxSnapshotBytes { get; init; } = MuxProtocol.MaxFrameBytes - MuxFrames.SnapshotHeaderBytes;

    public int MaxInboundFrameBytes { get; init; } = MuxProtocol.MaxFrameBytes;

    /// <summary>
    /// Largest grid (cols x rows) a peer may ask for through spawn, attach or resize. The headless
    /// buffer allocates eagerly, so this is what stands between a hostile geometry and an OOM in the
    /// daemon. Matches the client's default <c>MuxAttachLimits.MaxCells</c>, so nothing the server
    /// accepts makes a default client drop its connection (spec §7).
    /// </summary>
    public long MaxCells { get; init; } = 1_000_000;

    /// <summary>Largest single dimension (cols or rows) a peer may ask for.</summary>
    public int MaxDimension { get; init; } = 10_000;

    public Action<string>? Log { get; init; }
}
