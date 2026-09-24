using Ntilde.Pty;

namespace Ntilde.Shell.Mux;

internal enum PersistentSessionOutcome { NotPersistent, Spawned, Reattached, PreviousLost, Unavailable }

/// <param name="Endpoint">The daemon endpoint the session lives on (persisted as PaneNode.MuxEndpoint); null when not persistent.</param>
/// <param name="Detail">Why the outcome is Unavailable, for the log.</param>
internal sealed record PersistentSessionResult(ITerminalSession Session, PersistentSessionOutcome Outcome, string? Endpoint, string? Detail)
{
    /// <summary>Unavailable because the running daemon speaks another protocol version (the pane adds a kill-server hint).</summary>
    public bool VersionMismatch { get; init; }
}

/// <summary>A factory that can tell the pane whether the session it made will persist (spec §7).</summary>
internal interface IPersistentSessionFactory : ITerminalSessionFactory
{
    PersistentSessionResult CreatePersistent(TerminalSessionRequest request);
}
