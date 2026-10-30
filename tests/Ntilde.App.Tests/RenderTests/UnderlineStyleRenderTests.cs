using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Ntilde.Rendering;
using Ntilde.Shell;
using Ntilde.VT;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Ntilde.Tests.RenderTests;

/// <summary>
/// #270: the draw operation paints the underline in its style (SGR 4:x, 21) and color (SGR 58).
/// </summary>
/// <remarks>
/// Pixel checks on the band between the baseline and the cell's bottom, rendered at 2x so a stroke is a
/// couple of pixels wide. The text is "m", which has no descender, so everything lit in the band is the
/// underline. Each check compares against a straight underline drawn the same way rather than against
/// absolute coordinates, so it holds across the fonts the CI runners have.
/// </remarks>
public class UnderlineStyleRenderTests
{
    private const double Scaling = 2.0;
    private const int TextCells = 12;

    private static readonly CellMetrics Metrics = new()
    {
        CellWidth = 8.4f,
        CellHeight = 18.0f,
        Baseline = 14.0f,
        Ascent = 14.0f,
        Descent = 4.0f
    };

    private static readonly string Text = new('m', TextCells);

    [AvaloniaFact]
    public void Curly_SpansMoreRowsThanStraight()
    {
        using SKBitmap straight = Render("\x1b[4m" + Text);
        using SKBitmap curly = Render("\x1b[4:3m" + Text);

        int straightRows = LitRows(straight).Count;
        int curlyRows = LitRows(curly).Count;

        Assert.True(straightRows > 0, "no straight underline drawn");
        Assert.True(curlyRows >= straightRows + 2, $"curly covers {curlyRows} rows, straight {straightRows}");
    }

    [AvaloniaFact]
    public void Double_IsTwoSeparateLines()
    {
        using SKBitmap straight = Render("\x1b[4m" + Text);
        using SKBitmap doubled = Render("\x1b[4:2m" + Text);

        Assert.Equal(1, MaxSegmentsDownAColumn(straight));
        Assert.Equal(2, MaxSegmentsDownAColumn(doubled));
    }

    [AvaloniaFact]
    public void Sgr21_DrawsTheDoubleUnderline()
    {
        using SKBitmap doubled = Render("\x1b[21m" + Text);

        Assert.Equal(2, MaxSegmentsDownAColumn(doubled));
    }

    [AvaloniaTheory]
    [InlineData("\x1b[4:4m")]
    [InlineData("\x1b[4:5m")]
    public void DottedAndDashed_HaveGapsAlongTheLine(string style)
    {
        using SKBitmap straight = Render("\x1b[4m" + Text);
        using SKBitmap broken = Render(style + Text);

        Assert.Equal(0, GapsAlongBusiestRow(straight));
        Assert.True(GapsAlongBusiestRow(broken) >= 3, "the line has no gaps");
    }

    [AvaloniaFact]
    public void Sgr58TrueColor_ColorsTheUnderline_NotTheText()
    {
        using SKBitmap plain = Render("\x1b[4m" + Text);
        using SKBitmap red = Render("\x1b[4;58;2;255;0;0m" + Text);

        Assert.True(IsWhiteish(BrightestBandPixel(plain)), "a plain underline takes the text color");
        SKColor underline = BrightestBandPixel(red);
        Assert.True(underline.Red > 200 && underline.Green < 80 && underline.Blue < 80, $"underline is {underline}");
    }

    [AvaloniaFact]
    public void Sgr58Palette_ResolvesThroughTheTheme()
    {
        var theme = new TerminalTheme
        {
            Foreground = TermColor.White,
            Background = TermColor.Black,
            CursorColor = TermColor.White,
            Green = TermColor.FromRgb(0, 200, 0), // palette index 2
        };

        using SKBitmap green = Render("\x1b[4;58;5;2m" + Text, theme);

        SKColor underline = BrightestBandPixel(green);
        Assert.True(underline.Green > 150 && underline.Red < 80 && underline.Blue < 80, $"underline is {underline}");
    }

    [AvaloniaFact]
    public void AdjacentStyles_AreDrawnEachInItsOwn()
    {
        // A run ends where the underline's style changes, as it does where underline itself turns on or off.
        string half = new('m', TextCells / 2);
        using SKBitmap mixed = Render("\x1b[4:2m" + half + "\x1b[4:1m" + half);

        int middle = Bitmap(4.0 + ((TextCells / 2) * Metrics.CellWidth));
        Assert.Equal(2, MaxSegmentsDownAColumn(mixed, RunLeft, middle - 2));
        Assert.Equal(1, MaxSegmentsDownAColumn(mixed, middle + 2, RunRight));
    }

    [AvaloniaFact]
    public void Strikethrough_KeepsTheTextColor_BesideAColoredUnderline()
    {
        // Underline and strike share one paint; the underline's own color must not leak into the strike.
        // Periods leave the strike's height clear of glyph pixels.
        using SKBitmap bitmap = Render("\x1b[4;9;58;2;255;0;0m" + new string('.', TextCells));

        SKColor strike = BrightestPixel(bitmap, Bitmap(Metrics.CellHeight * 0.52) - 3, Bitmap(Metrics.CellHeight * 0.52) + 3);
        Assert.True(IsWhiteish(strike), $"strike is {strike}");
        Assert.True(BrightestBandPixel(bitmap).Red > 200, "the underline is red");
    }

