namespace Ntilde.Shell.Mux;

/// <summary>
/// What disposing a pane's control tree means for its session (spec §9). Only a persistent (mux)
/// session tells the two apart: a normal session ends either way.
/// </summary>
internal enum PaneDisposition
{
    /// <summary>The user closed the pane: the shell ends, even one living in the daemon.</summary>
    EndSession,

    /// <summary>Only the view goes away: a mux session detaches and keeps running in the daemon.</summary>
    Detach,
}
