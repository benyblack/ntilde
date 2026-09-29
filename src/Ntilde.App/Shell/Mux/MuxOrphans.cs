using Ntilde.Mux.Contracts;
using Ntilde.Pty;

namespace Ntilde.Shell.Mux;

/// <summary>
/// Daemon sessions no restored pane will reopen - the GUI crashed before it wrote the session file
/// (spec §9). Computed from the SAVED session tree, not live panes: restored panes have not attached
/// yet, and deferred tabs have no panes at all.
/// </summary>
internal static class MuxOrphans
{
    public static HashSet<Guid> CollectReferencedIds(NtildeSession? session)
    {
        var ids = new HashSet<Guid>();
        if (session is null) return ids;
        foreach (TabSession tab in session.Tabs) Walk(tab.Root, ids);
        return ids;
    }

    private static void Walk(PaneNode? node, HashSet<Guid> ids)
    {
        if (node is null) return;
        if (Guid.TryParse(node.MuxSessionId, out Guid id)) ids.Add(id);
        foreach (PaneNode child in node.Children) Walk(child, ids);
    }

    /// <summary>A running, healthy shell nobody shows.</summary>
    private static bool IsUnshown(SessionSummary s) => s.Running && !s.Faulted && s.AttachedClients == 0;

    /// <summary>Crash orphans only: a shell the user detached on purpose stays detached (spec §7.7).</summary>
    public static IReadOnlyList<SessionSummary> Select(IEnumerable<SessionSummary> sessions, IReadOnlySet<Guid> referenced) =>
        sessions.Where(s => IsUnshown(s) && !s.DetachedByUser && !referenced.Contains(s.SessionId)).ToList();

    /// <summary>
    /// Running shells the user detached and nobody shows: the once-per-launch reminder's count. A
    /// saved pane that names one reopens it itself, so it is not counted as left behind. (In the rare
    /// case that pane's command no longer matches the session's, it spawns fresh instead and the
    /// shell stays detached, uncounted this launch; the next launch counts it.)
    /// </summary>
    public static int CountUserDetached(IEnumerable<SessionSummary> sessions, IReadOnlySet<Guid> referenced) =>
        sessions.Count(s => IsUnshown(s) && s.DetachedByUser && !referenced.Contains(s.SessionId));
}
