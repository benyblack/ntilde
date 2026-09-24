namespace Ntilde.Shell.Mux;

/// <summary>
/// What disposing a pane's control tree means for its session (spec §9). Only a persistent (mux)
/// session is affected: a normal session ends either way.
/// </summary>
/// <remarks>
/// A <c>Detach</c> member (only the view goes away, the mux session keeps running) existed for a
/// caller that never materialised and was removed (PR #489 review 2); add it back with its first
/// user. The enum stays so that caller has one place to plug in.
/// </remarks>
internal enum PaneDisposition
{
    /// <summary>The user closed the pane: the shell ends, even one living in the daemon.</summary>
    EndSession,
}
