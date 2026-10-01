using Ntilde.Mux.Contracts;
using Ntilde.Shell.Mux;

namespace Ntilde.Tests.Shell.Mux;

public sealed class MuxSessionPickerTests
{
    private static SessionSummary S(string title, bool running = true, bool faulted = false, int attached = 0, string? cwd = null, int? exit = null) =>
        new() { SessionId = Guid.NewGuid(), Title = title, Command = "pwsh", Cols = 120, Rows = 40, Running = running, Faulted = faulted, AttachedClients = attached, Cwd = cwd, ExitCode = exit };

    [Fact]
    public void Running_sessions_come_first_faulted_ones_are_left_out_and_open_here_is_marked()
    {
        SessionSummary exited = S("zeta", running: false, exit: 2);
        SessionSummary b = S("beta", attached: 1, cwd: "/srv");
        SessionSummary a = S("alpha");
        SessionSummary broken = S("broken", faulted: true);

        IReadOnlyList<MuxSessionPickerRow> rows = MuxSessionPicker.BuildRows([exited, b, a, broken], new HashSet<Guid> { b.SessionId });

        Assert.Equal(["alpha", "beta", "zeta"], rows.Select(r => r.Title).ToArray());
        Assert.True(rows[1].OpenHere);
        Assert.Equal("exited 2", rows[2].State);
        Assert.Contains("/srv", rows[1].Display, StringComparison.Ordinal);
        Assert.Contains("120x40", rows[1].Display, StringComparison.Ordinal);
        Assert.Contains("1 attached", rows[1].Display, StringComparison.Ordinal);
        Assert.Contains("open here", rows[1].Display, StringComparison.Ordinal);
        Assert.Contains("detached", rows[0].Display, StringComparison.Ordinal);
    }
}
