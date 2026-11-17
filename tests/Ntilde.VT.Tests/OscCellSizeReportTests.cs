using System.Globalization;

namespace Ntilde.VT.Tests;

public sealed class OscCellSizeReportTests
{
    private static (TerminalBuffer Buffer, AnsiParser Parser, List<string> Replies) Create()
    {
        var buffer = new TerminalBuffer(80, 24);
        var replies = new List<string>();
        var parser = new AnsiParser(buffer)
        {
            OnResponse = replies.Add,
            CellSizeReports = new CellSizeReportMetrics(8.25, 17.5, 2, 12.25)
        };
        return (buffer, parser, replies);
    }

    [Theory]
    [InlineData("\a")]
    [InlineData("\x1b\\")]
    [InlineData("\u009c")]
    public void Exact_protocols_have_distinct_dimension_order_units_and_framing(string terminator)
    {
        var (buffer, parser, replies) = Create();
        parser.Process("\x1b[3;5H\x1b[1;4m");
        string input = "\x1b]1337;ReportCellSize" + terminator + "\x1b]776;?" + terminator;
        foreach (char value in input) parser.Process(value.ToString());
        Assert.Equal(new[] { "\x1b]1337;ReportCellSize=17.5;8.25;2\x1b\\", "\x1b]776;17;35;25" + terminator }, replies);
        Assert.Equal((2, 4), (buffer.CursorRow, buffer.CursorCol));
        Assert.True(buffer.IsBold);
        Assert.True(buffer.IsUnderline);
        Assert.Equal(0, buffer.Scrollback.Count);
    }

    [Theory]
    [InlineData("776")]
    [InlineData("776;")]
    [InlineData("776;??")]
    [InlineData("776;?;1")]
    [InlineData("776;17;35;25")]
    [InlineData("1337;ReportCellSize;")]
    [InlineData("1337;ReportCellSize=?")]
    [InlineData("1337;ReportCellSize=17;8;2")]
    [InlineData("1337;reportcellsize")]
    [InlineData("1337;ReportCellSizeExtra")]
    public void Unsupported_forms_and_incoming_replies_are_silent(string body)
    {
        var (_, parser, replies) = Create();
        parser.Process("\x1b]" + body + "\a");
        Assert.Empty(replies);
    }

    [Fact]
    public void Missing_or_invalid_host_metrics_never_fabricate_a_reply()
    {
        var (_, parser, replies) = Create();
        foreach (var metrics in new CellSizeReportMetrics?[] { null, new(0, 20, 2, 12), new(10, double.NaN, 2, 12),
            new(10, 20, double.PositiveInfinity, 12), new(10, 20, 2, 21), new(10, 20, 2, -1), new(10, 20, int.MaxValue, 12) })
        {
            parser.CellSizeReports = metrics;
            parser.Process("\x1b]1337;ReportCellSize\a\x1b]776;?\a");
        }
        Assert.Empty(replies);
    }

    [Fact]
    public void Updated_metrics_and_non_english_culture_use_the_current_atomic_snapshot()
    {
        var (_, parser, replies) = Create();
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            parser.CellSizeReports = new(7.5, 16, 1.5, 11);
            parser.Process("\x1b]1337;ReportCellSize\a\x1b]776;?\a");
            Assert.Equal(new[] { "\x1b]1337;ReportCellSize=16;7.5;1.5\x1b\\", "\x1b]776;11;24;17\a" }, replies);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
