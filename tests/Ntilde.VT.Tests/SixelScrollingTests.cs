using Ntilde.VT;
using Ntilde.VT.Tests.StateTransfer;

namespace Ntilde.VT.Tests;

public sealed class SixelScrollingTests
{
    private const string Image = "\x1bPq!30~-~\x1b\\";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_wide_image_never_wraps_text_cells_or_erases_the_underlying_row(bool lnm)
    {
        var buffer = new TerminalBuffer(10, 8);
        var parser = new AnsiParser(buffer) { ImageDecoder = new FixedSizeImageDecoder(300, 40) };
        parser.Process("\x1b[2;1Hpreserved\x1b[2;9H" + (lnm ? "\x1b[20h" : ""));
        parser.Process(Image);
        Assert.Equal((0, 3), (buffer.CursorCol, buffer.CursorRow));
        Assert.Equal(8, buffer.TotalLines);
        buffer.Lock.EnterReadLock();
        try { Assert.Equal("p", buffer.GetGrapheme(0, 1)); }
        finally { buffer.Lock.ExitReadLock(); }
        Assert.Equal((8, 1), (Assert.Single(buffer.Images).CellX, buffer.Images[0].CellY));
        Assert.Equal(28, buffer.Images[0].ClipRightColumns);
    }

    [Fact]
    public void A_headless_parser_advances_the_same_rows_without_a_bitmap_decoder()
    {
        var buffer = new TerminalBuffer(10, 3);
        var parser = new AnsiParser(buffer) { CellHeight = 6 };
        parser.Process("\x1b[3;1H" + Image);
        Assert.Equal(5, buffer.TotalLines);
        Assert.Equal((0, 2), (buffer.CursorCol, buffer.CursorRow));
        Assert.Empty(buffer.Images);
    }

    [Fact]
    public void Display_mode_places_at_page_origin_without_moving_or_scrolling_the_text_cursor()
    {
        var buffer = new TerminalBuffer(10, 3);
        var parser = new AnsiParser(buffer) { ImageDecoder = new FixedSizeImageDecoder(300, 100) };
        parser.Process("\x1b[?80h\x1b[3;5H" + Image);
        Assert.Equal((4, 2), (buffer.CursorCol, buffer.CursorRow));
        Assert.Equal(3, buffer.TotalLines);
        var image = Assert.Single(buffer.Images);
        Assert.Equal((0, 0), (image.CellX, image.CellY));
    }

    [Fact]
    public void Mode_80_has_live_query_state_and_RIS_restores_scrolling()
    {
        var buffer = new TerminalBuffer(10, 8);
        var parser = new AnsiParser(buffer);
        var replies = new List<string>();
        parser.OnResponse += replies.Add;
        parser.Process("\x1b[?80$p\x1b[?80h\x1b[?80$p\x1b[?80l\x1b[?80$p\x1b[?80h\u001bc\x1b[?80$p");
        Assert.Equal(new[] { "\x1b[?80;2$y", "\x1b[?80;1$y", "\x1b[?80;2$y", "\x1b[?80;2$y" }, replies);
    }
    [Fact]
    public void A_tall_main_screen_image_is_anchored_in_scrollback_and_text_resumes_below_it()
    {
        var buffer = new TerminalBuffer(10, 3);
        var parser = new AnsiParser(buffer) { ImageDecoder = new FixedSizeImageDecoder(20, 80) };
        parser.Process("\x1b[3;5H" + Image);
        var image = Assert.Single(buffer.Images);
        Assert.Equal((4, 2, 4), (image.CellX, image.CellY, image.CellHeight));
        Assert.Equal(7, buffer.TotalLines);
        Assert.Equal((0, 2), (buffer.CursorCol, buffer.CursorRow));
        buffer.Lock.EnterReadLock();
        try { Assert.Single(buffer.GetVisibleImagesSnapshot(2, 3)); }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Regional_scroll_clips_pixels_and_keeps_rows_outside_the_margins(bool alt)
    {
        var buffer = new TerminalBuffer(10, 6);
        var parser = new AnsiParser(buffer) { ImageDecoder = new FixedSizeImageDecoder(20, 80) };
        parser.Process((alt ? "\x1b[?1049h" : "") + "header\x1b[6;1Hfooter\x1b[2;5r\x1b[5;3H" + Image);
        var image = Assert.Single(buffer.Images);
        Assert.Equal(0, image.CellY);
        Assert.Equal(1, image.ClipTopRows);
        Assert.Equal(0, image.ClipBottomRows);
        Assert.Equal((0, 4), (buffer.CursorCol, buffer.CursorRow));
        Assert.Equal(6, buffer.TotalLines);
        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal("h", buffer.GetGrapheme(0, 0));
            Assert.Equal("f", buffer.GetGrapheme(0, 5));
            Assert.Empty(buffer.GetVisibleImagesSnapshot(0, 1));
        }
        finally { buffer.Lock.ExitReadLock(); }
        parser.Process("\x1b[S\x1b[S\x1b[S");
        Assert.Empty(buffer.Images); // Fully clipped images are retired, not retained off-region.
        var retired = new List<object>();
        Assert.True(buffer.DrainRetiredImageHandles(retired, long.MaxValue));
        Assert.Same(image.ImageHandle, Assert.Single(retired));
    }

