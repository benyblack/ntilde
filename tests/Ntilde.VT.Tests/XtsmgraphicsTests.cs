using Ntilde.VT;

namespace Ntilde.VT.Tests;

/// <summary>
/// XTSMGRAPHICS, <c>CSI ? Pi ; Pa ; Pv S</c> (#872): xterm's reply format and status codes, with
/// Ntilde's real Sixel limits (4096 color registers, 2000 x 2000 pixels).
/// </summary>
public sealed class XtsmgraphicsTests
{
    // 80 x 24 cells of 9 x 18 pixels: a 720 x 432 text area.
    private const int Cols = 80, Rows = 24;
    private const float CellWidth = 9, CellHeight = 18;

    [Theory]
    [InlineData("\x1b[?1;1;0S", "\x1b[?1;0;4096S")] // read
    [InlineData("\x1b[?1;2;0S", "\x1b[?1;0;4096S")] // reset: the count is already the default
    [InlineData("\x1b[?1;4;0S", "\x1b[?1;0;4096S")] // read maximum
    [InlineData("\x1b[?1;3;4096S", "\x1b[?1;0;4096S")] // set to the count it already has
    [InlineData("\x1b[?1;3;256S", "\x1b[?1;3S")] // notcurses' first try: cannot change
    [InlineData("\x1b[?1;3;1024S", "\x1b[?1;3S")] // and its second
    [InlineData("\x1b[?1;3;0S", "\x1b[?1;3S")]
    [InlineData("\x1b[?1;0;0S", "\x1b[?1;2S")] // unknown action
    [InlineData("\x1b[?1;5;0S", "\x1b[?1;2S")]
    [InlineData("\x1b[?2;1;0S", "\x1b[?2;0;720;432S")] // read: the text area in Sixel pixels
    [InlineData("\x1b[?2;4;0S", "\x1b[?2;0;2000;2000S")] // read maximum: the decoder's clip
    [InlineData("\x1b[?2;2;0S", "\x1b[?2;3S")] // reset: the window sets the size
    [InlineData("\x1b[?2;3;640S", "\x1b[?2;3S")] // set
    [InlineData("\x1b[?2;9;0S", "\x1b[?2;2S")] // unknown action
    [InlineData("\x1b[?3;1;0S", "\x1b[?3;1S")] // ReGIS: unknown item
    [InlineData("\x1b[?0;1;0S", "\x1b[?0;1S")]
    [InlineData("\x1b[?;1;0S", "\x1b[?0;1S")] // an empty parameter is 0
    [InlineData("\x1b[?4;1;0S", "\x1b[?4;1S")]
    [InlineData("\u009b?1;1;0S", "\x1b[?1;0;4096S")] // C1 CSI
    public void Requests_get_xterms_reply_with_ntildes_limits(string request, string reply)
    {
        Assert.Equal(new[] { reply }, Replies(request));
    }

    [Theory]
    [InlineData("\x1b[?1;1S")] // two parameters
    [InlineData("\x1b[?1S")]
    [InlineData("\x1b[?S")]
    [InlineData("\x1b[?1;1;0;0S")] // four
    [InlineData("\x1b[?2;3;640;480S")] // xterm also stays silent on a two-value geometry set
    [InlineData("\x1b[?1:1:0S")] // colon sub-parameters
    [InlineData("\x1b[?1;1:0S")]
    [InlineData("\x1b[?1;1;0 S")] // an intermediate
    [InlineData("\x1b[?1;1;0$S")]
    [InlineData("\x1b[>1;1;0S")] // other leaders
    [InlineData("\x1b[=1;1;0S")]
    public void Other_shapes_are_not_this_request_and_get_no_reply(string request)
    {
        Assert.Empty(Replies(request));
    }

    [Fact]
    public void Geometry_read_is_the_text_area_capped_at_the_decoder_limit()
    {
        var buffer = new TerminalBuffer(300, 20);
        var replies = new List<string>();
        var parser = new AnsiParser(buffer) { CellWidth = 9, CellHeight = 18, OnResponse = replies.Add };
        parser.Process("\x1b[?2;1;0S\x1b[14t");
        Assert.Equal(new[] { "\x1b[?2;0;2000;360S", "\x1b[4;360;2700t" }, replies);
        replies.Clear();
        buffer.Resize(40, 200);
        parser.CellWidth = 10.5f;
        parser.Process("\x1b[?2;1;0S\x1b[14t");
        Assert.Equal(new[] { "\x1b[?2;0;420;2000S", "\x1b[4;3600;420t" }, replies);
    }

