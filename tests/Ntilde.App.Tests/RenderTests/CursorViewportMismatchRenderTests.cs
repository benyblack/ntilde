using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Ntilde.Platform;
using Ntilde.Rendering;
using Ntilde.Shell;
using Ntilde.VT;
using SkiaSharp;
using System.Collections.Concurrent;
using Xunit;

namespace Ntilde.Tests.RenderTests;

/// <summary>
/// The view's row count (<see cref="TerminalView.Rows"/>, taken from its bounds) moves on layout, but the buffer
/// only follows once the throttled resize dispatches, or, for a session that orders resizes in its stream, once the
/// session applies it. Frames drawn in between show more or fewer rows than the buffer has. The cursor must stay
/// on the cell it is in: it used to be placed as if the buffer had the view's row count, which put it one row
/// above the prompt for every row the pane had just grown (seen when focus mode grew a filled pane), and dropped
/// it entirely while a shrink was pending.
/// </summary>
[Trait("Lane", "PlatformBoot")]
[Collection("GoldenPng")]
public sealed class CursorViewportMismatchRenderTests
{
    private const int CellWidth = 10;
    private const int CellHeight = 20;
    private const int PaddingLeft = 4;

    [AvaloniaTheory]
    // Main screen, one row of scrollback: a taller view shows that row above the viewport, so the prompt row
    // (buffer row 1) is drawn at visual row 2.
    [InlineData(false, 3, 2)]
    // Main screen, shrink not yet applied: the view shows only the bottom row, which holds the cursor.
    [InlineData(false, 1, 0)]
    // Alt screen has no scrollback: a taller view leaves the extra row blank at the bottom and the cursor where
    // the application put it.
    [InlineData(true, 3, 1)]
    public void Cursor_stays_on_its_cell_while_the_view_and_buffer_row_counts_differ(bool altScreen, int viewRows, int expectedVisualRow)
    {
        var buffer = new TerminalBuffer(3, 2)
        {
            Theme = new TerminalTheme { Background = TermColor.Black, Foreground = TermColor.White, CursorColor = TermColor.FromRgb(255, 0, 0) }
        };
        var parser = new AnsiParser(buffer);
        parser.Process(altScreen ? "\u001b[?1049h\u001b[2;2H" : "a\r\nb\r\nc");
        Assert.Equal(1, buffer.CursorRow);
        Assert.Equal(1, buffer.CursorCol);

        using var bitmap = Draw(buffer, viewRows);

        int x = PaddingLeft + CellWidth + CellWidth / 2;
        for (int row = 0; row < viewRows; row++)
        {
            var pixel = bitmap.GetPixel(x, row * CellHeight + CellHeight / 2);
            if (row == expectedVisualRow)
                Assert.True(pixel == SKColors.Red, $"cursor missing from visual row {row}: {pixel}");
            else
                Assert.True(pixel != SKColors.Red, $"cursor drawn on visual row {row}, expected row {expectedVisualRow}");
        }
    }

    private static SKBitmap Draw(TerminalBuffer buffer, int viewRows)
    {
        var typeface = new Typeface("Cascadia Code, Consolas, Monospace");
        var skTypeface = new SharedSKTypeface(SKTypeface.FromFamilyName(typeface.FontFamily.Name));
        var skFont = new SharedSKFont(new SKFont(skTypeface.Typeface, 14));
        int width = PaddingLeft + 3 * CellWidth + 4;
        int height = viewRows * CellHeight;
        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);
        using var operation = new TerminalDrawOperation(
            new Rect(0, 0, width, height), buffer, 0, new SelectionState(), null, -1,
            new CellMetrics { CellWidth = CellWidth, CellHeight = CellHeight, Baseline = 15, Ascent = 15, Descent = 5 },
            typeface, 14, typeface.GlyphTypeface, skTypeface, skFont, false,
            new ConcurrentDictionary<string, SKTypeface?>(), [], 1, false,
            snapshotRows: viewRows, snapshotCols: 3, totalLines: buffer.TotalLines,
            cursorRow: buffer.CursorRow, cursorCol: buffer.CursorCol, preferredCursorStyle: CursorStyle.Block);
        skFont.Dispose(); // operation owns the remaining references
        skTypeface.Dispose();
        using var snapshot = operation.DrawTerminalInternal(canvas);
        return bitmap;
    }
}
