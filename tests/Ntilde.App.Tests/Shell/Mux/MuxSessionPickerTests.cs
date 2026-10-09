using Ntilde.Mux.Contracts;
using Ntilde.Shell.Mux;

namespace Ntilde.Tests.Shell.Mux;

public sealed class MuxSessionPickerTests
{
    private static readonly MuxEndpointId HostA = MuxEndpointId.ForSsh(Guid.NewGuid());
    private static readonly MuxEndpointId HostB = MuxEndpointId.ForSsh(Guid.NewGuid());

    private static readonly IReadOnlySet<(MuxEndpointId, Guid)> NoneOpenHere = new HashSet<(MuxEndpointId, Guid)>();

    private static SessionSummary S(string title, bool running = true, bool faulted = false, int attached = 0, string? cwd = null, int? exit = null, string command = "pwsh", Guid? id = null) =>
        new() { SessionId = id ?? Guid.NewGuid(), Title = title, Command = command, Cols = 120, Rows = 40, Running = running, Faulted = faulted, AttachedClients = attached, Cwd = cwd, ExitCode = exit };

    private static MuxPickerHostListing Local(params SessionSummary[] sessions) => new(MuxEndpointId.Local, "this computer", sessions, null);

    private static MuxPickerHostListing Remote(MuxEndpointId endpoint, string host, params SessionSummary[] sessions) => new(endpoint, host, sessions, null);

    private static List<MuxSessionPickerRow> Sessions(IReadOnlyList<MuxPickerItem> items) => [.. items.OfType<MuxSessionPickerRow>()];

    [Fact]
    public void Running_sessions_come_first_faulted_ones_are_left_out_and_open_here_is_marked()
    {
        SessionSummary exited = S("zeta", running: false, exit: 2);
        SessionSummary b = S("beta", attached: 1, cwd: "/srv");
        SessionSummary a = S("alpha");
        SessionSummary broken = S("broken", faulted: true);

        List<MuxSessionPickerRow> rows = Sessions(MuxSessionPicker.BuildRows([Local(exited, b, a, broken)], new HashSet<(MuxEndpointId, Guid)> { (MuxEndpointId.Local, b.SessionId) }));

        Assert.Equal(["alpha", "beta", "zeta"], rows.Select(r => r.Title).ToArray());
        Assert.True(rows[1].OpenHere);
        Assert.Equal("exited 2", rows[2].State);
        Assert.Contains("/srv", rows[1].Display, StringComparison.Ordinal);
        Assert.Contains("120x40", rows[1].Display, StringComparison.Ordinal);
        Assert.Contains("1 attached", rows[1].Display, StringComparison.Ordinal);
        Assert.Contains("open here", rows[1].Display, StringComparison.Ordinal);
        Assert.Contains("detached", rows[0].Display, StringComparison.Ordinal);
    }

    /// <summary>Phase 5 spec §5: rows come grouped by host - the local daemon's first, then the remote hosts in the order given.</summary>
    [Fact]
    public void Rows_are_grouped_by_host_local_first_then_remotes_in_the_given_order()
    {
        IReadOnlyList<MuxPickerItem> items = MuxSessionPicker.BuildRows(
            [
                Remote(HostB, "nova@b", S("b-zeta"), S("b-alpha", running: false, exit: 0)),
                Local(S("l-beta"), S("l-alpha")),
                Remote(HostA, "nova@a", S("a-one")),
            ],
            NoneOpenHere);

        List<MuxSessionPickerRow> rows = Sessions(items);
        Assert.Equal(5, items.Count);
        Assert.Equal(["l-alpha", "l-beta", "b-zeta", "b-alpha", "a-one"], rows.Select(r => r.Title).ToArray());
        Assert.Equal([MuxEndpointId.Local, MuxEndpointId.Local, HostB, HostB, HostA], rows.Select(r => r.Endpoint).ToArray());
        Assert.Equal(["this computer", "this computer", "nova@b", "nova@b", "nova@a"], rows.Select(r => r.HostDisplayName).ToArray());
    }

    [Fact]
    public void A_remote_row_names_its_host_first_and_a_local_row_does_not()
    {
        List<MuxSessionPickerRow> rows = Sessions(MuxSessionPicker.BuildRows([Local(S("here")), Remote(HostA, "nova@a", S("there", cwd: "/home/nova"))], NoneOpenHere));

        Assert.Equal(2, rows.Count);
        Assert.Equal("here  —  pwsh  ·  120x40  ·  detached  ·  running", rows[0].Display);
        Assert.Equal("[nova@a] there  —  pwsh  /home/nova  ·  120x40  ·  detached  ·  running", rows[1].Display);
    }