    [Fact]
    public void Geometry_read_uses_the_cell_size_sixel_placement_uses()
    {
        // A scaled display: logical 8.5 x 18.5 cells, Sixel placed in 17 x 37 device-pixel cells,
        // as the App sets them. The read and CSI 14 t both report the device-pixel text area (#876).
        var buffer = new TerminalBuffer(20, 10);
        var replies = new List<string>();
        var parser = new AnsiParser(buffer)
        {
            CellWidth = 8.5f,
            CellHeight = 18.5f,
            SixelCellSizeOverride = new SixelCellSize(17, 37),
            RetainSixelSources = true,
            OnResponse = replies.Add,
        };
        parser.Process("\x1b[?2;1;0S\x1b[14t");
        Assert.Equal(new[] { "\x1b[?2;0;340;370S", "\x1b[4;370;340t" }, replies);

        // An image exactly as wide as the reported width spans exactly the pane's columns.
        parser.Process("\x1bPq!340~\x1b\\");
        Assert.Equal(20, Assert.Single(buffer.Images).CellWidth);
    }

    [Fact]
    public void Without_a_sixel_cell_size_the_geometry_read_matches_the_window_pixel_report()
    {
        var replies = new List<string>();
        new AnsiParser(new TerminalBuffer(Cols, Rows)) { CellWidth = CellWidth, CellHeight = CellHeight, OnResponse = replies.Add }
            .Process("\x1b[14t\x1b[?2;1;0S");
        Assert.Equal(new[] { "\x1b[4;432;720t", "\x1b[?2;0;720;432S" }, replies);
    }

    [Fact]
    public void Queries_change_nothing_and_reply_under_the_write_lock()
    {
        var buffer = new TerminalBuffer(Cols, Rows);
        var replies = new List<string>();
        var parser = new AnsiParser(buffer)
        {
            CellWidth = CellWidth,
            CellHeight = CellHeight,
            OnResponse = reply =>
            {
                Assert.True(buffer.Lock.IsWriteLockHeld);
                replies.Add(reply);
            },
        };
        parser.Process("line one\r\nline two\x1b[31m\x1b[5;7H");
        byte[] before = TerminalStateSerializer.ToBytes(buffer.ExportState(0));
        parser.Process("\x1b[?1;3;256S\x1b[?1;2;0S\x1b[?2;3;640S\x1b[?2;1;0S\x1b[?3;1;0S");
        Assert.Equal(5, replies.Count);
        Assert.Equal(before, TerminalStateSerializer.ToBytes(buffer.ExportState(0)));
    }

    [Fact]
    public void Every_split_of_a_request_gets_the_same_reply()
    {
        const string request = "\x1b[?2;1;0S";
        for (int split = 1; split < request.Length; split++)
        {
            var replies = new List<string>();
            var parser = new AnsiParser(new TerminalBuffer(Cols, Rows)) { CellWidth = CellWidth, CellHeight = CellHeight, OnResponse = replies.Add };
            parser.Process(request[..split]);
            Assert.Empty(replies);
            parser.Process(request[split..]);
            Assert.Equal(new[] { "\x1b[?2;0;720;432S" }, replies);
        }
    }

    [Fact]
    public void The_reported_maximum_is_the_sixel_size_cap()
    {
        Assert.Equal(2000, SixelGeometry.DefaultMaxPixelDimension);
        string wide = "\x1bPq#1;2;100;0;0#1!2500~\x1b\\";
        Assert.Equal((SixelGeometry.DefaultMaxPixelDimension, 6), SixelGeometry.Measure(wide.AsSpan(2, wide.Length - 4)));
    }

    private static List<string> Replies(string input)
    {
        var replies = new List<string>();
        new AnsiParser(new TerminalBuffer(Cols, Rows)) { CellWidth = CellWidth, CellHeight = CellHeight, OnResponse = replies.Add }.Process(input);
        return replies;
    }
}
