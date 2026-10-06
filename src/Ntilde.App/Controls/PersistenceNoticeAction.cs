using Ntilde.Shell.Mux.Remote;

namespace Ntilde.Controls;

/// <summary>
/// The one action a persistence notice's toast can offer (Phase 4 spec §7.5): a button labelled
/// <paramref name="Label"/> that hides the toast and runs <paramref name="Run"/> on the UI thread. A toast
/// that merges several notices offers the last action raised.
/// </summary>
/// <param name="Label">The button's text, for example <see cref="TerminalPane.RemoteMuxInstallActionLabel"/>.</param>
/// <param name="Run">What the button does. A throw is logged, never raised into the click.</param>
internal sealed record PersistenceNoticeAction(string Label, Action Run)
{
    /// <summary>
    /// The action a remote failure offers (spec §7.5): the install flow (§9) for the profile when the
    /// remote has no ntilde-mux (<see cref="RemoteFailureKind.NotInstalled"/>), or when it has one that
    /// speaks another protocol (<see cref="RemoteFailureKind.VersionMismatch"/>, labelled as an update).
    /// Nothing else: reinstalling fixes neither a host it cannot run on nor a failed SSH connection. Null
    /// as well when there is no install flow to open (<paramref name="openInstall"/> is null).
    /// </summary>
    /// <param name="host">The profile's <c>user@host</c>: the button names it (final review I1), since a merged toast offers only the last action raised.</param>
    public static PersistenceNoticeAction? ForRemoteFailure(RemoteMuxFailure? failure, Guid profileId, string host, Action<Guid>? openInstall)
    {
        if (openInstall is null) return null;

        string? label = failure?.Kind switch
        {
            RemoteFailureKind.NotInstalled => TerminalPane.RemoteMuxInstallActionLabel(host),
            RemoteFailureKind.VersionMismatch => TerminalPane.RemoteMuxUpdateActionLabel(host),
            _ => null,
        };

        return label is null ? null : new PersistenceNoticeAction(label, () => openInstall(profileId));
    }
}
