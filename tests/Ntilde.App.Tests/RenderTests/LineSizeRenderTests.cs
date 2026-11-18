using Avalonia.Headless.XUnit;
using SkiaSharp;
using System;

namespace Ntilde.Tests.RenderTests;

/// <summary>
/// VT100 double-width and double-height lines (DECDWL <c>ESC # 6</c>, DECDHL <c>ESC # 3</c> / <c>ESC # 4</c>) through the
/// production draw path: ink and fill geometry, not golden images.
/// </summary>
public sealed class LineSizeRenderTests
{
    private const int Cols = 20;
    private const int Rows = 4;
    private const int Origin = 4;      // the grid's left padding, in pixels at scale 1
    private const int CellWidth = 8;   // 8.4 snapped to the device grid
    private const int CellHeight = 18;
    private const string HideCursor = "\u001b[?25l";
    private const string Dwl = "\u001b#6", Top = "\u001b#3", Bottom = "\u001b#4";

    private static int Width => (int)Math.Ceiling((Cols * DrawPathHarness.Metrics.CellWidth) + 8);

    private static byte[] Render(string sequence, bool withRowCache = false, int scrollOffset = 0)
    {
        using var harness = new DrawPathHarness(Cols, Rows, withRowCache);
        harness.Parser.Process(sequence);
        harness.ScrollOffset = scrollOffset;
        harness.DrawFrame();
        return harness.Pixels();
    }

