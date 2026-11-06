using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Ntilde.VT;
using Xunit;

namespace Ntilde.VT.Tests;

/// <summary>
/// <c>CSI 14 t</c>, <c>CSI 16 t</c> and image placement measure a cell in one unit. The App sets
/// <see cref="AnsiParser.CellWidth"/> to the logical cell and <see cref="AnsiParser.SixelCellSizeOverride"/>
/// to device pixels, so on a 2x display a direct pane has an 8.5 x 18.5 cell and a 17 x 37 override.
/// The reports used the logical cell while Sixel placement used the override, and notcurses, which
/// sizes Sixel output from <c>CSI 16 t</c>, drew images at half size in direct panes (persistent panes
/// already agreed, because the daemon's parser has the device size in both). Now both reports and every
/// image protocol use the override when the host set one.
/// </summary>
public sealed class ImageCellPixelGeometryTests
{
    private const float LogicalWidth = 8.5f;
    private const float LogicalHeight = 18.5f;
    private static readonly SixelCellSize Device = new(17, 37);

    private static (TerminalBuffer Buffer, AnsiParser Parser, List<string> Responses) Create(IImageDecoder? decoder = null)
    {
        // 140 x 41 is the pane the bug was seen in.
        var buffer = new TerminalBuffer(140, 41);
        var responses = new List<string>();
        // forceConPtyFiltering: false, so a native APC kitty image is placed on every platform.
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false)
        {
            OnResponse = responses.Add,
            ImageDecoder = decoder,
            CellWidth = LogicalWidth,
            CellHeight = LogicalHeight,
        };
        return (buffer, parser, responses);
    }

    [Fact]
    public void Csi16t_reports_the_host_cell_size_rather_than_the_logical_cell()
    {
        var (_, parser, responses) = Create();
        parser.SixelCellSizeOverride = Device;

        parser.Process("\x1b[16t");

        Assert.Equal("\x1b[6;37;17t", Assert.Single(responses));
    }

    [Fact]
    public void Csi14t_reports_the_text_area_in_the_host_cell_size()
    {
        var (_, parser, responses) = Create();
        parser.SixelCellSizeOverride = Device;

        parser.Process("\x1b[14t");

        // 41 rows * 37 = 1517 px tall, 140 cols * 17 = 2380 px wide.
        Assert.Equal("\x1b[4;1517;2380t", Assert.Single(responses));
    }

    [Fact]
    public void Without_a_host_cell_size_the_reports_use_the_parser_cell()
    {
        // Hosts that never set the override (tests, tools) keep the old answer. These are the
        // 8 x 18 px notcurses was told in a direct pane before the fix.
        var (_, parser, responses) = Create();

        parser.Process("\x1b[16t\x1b[14t");

        Assert.Equal(new[] { "\x1b[6;18;8t", "\x1b[4;758;1190t" }, responses);
    }

    [Fact]
    public void The_reports_follow_a_scale_change()
    {
        var (_, parser, responses) = Create();
        parser.SixelCellSizeOverride = Device;
        parser.Process("\x1b[16t");

        // Moved to a 1.5x monitor: 8.5 x 18.5 logical is 12.75 x 27.75 device pixels.
        parser.SixelCellSizeOverride = new SixelCellSize(12.75f, 27.75f);
        parser.Process("\x1b[16t");

        Assert.Equal(new[] { "\x1b[6;37;17t", "\x1b[6;28;13t" }, responses);
    }

    [Theory]
    [InlineData(float.NaN, 0f, "\x1b[6;20;10t")]
    [InlineData(0.25f, 0.5f, "\x1b[6;1;1t")]
    public void An_unusable_host_cell_size_falls_back_as_sixel_placement_does(float width, float height, string expected)
    {
        var (_, parser, responses) = Create();
        parser.SixelCellSizeOverride = new SixelCellSize(width, height);

        parser.Process("\x1b[16t");

        Assert.Equal(expected, Assert.Single(responses));
    }

    // An image drawn for 20 x 4 cells from the CSI 16 t reply (a little short of 4 rows, so the
    // height is not an exact multiple) covers exactly those cells in every protocol.
    private const int Columns = 20;
    private const int Rows = 4;

    private static (int Width, int Height) ImagePixelsFromCsi16t(AnsiParser parser, List<string> responses)
    {
        parser.Process("\x1b[16t");
        Match reply = Regex.Match(Assert.Single(responses), @"^\x1b\[6;(\d+);(\d+)t$");
        Assert.True(reply.Success);
        responses.Clear();
        int cellHeight = int.Parse(reply.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        int cellWidth = int.Parse(reply.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        return (Columns * cellWidth, Rows * cellHeight - 10);
    }

    [Fact]
    public void A_sixel_image_sized_from_csi_16t_fills_the_cells_it_was_drawn_for()
    {
        // The query runs on a parser of its own so the decoder below can be built with the answer.
        var probe = Create();
        probe.Parser.SixelCellSizeOverride = Device;
        var (width, height) = ImagePixelsFromCsi16t(probe.Parser, probe.Responses);
        var (buffer, parser, _) = Create(new FixedSizeImageDecoder(width, height));
        parser.SixelCellSizeOverride = Device;

        parser.Process("\x1b[?80h\x1bPq!16~\x1b\\");

        TerminalImage image = Assert.Single(buffer.Images);
        Assert.Equal((Columns, Rows), (image.CellWidth, image.CellHeight));
    }

    [Fact]
    public void A_kitty_image_sized_from_csi_16t_fills_the_cells_it_was_drawn_for()
    {
        var probe = Create();
        probe.Parser.SixelCellSizeOverride = Device;
        var (width, height) = ImagePixelsFromCsi16t(probe.Parser, probe.Responses);
        var (buffer, parser, _) = Create(new FixedSizeImageDecoder(width, height));
        parser.SixelCellSizeOverride = Device;
        string payload = TestImages.PngBase64(width, height); // placed from its header (#838)

        // No c= or r=: the cell count comes from the image's pixels. Measured against the logical
        // 8.5 px cell this was 40 columns, twice what the client asked for.
        parser.Process($"\x1b_Ga=T,f=100;{payload}\x1b\\");

        TerminalImage image = Assert.Single(buffer.Images);
        Assert.Equal((Columns, Rows), (image.CellWidth, image.CellHeight));
    }

    [Fact]
    public void A_kitty_pixel_width_is_measured_in_the_host_cell_size()
    {
        var (buffer, parser, _) = Create(new FixedSizeImageDecoder(50, 70));
        parser.SixelCellSizeOverride = Device;
        string payload = TestImages.PngBase64(50, 70);

        // w=170 is 170 / 17 = 10 columns; the height follows the aspect ratio: ceil(10 * 17/37 * 1.4) = 7.
        parser.Process($"\x1b_Gw=170,a=T,f=100;{payload}\x1b\\");

        TerminalImage image = Assert.Single(buffer.Images);
        Assert.Equal((10, 7), (image.CellWidth, image.CellHeight));
    }

    [Fact]
    public void An_iterm2_image_sized_from_csi_16t_fills_the_cells_it_was_drawn_for()
    {
        var probe = Create();
        probe.Parser.SixelCellSizeOverride = Device;
        var (width, height) = ImagePixelsFromCsi16t(probe.Parser, probe.Responses);
        var (buffer, parser, _) = Create(new FixedSizeImageDecoder(width, height));
        parser.SixelCellSizeOverride = Device;
        string payload = TestImages.PngBase64(width, height); // placed from its header (#838)

        parser.Process($"\x1b]1337;File=inline=1:{payload}\x07");

        TerminalImage image = Assert.Single(buffer.Images);
        Assert.Equal((Columns, Rows), (image.CellWidth, image.CellHeight));
    }
}
