namespace Ntilde.Mux;

public sealed class HeadlessSessionOptions
{
    public string Title { get; init; } = string.Empty;
    public string Command { get; init; } = string.Empty;
    public string? Arguments { get; init; }
    public int Cols { get; init; } = 80;
    public int Rows { get; init; } = 24;
    /// <summary>Explicit, reported to clients in Welcome, so both halves of a snapshot parse identically.</summary>
    public bool ForceConPtyFiltering { get; init; } = OperatingSystem.IsWindows();
    public Action<string>? Log { get; init; }

    /// <summary>
    /// Input (UTF-16 bytes) queued for the child's stdin and not yet written; past it, further input
    /// is dropped rather than queued forever behind a child that has stopped reading (spec §4).
    /// </summary>
    public long MaxQueuedInputBytes { get; init; } = 16L * 1024 * 1024;
}