    private static bool Lit(byte[] pixels, int x, int y)
    {
        int i = ((y * Width) + x) * 4;
        return Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2])) > 96;
    }

    /// <summary>The bounding box of everything brighter than the background inside rows [rowFrom, rowTo).</summary>
    private static SKRectI Ink(byte[] pixels, int rowFrom = 0, int rowTo = Rows)
    {
        int left = int.MaxValue, top = int.MaxValue, right = 0, bottom = 0;
        for (int y = rowFrom * CellHeight; y < rowTo * CellHeight; y++)
            for (int x = 0; x < Width; x++)
            {
                if (!Lit(pixels, x, y)) continue;
                left = Math.Min(left, x); top = Math.Min(top, y);
                right = Math.Max(right, x + 1); bottom = Math.Max(bottom, y + 1);
            }
        return right <= left ? SKRectI.Empty : new SKRectI(left, top, right, bottom);
    }

    [AvaloniaFact]
    public void A_double_width_line_doubles_ink_width_and_position_but_not_height()
    {
        SKRectI single = Ink(Render(HideCursor + "   M"));
        SKRectI doubled = Ink(Render(HideCursor + "   M" + Dwl));
        Assert.False(single.IsEmpty);
        Assert.InRange(doubled.Width, (single.Width * 2) - 3, (single.Width * 2) + 3);
        Assert.InRange(doubled.Left - Origin, ((single.Left - Origin) * 2) - 3, ((single.Left - Origin) * 2) + 3);
        Assert.InRange(doubled.Height, single.Height - 1, single.Height + 1);
        Assert.InRange(doubled.Top, single.Top - 1, single.Top + 1);
    }

    [AvaloniaFact]
    public void The_two_halves_of_a_double_height_line_make_one_glyph_two_rows_tall()
    {
        SKRectI single = Ink(Render(HideCursor + "   M"));
        byte[] pair = Render(HideCursor + Top + "   M\r\n" + Bottom + "   M");
        SKRectI whole = Ink(pair, 0, 2);
        Assert.InRange(whole.Width, (single.Width * 2) - 3, (single.Width * 2) + 3);
        Assert.InRange(whole.Height, (single.Height * 2) - 3, (single.Height * 2) + 3);
        Assert.InRange(whole.Top, (single.Top * 2) - 2, (single.Top * 2) + 2);

        // Each row holds its own half and both halves carry ink.
        SKRectI top = Ink(pair, 0, 1);
        SKRectI bottom = Ink(pair, 1, 2);
        Assert.False(top.IsEmpty);
        Assert.False(bottom.IsEmpty);
        Assert.Equal(CellHeight, top.Bottom);  // the glyph is cut at the row boundary, not ended above it
        Assert.Equal(CellHeight, bottom.Top);
    }

    [AvaloniaFact]
    public void A_half_drawn_alone_stays_inside_its_own_row()
    {
        byte[] topOnly = Render(HideCursor + "\r\n" + Top + "   M");
        Assert.True(Ink(topOnly, 0, 1).IsEmpty);
        Assert.False(Ink(topOnly, 1, 2).IsEmpty);
        Assert.True(Ink(topOnly, 2, 3).IsEmpty);

        byte[] bottomOnly = Render(HideCursor + "\r\n" + Bottom + "   M");
        Assert.True(Ink(bottomOnly, 0, 1).IsEmpty);
        Assert.False(Ink(bottomOnly, 1, 2).IsEmpty);
        Assert.True(Ink(bottomOnly, 2, 3).IsEmpty);

        // The top half shows the upper part of the glyph and the bottom half the lower part: not the same picture.
        Assert.NotEqual(topOnly, bottomOnly);
    }

    [AvaloniaFact]
    public void A_cell_background_on_a_double_width_line_covers_two_cells()
    {
        SKRectI single = Ink(Render(HideCursor + " \u001b[41m  \u001b[0m"));
        SKRectI doubled = Ink(Render(HideCursor + " \u001b[41m  \u001b[0m" + Dwl));
        Assert.Equal(new SKRectI(Origin + CellWidth, 0, Origin + (3 * CellWidth), CellHeight), single);
        Assert.Equal(new SKRectI(Origin + (2 * CellWidth), 0, Origin + (6 * CellWidth), CellHeight), doubled);
    }

    [AvaloniaFact]
    public void The_block_cursor_on_a_double_width_line_is_two_cells_wide()
    {
        const string steadyBlock = "\u001b[2 q";
        SKRectI single = Ink(Render(steadyBlock + "\u001b[1;4H"));
        SKRectI doubled = Ink(Render(steadyBlock + Dwl + "\u001b[1;4H"));
        Assert.Equal(new SKRectI(Origin + (3 * CellWidth), 0, Origin + (4 * CellWidth), CellHeight), single);
        Assert.Equal(new SKRectI(Origin + (6 * CellWidth), 0, Origin + (8 * CellWidth), CellHeight), doubled);
    }

    [AvaloniaFact]
    public void A_cursor_left_past_the_half_by_a_vertical_move_is_drawn_on_the_last_column()
    {
        const string steadyBlock = "\u001b[2 q";
        SKRectI cursor = Ink(Render(steadyBlock + "\u001b[2;1H" + Dwl + "\u001b[1;20H\u001b[B"));
        Assert.Equal(new SKRectI(Origin + (18 * CellWidth), CellHeight, Origin + (20 * CellWidth), 2 * CellHeight), cursor);
    }

    [AvaloniaFact]
    public void The_row_picture_cache_draws_the_same_pixels_and_follows_a_change_of_attribute()
    {
        const string content = HideCursor + Top + "  Banner\r\n" + Bottom + "  Banner\r\n" + Dwl + "wide \u001b[42m ok \u001b[0m\r\nplain";
        byte[] direct = Render(content);

        using var harness = new DrawPathHarness(Cols, Rows, withRowCache: true);
        harness.Parser.Process(content);
        harness.DrawFrame();
        Assert.Equal(direct, harness.Pixels());
        harness.DrawFrame(); // served from the cached row pictures
        Assert.Equal(direct, harness.Pixels());

        // A cell change inside a double-size row repaints it whole.
        harness.Parser.Process("\u001b[3;2HX");
        harness.DrawFrame();
        Assert.Equal(Render(content + "\u001b[3;2HX"), harness.Pixels());

        // Back to single width with no cell change at all.
        harness.Parser.Process("\u001b[3;1H\u001b#5");
        harness.DrawFrame();
        Assert.Equal(Render(content + "\u001b[3;2HX\u001b[3;1H\u001b#5"), harness.Pixels());
    }

    [AvaloniaFact]
    public void A_double_width_line_keeps_its_size_in_history()
    {
        SKRectI single = Ink(Render(HideCursor + "   M"));
        // Four line feeds push the first two lines into history; scrolled back by one, the second is the top row.
        const string feeds = "\r\n\r\n\r\n\r\n";
        Assert.Equal(single, Ink(Render(HideCursor + "\r\n   M" + feeds, scrollOffset: 1), 0, 1));
        SKRectI inHistory = Ink(Render(HideCursor + "\r\n   M" + Dwl + feeds, scrollOffset: 1), 0, 1);
        Assert.InRange(inHistory.Width, (single.Width * 2) - 3, (single.Width * 2) + 3);
    }
}
