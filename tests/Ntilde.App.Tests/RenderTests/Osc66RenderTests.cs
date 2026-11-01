using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Ntilde.Rendering;
using Ntilde.Shell;
using Ntilde.VT;
using SkiaSharp;
using System;
using System.Collections.Concurrent;

namespace Ntilde.Tests.RenderTests;

public sealed class Osc66RenderTests
{
    private const double Scaling = 2.0;
    private static readonly CellMetrics Metrics = new()
    {
        CellWidth = 8.4f,
        CellHeight = 18,
        Baseline = 14,
        Ascent = 14,
        Descent = 4
    };

    [AvaloniaFact]
    public void Scaled_ink_spans_both_occupied_rows()
    {
        using var ordinary = Render("\x1b]66;s=1:w=1;M\a");
        using var scaled = Render("\x1b]66;s=2;M\a");
        var normal = Ink(ordinary);
        var large = Ink(scaled);
        Assert.True(large.Width > normal.Width * 1.5f, $"normal={normal}, scaled={large}");
        Assert.True(large.Height > normal.Height * 1.5f);
        Assert.True(large.Bottom > Metrics.CellHeight * Scaling);
    }

    [AvaloniaFact]
    public void Fraction_and_alignment_move_ink_inside_unchanged_geometry()
    {
        using var full = Render("\x1b]66;s=2:w=2;M\a");
        using var topLeft = Render("\x1b]66;s=2:w=2:n=1:d=2;M\a");
        using var bottomRight = Render("\x1b]66;s=2:w=2:n=1:d=2:v=1:h=1;M\a");
        var large = Ink(full);
        var small = Ink(topLeft);
        var aligned = Ink(bottomRight);
        Assert.True(small.Width < large.Width * 0.75f);
        Assert.True(small.Height < large.Height * 0.75f);
        Assert.True(aligned.Left > small.Left + Metrics.CellWidth * Scaling);
        Assert.True(aligned.Top > small.Top + Metrics.CellHeight * Scaling * 0.75f);
    }

    [AvaloniaFact]
    public void Fractional_pair_uses_two_small_glyphs_in_one_cell_instead_of_shrinking_the_whole_string_again()
    {
        using var single = Render("\x1b]66;n=1:d=2:w=1;M\a");
        using var pair = Render("\x1b]66;n=1:d=2:w=1;MM\a");
        Assert.True(Ink(pair).Width > Ink(single).Width * 1.2f);
    }

    [AvaloniaFact]
    public void Explicit_width_compresses_the_entire_payload_and_clips_at_the_block_edge()
    {
        using var bitmap = Render("\x1b]66;s=2:w=1;MMMMMMMMMMMMMMMM\a");
        var ink = Ink(bitmap);
        Assert.True(ink.Width > 2);
        Assert.True(ink.Right <= (4 + Metrics.CellWidth * 2) * Scaling + 1);
        Assert.True(ink.Bottom > Metrics.CellHeight * Scaling);
    }

    [AvaloniaFact]
    public void Visible_lower_fragment_draws_when_the_anchor_is_in_scrollback()
    {
        using var bitmap = Render("\x1b]66;s=2;M\a\x1b[4;1H\n");
        var ink = Ink(bitmap);
        Assert.True(ink.Height > 0);
        Assert.True(ink.Bottom <= Metrics.CellHeight * Scaling);
    }

    [AvaloniaFact]
    public void Cached_rows_refresh_after_combining_text_changes()
    {
        using var harness = new DrawPathHarness(20, 4);
        harness.Parser.Process("\x1b]66;s=2;A\a");
        harness.DrawFrame();
        byte[] before = harness.Pixels();
        harness.Parser.Process("\u0332");
        harness.DrawFrame();
        Assert.NotEqual(before, harness.Pixels());
    }

    [AvaloniaFact]
    public void Italic_shear_keeps_the_same_baseline_anchor_as_ordinary_italic_text()
    {
        using var ordinary = Render("\x1b[3mI");
        using var sized = Render("\x1b[3m\x1b]66;w=1;I\a");
        var normal = Ink(ordinary);
        var actual = Ink(sized);
        Assert.True(actual.Width > 0);
        Assert.InRange(actual.Left, normal.Left - 1, normal.Left + 1);
    }

