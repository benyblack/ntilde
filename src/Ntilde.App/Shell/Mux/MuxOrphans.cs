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

    public static IReadOnlyList<SessionSummary> Select(IEnumerable<SessionSummary> sessions, IReadOnlySet<Guid> referenced) =>
        sessions.Where(s => s.Running && !s.Faulted && s.AttachedClients == 0 && !referenced.Contains(s.SessionId)).ToList();
}
