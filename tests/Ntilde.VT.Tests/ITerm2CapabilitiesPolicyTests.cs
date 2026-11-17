using Ntilde.VT;

namespace Ntilde.VT.Tests;

/// <summary>
/// Ntilde implements none of iTerm2's feature-reporting protocol: the VT answers no
/// <c>OSC 1337 ; Capabilities</c> request in any spelling, so the terminal never declares a
/// feature list (the App-side test pins that the native PTY layer never sets <c>TERM_FEATURES</c>
/// for a child either). The only OSC 1337 replies remain the cell-size report, and images never
/// reply. Policy audit: <c>docs/qa/terminfo/2026-11-17-term-features-policy.md</c>.
/// </summary>
public sealed class ITerm2CapabilitiesPolicyTests
{
    private const string Esc = "\u001b";

    [Theory]
    [InlineData("\a")]
    [InlineData("\x1b\\")]
    [Trait("Category", "Regression")]
    public void Capabilities_requests_are_ignored_without_a_reply_or_cell_change(string terminator)
    {
        var buffer = new TerminalBuffer(20, 4);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false);
        var replies = new List<string>();
        parser.OnResponse = replies.Add;

        parser.Process($"{Esc}]1337;Capabilities{terminator}");
        parser.Process($"{Esc}]1337;Capabilities?{terminator}");
        parser.Process($"{Esc}]1337;Capabilities=TSGoUwTsBFSyHNoSxP{terminator}");

        Assert.Empty(replies);
        Assert.Equal((0, 0), (buffer.CursorRow, buffer.CursorCol));
        Assert.Equal(" ", FirstCell(buffer));
    }

    private static string FirstCell(TerminalBuffer buffer)
    {
        buffer.Lock.EnterReadLock();
        try { return buffer.GetGrapheme(0, 0); }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Fact]
    [Trait("Category", "Regression")]
    public void A_neighboring_osc_1337_query_still_gets_its_reply()
    {
        var buffer = new TerminalBuffer(20, 4);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false)
        {
            CellSizeReports = new CellSizeReportMetrics(8.5, 18.5, 2.0, 12.0),
        };
        var replies = new List<string>();
        parser.OnResponse = replies.Add;

        parser.Process($"{Esc}]1337;Capabilities{Esc}\\");
        parser.Process($"{Esc}]1337;ReportCellSize{Esc}\\");
        parser.Process($"{Esc}]1337;Capabilities?{Esc}\\");

        var reply = Assert.Single(replies);
        Assert.StartsWith($"{Esc}]1337;ReportCellSize=", reply);
    }
}