    [Fact]
    public void Alternate_screen_scrolling_never_moves_the_hidden_main_screen_image()
    {
        var buffer = new TerminalBuffer(10, 3);
        var parser = new AnsiParser(buffer) { ImageDecoder = new FixedSizeImageDecoder(20, 40) };
        parser.Process(Image);
        var main = Assert.Single(buffer.Images);
        int mainRow = main.CellY;
        parser.Process("\x1b[?1049h\x1b[3;1H" + Image);
        Assert.Equal(mainRow, main.CellY);
        var alt = Assert.Single(buffer.Images, i => i.IsAltScreenImage);
        Assert.Equal(0, alt.CellY + alt.ClipTopRows);
        parser.Process("\x1b[?1049l");
        buffer.Lock.EnterReadLock();
        try { Assert.Same(main.ImageHandle, Assert.Single(buffer.GetVisibleImagesSnapshot(0, 3)).ImageHandle); }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Fact]
    public void A_snapshot_preserves_display_mode_and_authoritative_metrics_mid_DCS()
    {
        var buffer = new TerminalBuffer(10, 5);
        var parser = new AnsiParser(buffer) { SixelCellSizeOverride = new SixelCellSize(20, 6) };
        parser.Process("\x1b[?80h\x1b[4;3H\x1bPq!30~");
        var snapshot = buffer.ExportState(0);
        snapshot.Parser = parser.ExportState();
        snapshot = TerminalStateSerializer.FromBytes(TerminalStateSerializer.ToBytes(snapshot));
        var restored = new TerminalBuffer(10, 5);
        var resumed = new AnsiParser(restored);
        TerminalStateTransfer.Restore(restored, resumed, snapshot);
        resumed.Process("-~\x1b\\");
        Assert.Equal((2, 3), (restored.CursorCol, restored.CursorRow));
        Assert.True(restored.Modes.IsSixelDisplayMode);
        Assert.True(restored.Modes.Clone().IsSixelDisplayMode);
        Assert.Equal(6, resumed.SixelCellHeightPx);
        resumed.Process("\x1b[?80l" + Image);
        Assert.Equal((0, 4), (restored.CursorCol, restored.CursorRow));
        Assert.Equal(6, restored.TotalLines);
    }

    [Theory]
    [InlineData(float.NaN, 6)]
    [InlineData(10, float.PositiveInfinity)]
    [InlineData(0, 6)]
    public void Invalid_snapshot_metrics_are_rejected_before_the_destination_is_changed(float width, float height)
    {
        var source = new TerminalBuffer(10, 5);
        var snapshot = source.ExportState(0);
        snapshot.Parser = new AnsiParser(source).ExportState();
        snapshot.Parser.SixelCellWidthPx = width;
        snapshot.Parser.SixelCellHeightPx = height;
        var target = new TerminalBuffer(20, 8);
        Assert.Throws<InvalidOperationException>(() => TerminalStateTransfer.Restore(target, new AnsiParser(target), snapshot));
        Assert.Equal((20, 8), (target.Cols, target.Rows));
    }

    [Theory]
    [InlineData("q!999999999999999999999999~", 2000, 6)]
    [InlineData("q\"1;1;999999;999999~", 1, 6)]
    [InlineData("q!20~$!2~-!3~", 20, 12)]
    [InlineData("q!0~", 0, 0)]
    [InlineData("q#1;2;100;0;0", 0, 0)]
    [InlineData("q!-~", 1, 12)]
    public void Headless_geometry_is_bounded_and_does_not_trust_declared_raster_sizes(string data, int width, int height)
    {
        Assert.Equal((width, height), SixelGeometry.Measure(data));
    }

