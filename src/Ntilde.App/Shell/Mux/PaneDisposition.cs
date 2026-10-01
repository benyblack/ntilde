namespace Ntilde.Shell.Mux;

/// <summary>
/// What disposing a pane's control tree means for its session (Phase 2 spec §9, Phase 3 §7.4). Only
/// a persistent (mux) session is affected: a normal session ends either way.
/// </summary>
internal enum PaneDisposition
{
    /// <summary>The user closed the pane: the shell ends, even one living in the daemon.</summary>
    EndSession,

    /// <summary>"Pane: Detach" or the shared-close prompt's Detach: only the view goes, the daemon keeps the shell.</summary>
    Detach,

    /// <summary>
    /// An agent closed a pane whose shell other clients still show: only the view goes, like Detach, but
    /// the daemon is not told it was deliberate.
    /// </summary>
    Leave,
}
