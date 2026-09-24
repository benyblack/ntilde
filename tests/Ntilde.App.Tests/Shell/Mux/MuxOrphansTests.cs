using Ntilde.Mux.Contracts;
using Ntilde.Pty;
using Ntilde.Shell.Mux;

namespace Ntilde.Tests.Shell.Mux;

/// <summary>Spec §9 orphans: daemon sessions no saved pane references and no client is attached to.</summary>
public sealed class MuxOrphansTests
{
    private static SessionSummary S(Guid id, bool running = true, int attached = 0, bool faulted = false) =>
        new() { SessionId = id, Running = running, AttachedClients = attached, Faulted = faulted };

    [Fact]
    public void Collects_ids_from_nested_splits_and_ignores_garbage()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        var session = new NtildeSession
        {
            Tabs =
            {
                new TabSession { Root = new PaneNode { Type = NodeType.Split, Children =
                {
                    new PaneNode { Type = NodeType.Leaf, MuxSessionId = a.ToString() },
                    new PaneNode { Type = NodeType.Leaf, MuxSessionId = "not-a-guid" },
                } } },
                new TabSession { Root = new PaneNode { Type = NodeType.Leaf, MuxSessionId = b.ToString() } },
                new TabSession { Root = null },
            },
        };
        Assert.Equal(new HashSet<Guid> { a, b }, MuxOrphans.CollectReferencedIds(session));
        Assert.Empty(MuxOrphans.CollectReferencedIds(null));
    }

    [Fact]
    public void Orphans_are_running_unattached_unfaulted_and_unreferenced()
    {
        Guid referenced = Guid.NewGuid(), orphan = Guid.NewGuid(), attached = Guid.NewGuid(), exited = Guid.NewGuid(), faulted = Guid.NewGuid();
        var result = MuxOrphans.Select(
            [S(referenced), S(orphan), S(attached, attached: 1), S(exited, running: false), S(faulted, faulted: true)],
            new HashSet<Guid> { referenced });
        Assert.Equal([orphan], result.Select(r => r.SessionId));
    }
}
