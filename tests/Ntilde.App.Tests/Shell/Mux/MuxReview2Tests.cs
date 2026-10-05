using System.Net.Sockets;
using Ntilde.Mux;
using Ntilde.Pty;
using Ntilde.Shell;
using Ntilde.Shell.Mux;

namespace Ntilde.Tests.Shell.Mux;

/// <summary>PR #489 review 2: the app-side pieces that have no better home.</summary>
public sealed class MuxReview2Tests
{
    // ---- item 3: a SocketException must end a verb with exit 1, not crash it (serve's half: Ntilde.Mux.Tests' MuxDaemonReviewTests).

    [Fact]
    public void A_socket_exception_is_a_reportable_verb_failure()
    {
        Assert.True(MuxCommand.IsReportableFailure(new SocketException(98)));
        Assert.True(MuxCommand.IsReportableFailure(new IOException("x")));
        Assert.False(MuxCommand.IsReportableFailure(new InvalidOperationException("a bug")));
    }

    /// <summary>
    /// Task 22 review: a GUI started inside a mux shell must not hand that shell's session id to its
    /// local panes (overrides cannot unset a variable, so it is blanked); a daemon spawn's own id wins.
    /// </summary>
    [Fact]
    public void An_inherited_mux_session_id_is_blanked_for_local_shells_but_a_daemon_spawns_own_id_is_kept()
    {
        const string Key = MuxServer.SessionEnvironmentVariable;
        string inherited = Guid.NewGuid().ToString("D");

        IReadOnlyDictionary<string, string>? none = DefaultTerminalSessionFactory.MaskInheritedMuxSession(null, inherited);
        IReadOnlyDictionary<string, string>? withOthers = DefaultTerminalSessionFactory.MaskInheritedMuxSession(new Dictionary<string, string> { ["ZDOTDIR"] = "/z" }, inherited);
        string own = Guid.NewGuid().ToString("D");
        IReadOnlyDictionary<string, string>? daemon = DefaultTerminalSessionFactory.MaskInheritedMuxSession(new Dictionary<string, string> { [Key] = own }, inherited);

        Assert.Equal(KeyValuePair.Create(Key, string.Empty), Assert.Single(none!));
        Assert.Equal(("/z", string.Empty), (withOthers!["ZDOTDIR"], withOthers[Key]));
        Assert.Equal(own, daemon![Key]);
        Assert.False(Guid.TryParse(none![Key], out _)); // so `mux attach` treats the pane as inside no session
        Assert.Null(DefaultTerminalSessionFactory.MaskInheritedMuxSession(null, null)); // nothing inherited: unchanged
    }

    // ---- item 6: one daemon session id per restored pane.

    private static PaneNode Leaf(string? muxId) => new() { Type = NodeType.Leaf, MuxSessionId = muxId, MuxEndpoint = muxId is null ? null : "ep" };

    [Fact]
    public void A_duplicated_mux_id_is_kept_only_on_its_first_pane()
    {
        string a = Guid.NewGuid().ToString("D");
        string b = Guid.NewGuid().ToString("D");
        PaneNode first = Leaf(a), splitDup = Leaf(a), other = Leaf(b), secondTabDup = Leaf(a), bDup = Leaf(b.ToUpperInvariant());
        var session = new NtildeSession
        {
            Tabs =
            {
                new TabSession { Root = new PaneNode { Type = NodeType.Split, Children = { first, splitDup, other } } },
                new TabSession { Root = new PaneNode { Type = NodeType.Split, Children = { secondTabDup, bDup, Leaf(null) } } },
            },
        };

        SessionManager.DedupeMuxIds(session);

        Assert.Equal(a, first.MuxSessionId);
        Assert.Equal("ep", first.MuxEndpoint);
        Assert.Equal(b, other.MuxSessionId);
        Assert.Null(splitDup.MuxSessionId);
        Assert.Null(splitDup.MuxEndpoint);
        Assert.Null(secondTabDup.MuxSessionId); // across tabs too: one restore pass is the whole file
        Assert.Null(bDup.MuxSessionId);         // the same id in another spelling is the same session
    }
}
