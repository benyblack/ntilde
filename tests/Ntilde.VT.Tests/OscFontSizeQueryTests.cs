using System.Globalization;

namespace Ntilde.VT.Tests;

public sealed class OscFontSizeQueryTests
{
    [Theory]
    [InlineData("\a")]
    [InlineData("\x1b\\")]
    [InlineData("\u009c")]
    public void Queries_use_current_integer_point_size_and_preserve_state_at_every_split(string end)
    {
        foreach (string code in new[] { "7770", "7777" })
        {
            string request = "\x1b]" + code + ";?" + end;
            for (int split = 0; split <= request.Length; split++)
            {
                var buffer = new TerminalBuffer(40, 10);
                var replies = new List<string>();
                var parser = new AnsiParser(buffer) { OnResponse = replies.Add };
                parser.Process("\x1b[31;4mcontent\x1b[2;4r\x1b[?6h");
                byte[] before = TerminalStateSerializer.ToBytes(buffer.ExportState(0));
                foreach (int points in new[] { 12, 19, 1, 512 })
                {
                    parser.CellSizeReports = new(8, 18, 2, 12) { FontSizePoints = points };
                    parser.Process(request[..split]);
                    parser.Process(request[split..]);
                    Assert.Equal("\x1b]" + code + ";" + points.ToString(CultureInfo.InvariantCulture) + end, replies[^1]);
                }
                Assert.Equal(4, replies.Count);
                Assert.Equal(before, TerminalStateSerializer.ToBytes(buffer.ExportState(0)));
            }
        }
    }

    [Fact]
    public void Combined_display_scaling_and_zoom_allow_only_binary_conversion_roundoff()
    {
        var replies = new List<string>();
        var parser = new AnsiParser(new TerminalBuffer(40, 10)) { OnResponse = replies.Add };
        // A real host conversion:20DIP,140% UI zoom,150% backing scale.
        double effective = 1.4 * 1.5;
        double points = 20 * (effective / 1.5) * 72 / 96;
        foreach (double value in new[] { points, Math.BitDecrement(21), Math.BitIncrement(21) })
        {
            parser.CellSizeReports = new(8, 18, 1.5, 12) { FontSizePoints = value };
            parser.Process("\x1b]7770;?\a\x1b]7777;?\a");
        }
        Assert.Equal(6, replies.Count);
        Assert.All(replies, reply => Assert.Contains(";21\a", reply));
        replies.Clear();
        foreach (double value in new[] { 21.000001, 20.999999, 21.5 })
        {
            parser.CellSizeReports = new(8, 18, 1.5, 12) { FontSizePoints = value };
            parser.Process("\x1b]7770;?\a\x1b]7777;?\a");
        }
        Assert.Empty(replies);
    }

    [Fact]
    public void Missing_invalid_or_fractional_points_are_not_rounded_or_guessed()
    {
        var replies = new List<string>();
        var parser = new AnsiParser(new TerminalBuffer(40, 10)) { OnResponse = replies.Add };
        parser.Process("\x1b]7770;?\a\x1b]7777;?\a");
        foreach (double? points in new double?[] { null, 0, -1, 10.5, double.NaN, double.PositiveInfinity, 513 })
        {
            parser.CellSizeReports = new(8, 18, 2, 12) { FontSizePoints = points };
            parser.Process("\x1b]7770;?\a\x1b]7777;?\a");
        }
        parser.CellSizeReports = new(0, 18, 2, 12) { FontSizePoints = 12 };
        parser.Process("\x1b]7770;?\a\x1b]7777;?\a");
        Assert.Empty(replies);
    }

    [Theory]
    [InlineData("")]
    [InlineData(";")]
    [InlineData(";12")]
    [InlineData(";+2")]
    [InlineData(";-2")]
    [InlineData(";??")]
    [InlineData(";?;1")]
    [InlineData(";12\rid\r")]
    public void Mutation_and_malformed_forms_are_silent_and_leave_the_host_snapshot_unchanged(string tail)
    {
        var replies = new List<string>();
        var parser = new AnsiParser(new TerminalBuffer(40, 10)) { OnResponse = replies.Add,
            CellSizeReports = new(8, 18, 2, 12) { FontSizePoints = 12 } };
        var snapshot = parser.CellSizeReports;
        foreach (string code in new[] { "7770", "7777", "710" }) parser.Process("\x1b]" + code + tail + "\a");
        Assert.Empty(replies);
        Assert.Same(snapshot, parser.CellSizeReports);
        parser.Process("\x1b[c");
        Assert.Equal("\x1b[?62;4;22c", Assert.Single(replies));
    }
}