    [AvaloniaTheory]
    [InlineData("\x1b[4:3m")]
    [InlineData("\x1b[4;58;2;255;0;0m")]
    public void StyledOrColoredUnderline_IsDrawnUnderBlanks(string style)
    {
        // An editor's squiggle on trailing whitespace: blanks only, and still meant to be seen.
        using SKBitmap bitmap = Render(style + new string(' ', TextCells));

        Assert.NotEmpty(LitRows(bitmap));
    }

    [AvaloniaFact]
    public void PlainUnderline_UnderBlanksOnly_StaysUndrawn()
    {
        // Unchanged since e25e6bd: a plain underline is trimmed to the visible text.
        using SKBitmap bitmap = Render("\x1b[4m" + new string(' ', TextCells));

        Assert.Empty(LitRows(bitmap));
    }

    [AvaloniaFact]
    public void PlainSgr4_StillDrawsTheSameStraightLine()
    {
        // The common case must not move: a straight underline in the text color, as before #270.
        using SKBitmap viaSgr4 = Render("\x1b[4m" + Text);
        using SKBitmap viaColon1 = Render("\x1b[4:1m" + Text);

        Assert.Equal(LitRows(viaSgr4), LitRows(viaColon1));
    }

    // ---------------------------------------------------------------- rendering

    private static SKBitmap Render(string sequence, TerminalTheme? theme = null)
    {
        const int cols = 20;
        const int rows = 2;
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
            op.DrawTerminalInternal(canvas);
            return bitmap;
        }
        finally
        {
            op.Dispose();
        }
    }

    // ---------------------------------------------------------------- pixel analysis (row 0)

    // From just below the baseline to the cell's bottom; the text cells minus one at each end, clear of trimming.
    private static int BandTop => Bitmap(Metrics.Baseline) + 1;
    private static int BandBottom => Bitmap(Metrics.CellHeight);
    private static int RunLeft => Bitmap(4.0 + Metrics.CellWidth);
    private static int RunRight => Bitmap(4.0 + ((TextCells - 1) * Metrics.CellWidth));

    private static int Bitmap(double logical) => (int)Math.Round(logical * Scaling, MidpointRounding.AwayFromZero);

    private static bool IsLit(SKColor c) => Math.Max(c.Red, Math.Max(c.Green, c.Blue)) > 96;

    private static bool IsWhiteish(SKColor c) => c.Red > 150 && c.Green > 150 && c.Blue > 150;

    private static SortedSet<int> LitRows(SKBitmap bitmap)
    {
        var rows = new SortedSet<int>();
        for (int y = BandTop; y < BandBottom; y++)
        {
            for (int x = RunLeft; x < RunRight; x++)
            {
                if (IsLit(bitmap.GetPixel(x, y)))
                {
                    rows.Add(y);
                    break;
                }
            }
        }

        return rows;
    }

    private static int MaxSegmentsDownAColumn(SKBitmap bitmap) => MaxSegmentsDownAColumn(bitmap, RunLeft, RunRight);

    private static int MaxSegmentsDownAColumn(SKBitmap bitmap, int left, int right)
    {
        int max = 0;
        for (int x = left; x < right; x++)
        {
            int segments = 0;
            bool inSegment = false;
            for (int y = BandTop; y < BandBottom; y++)
            {
                bool lit = IsLit(bitmap.GetPixel(x, y));
                if (lit && !inSegment) segments++;
                inSegment = lit;
            }

            max = Math.Max(max, segments);
        }

        return max;
    }

    private static int GapsAlongBusiestRow(SKBitmap bitmap)
    {
        int busiest = -1;
        int busiestCount = -1;
        for (int y = BandTop; y < BandBottom; y++)
        {
            int count = 0;
            for (int x = RunLeft; x < RunRight; x++)
            {
                if (IsLit(bitmap.GetPixel(x, y))) count++;
            }

            if (count > busiestCount)
            {
                busiestCount = count;
                busiest = y;
            }
        }

        int gaps = 0;
        bool previousLit = true;
        for (int x = RunLeft; x < RunRight; x++)
        {
            bool lit = IsLit(bitmap.GetPixel(x, busiest));
            if (!lit && previousLit) gaps++;
            previousLit = lit;
        }

        return gaps;
    }

    private static SKColor BrightestBandPixel(SKBitmap bitmap) => BrightestPixel(bitmap, BandTop, BandBottom);

    private static SKColor BrightestPixel(SKBitmap bitmap, int top, int bottom)
    {
        SKColor best = SKColors.Black;
        int bestSum = -1;
        for (int y = top; y < bottom; y++)
        {
            for (int x = RunLeft; x < RunRight; x++)
            {
                SKColor c = bitmap.GetPixel(x, y);
                int sum = c.Red + c.Green + c.Blue;
                if (sum > bestSum)
                {
                    bestSum = sum;
                    best = c;
                }
            }
        }

        return best;
    }
}