    [AvaloniaTheory]
    [InlineData("\x1b[4m", false)]
    [InlineData("\x1b[4:3m", true)]
    [InlineData("\x1b[4;58:5:1m", true)]
    public void Sized_blank_underlines_follow_the_ordinary_plain_vs_styled_policy(string style, bool visible)
    {
        using var bitmap = Render(style + "\x1b]66;s=2:w=2;  \a");
        Assert.Equal(visible, Ink(bitmap).Width > 0);
    }

    [AvaloniaFact]
    public void A_plain_underline_still_draws_below_visible_sized_text()
    {
        using var bitmap = Render("\x1b[4m\x1b]66;s=2:w=2;M\a");
        bool line = false;
        for (int y = (int)(Metrics.Baseline * 2 * Scaling) + 2; y < Metrics.CellHeight * 2 * Scaling; y++)
            for (int x = 8; x < (4 + Metrics.CellWidth * 4) * Scaling; x++)
            {
                var c = bitmap.GetPixel(x, y);
                line |= Math.Max(c.Red, Math.Max(c.Green, c.Blue)) > 96;
            }
        Assert.True(line);
    }

    private static SKRectI Ink(SKBitmap bitmap)
    {
        int left = bitmap.Width, right = 0, top = bitmap.Height, bottom = 0;
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                var c = bitmap.GetPixel(x, y);
                if (Math.Max(c.Red, Math.Max(c.Green, c.Blue)) <= 96) continue;
                left = Math.Min(left, x); top = Math.Min(top, y);
                right = Math.Max(right, x + 1); bottom = Math.Max(bottom, y + 1);
            }
        return right <= left ? SKRectI.Empty : new SKRectI(left, top, right, bottom);
    }

    private static SKBitmap Render(string sequence, TerminalTheme? theme = null)
    {
        const int cols = 20;
        const int rows = 4;
        int width = (int)Math.Ceiling(((cols * Metrics.CellWidth) + 8) * Scaling);
        int height = (int)Math.Ceiling(rows * Metrics.CellHeight * Scaling);

        var buffer = new TerminalBuffer(cols, rows)
        {
            Theme = theme ?? new TerminalTheme
            {
                Foreground = TermColor.White,
                Background = TermColor.Black,
                CursorColor = TermColor.White
            }
        };
        new AnsiParser(buffer).Process(sequence);

        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);
        canvas.Scale((float)Scaling);

        var typeface = new Typeface("Cascadia Code, Consolas, Monospace");
        var skTypeface = new SharedSKTypeface(SKTypeface.FromFamilyName(typeface.FontFamily.Name));
        var skFont = new SharedSKFont(new SKFont(skTypeface.Typeface, 14));
        using var glyphCache = new GlyphCache();

        var op = new TerminalDrawOperation(
            new Rect(0, 0, width / Scaling, height / Scaling),
            buffer,
            scrollOffset: 0,
            selection: new SelectionState(),
            searchMatches: null,
            activeSearchIndex: -1,
            metrics: Metrics,
            typeface: typeface,
            fontSize: 14,
            glyphTypeface: typeface.GlyphTypeface,
            skTypeface: skTypeface,
            skFont: skFont,
            enableLigatures: false,
            fallbackCache: new ConcurrentDictionary<string, SKTypeface?>(),
            fallbackChain: Array.Empty<SKTypeface>(),
            opacity: 1.0,
            hideCursor: true,
            renderScaling: Scaling,
            snapshotRows: buffer.Rows,
            snapshotCols: buffer.Cols,
            totalLines: buffer.TotalLines,
            cursorRow: buffer.CursorRow,
            cursorCol: buffer.CursorCol,
            rowCache: null,
            enableComplexShaping: true,
            glyphCache: glyphCache);

        try
        {
            using TerminalRenderSnapshot? snapshot = op.DrawTerminalInternal(canvas);
            return bitmap;
        }
        finally
        {
            op.Dispose();
        }
    }

}