    [Theory]
    [InlineData(10, 12)]
    [InlineData(20, 6)]
    public void The_shared_parity_oracle_detects_different_authoritative_SIXEL_metrics(float width, float height)
    {
        var expected = new TerminalBuffer(10, 5);
        var actual = new TerminalBuffer(10, 5);
        var a = new AnsiParser(expected) { SixelCellSizeOverride = new SixelCellSize(10, 6) };
        var b = new AnsiParser(actual) { SixelCellSizeOverride = new SixelCellSize(width, height) };
        var error = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => TerminalStateAssert.AssertEquivalent("metrics", expected, a, actual, b));
        Assert.Contains("parser state", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_shared_parity_oracle_detects_different_SIXEL_display_modes()
    {
        var expected = new TerminalBuffer(10, 5);
        var actual = new TerminalBuffer(10, 5);
        expected.Modes.IsSixelDisplayMode = true;
        var error = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            TerminalStateAssert.AssertEquivalent("display mode", expected, new AnsiParser(expected), actual, new AnsiParser(actual)));
        Assert.Contains("modes", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Repeated_page_origin_frames_retire_fully_covered_images_without_disposing_a_live_snapshot()
    {
        var buffer = new TerminalBuffer(10, 5);
        var parser = new AnsiParser(buffer) { ImageDecoder = new FixedSizeImageDecoder(30, 40) };
        parser.Process("\x1b[?80h" + Image);
        object original = Assert.Single(buffer.Images).ImageHandle;
        int snapshot = buffer.BeginSnapshotSession();
        try
        {
            for (int i = 0; i < 50; i++) parser.Process(Image);
            Assert.Single(buffer.Images);
            Assert.NotSame(original, buffer.Images[0].ImageHandle);
            var retired = new List<object>();
            buffer.DrainRetiredImageHandles(retired, long.MaxValue);
            Assert.Empty(retired);
        }
        finally { buffer.EndSnapshotSession(snapshot); }
        var ready = new List<object>();
        buffer.DrainRetiredImageHandles(ready, long.MaxValue);
        Assert.Equal(50, ready.Count);
        Assert.Contains(original, ready);
    }

    [Fact]
    public void Page_origin_replacement_preserves_uncovered_pixels_and_other_screen_images()
    {
        var buffer = new TerminalBuffer(10, 5);
        var parser = new AnsiParser(buffer) { ImageDecoder = new FixedSizeImageDecoder(40, 40) };
        parser.Process("\x1b[?80h" + Image);
        object main = Assert.Single(buffer.Images).ImageHandle;
        parser.ImageDecoder = new FixedSizeImageDecoder(20, 20);
        parser.Process(Image);
        Assert.Equal(2, buffer.Images.Count); // The older image still has uncovered pixels.
        parser.Process("\x1b[?1049h" + Image + Image);
        Assert.Equal(3, buffer.Images.Count); // Two hidden main images and one current alternate image.
        Assert.Contains(buffer.Images, image => ReferenceEquals(image.ImageHandle, main));
        Assert.Single(buffer.Images, image => image.IsAltScreenImage);
    }

    [Fact]
    public void Sixel_only_hosts_preserve_chunk_state_but_refuse_other_image_transports_and_probes()
    {
        var buffer = new TerminalBuffer(10, 5);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false)
        {
            ImageDecoder = new FixedSizeImageDecoder(30, 40),
            AllowNonSixelImages = false,
        };
        var headless = new AnsiParser(new TerminalBuffer(10, 5), forceConPtyFiltering: false) { AllowNonSixelImages = false };
        const string first = "\x1b]1339;Kitty:Ga=T,m=1;AA\x07";
        parser.Process(first);
        headless.Process(first);
        Assert.Equal(headless.ExportState().ToDebugString(), parser.ExportState().ToDebugString());
        parser.Process("\x1b]1339;Kitty:Gm=0;AA==\x07");
        headless.Process("\x1b]1339;Kitty:Gm=0;AA==\x07");
        Assert.Equal(headless.ExportState().ToDebugString(), parser.ExportState().ToDebugString());
        int reads = 0;
        parser.ReadFileBytes = _ => { reads++; return new byte[4]; };
        parser.Process("\x1b_Ga=T,t=f;L3RtcC9pbWFnZQ==\x1b\\\x1b]1337;File=inline=1:AQIDBA==\x07");
        Assert.Equal(0, reads);
        Assert.Empty(buffer.Images);
        var replies = new List<string>();
        parser.OnResponse += replies.Add;
        parser.Process("\x1b_Ga=q,i=7;\x1b\\");
        Assert.Equal("\x1b_Gi=7;ERR\x1b\\", Assert.Single(replies));
        parser.Process(Image);
        Assert.Single(buffer.Images); // SIXEL still decodes and advances normally.
    }

}