    /// <summary>A host whose listing failed gives one line saying so, in our own words; it cannot be chosen.</summary>
    [Theory]
    [InlineData("ConnectionFailed", "[nova@a] not reachable: the connection failed")]
    [InlineData("TimedOut", "[nova@a] not reachable: it did not answer in time")]
    [InlineData("NotUsable", "[nova@a] not reachable: its multiplexer is not usable")]
    [InlineData("Restarting", "[nova@a] not reachable: the multiplexer is restarting")]
    public void A_host_that_could_not_be_listed_gives_one_disabled_row(string errorName, string expected)
    {
        MuxPickerHostError error = Enum.Parse<MuxPickerHostError>(errorName);
        IReadOnlyList<MuxPickerItem> items = MuxSessionPicker.BuildRows(
            [Local(S("here")), new MuxPickerHostListing(HostA, "nova@a", [S("ignored")], error)],
            NoneOpenHere);

        Assert.Equal(2, items.Count);
        Assert.True(items[0].IsSelectable);
        MuxSessionPickerErrorRow row = Assert.IsType<MuxSessionPickerErrorRow>(items[1]);
        Assert.Equal((HostA, "nova@a", error), (row.Endpoint, row.HostDisplayName, row.Error));
        Assert.False(row.IsSelectable);
        Assert.Equal(expected, row.Display);
    }

    /// <summary>Phase 4 spec §5: one id on two daemons is two sessions, and "open here" is per daemon.</summary>
    [Fact]
    public void Open_here_is_keyed_by_endpoint_so_one_id_on_two_daemons_is_two_rows()
    {
        Guid id = Guid.NewGuid();

        List<MuxSessionPickerRow> rows = Sessions(MuxSessionPicker.BuildRows(
            [Local(S("mine", id: id)), Remote(HostA, "nova@a", S("theirs", id: id))],
            new HashSet<(MuxEndpointId, Guid)> { (HostA, id) }));

        Assert.Equal(2, rows.Count);
        Assert.Equal([(MuxEndpointId.Local, id, false), (HostA, id, true)], rows.Select(r => (r.Endpoint, r.SessionId, r.OpenHere)).ToArray());
    }

    /// <summary>
    /// Ruling P2 (Task 5): a remote row's title, command and cwd come from another machine, so they are quoted - control,
    /// bidi and format characters stripped and the length capped - before they reach the line. A local row's are cleaned:
    /// a local shell's OSC title can carry the same characters.
    /// </summary>
    [Fact]
    public void Server_text_is_quoted_on_a_remote_row_and_cleaned_on_a_local_one()
    {
        const string Spoofed = "safe\u202Etxt.exe\u0007\u200F";
        string longCommand = new('c', 300);

        List<MuxSessionPickerRow> rows = Sessions(MuxSessionPicker.BuildRows(
            [
                Local(S(Spoofed, cwd: "/tmp\u2066x", command: longCommand)),
                Remote(HostA, "nova@a", S(Spoofed, cwd: "/srv\u001b[2Jy", command: longCommand)),
            ],
            NoneOpenHere));

        Assert.Equal(2, rows.Count);
        MuxSessionPickerRow local = rows[0], remote = rows[1];
        Assert.Equal(("safetxt.exe", longCommand, "/tmpx"), (local.Title, local.Command, local.Cwd));
        Assert.Equal(("safetxt.exe", new string('c', 200) + "…", "/srv[2Jy"), (remote.Title, remote.Command, remote.Cwd));
        foreach (MuxSessionPickerRow row in rows)
        {
            Assert.DoesNotContain(row.Display, c => c is '\u202E' or '\u0007' or '\u200F' or '\u2066' or '\u001b');
        }
    }

    [Fact]
    public void A_connect_row_offers_to_connect_to_its_host()
    {
        var row = new MuxSessionPickerConnectRow(Guid.NewGuid(), "nova@b");

        Assert.Equal("Connect to nova@b…", row.Display);
        Assert.True(row.IsSelectable);
    }

    /// <summary>A listing's failure is told by its kind, never by its text (the error row's words are ours).</summary>
    [Fact]
    public void A_listing_failure_is_classified_by_its_kind()
    {
        Assert.Equal(MuxPickerHostError.TimedOut, MuxSessionPicker.ErrorOf(new OperationCanceledException()));
        Assert.Equal(MuxPickerHostError.TimedOut, MuxSessionPicker.ErrorOf(new TimeoutException()));
        Assert.Equal(MuxPickerHostError.NotUsable, MuxSessionPicker.ErrorOf(new MuxProtocolException("protocol_error", "nope")));
        Assert.Equal(MuxPickerHostError.ConnectionFailed, MuxSessionPicker.ErrorOf(new IOException("pipe broken")));
        Assert.Equal(MuxPickerHostError.ConnectionFailed, MuxSessionPicker.ErrorOf(new ObjectDisposedException("client")));
    }
}
