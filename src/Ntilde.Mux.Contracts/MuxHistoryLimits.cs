namespace Ntilde.Mux.Contracts;

/// <summary>Daemon-owned retention and independent attach-transfer limits, reported by supporting peers.</summary>
public sealed record MuxHistoryLimits
{
    public const int DefaultRows = 10_000;
    public const int DefaultMaxRows = 100_000;
    public const long DefaultMaxBytes = 128L * 1024 * 1024;
    public const int DefaultMaxAttachRows = 20_000;

    public int MaxRows { get; init; }
    public long MaxBytes { get; init; }
    public int MaxAttachRows { get; init; }
}
