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

[Trait("Lane", "PlatformBoot")]
[Collection("GoldenPng")]
public sealed class CursorResetRenderTests
{
    [AvaloniaFact]
    public void DECSTR_releases_the_application_cursor_override_and_uses_the_host_preference()
    {
        var buffer = new TerminalBuffer(3, 2)
        {
            Theme = new TerminalTheme { Background = TermColor.Black, Foreground = TermColor.White, CursorColor = TermColor.FromRgb(255, 0, 0) }
        };
        var parser = new AnsiParser(buffer);
        parser.Process("\u001b[6 q"); // application requests a steady beam, host prefers block
        using (var overridden = Draw(buffer, CursorStyle.Block))
            Assert.Equal(SKColors.Black, overridden.GetPixel(9, 10)); // outside the beam
        parser.Process("\u001b[!p");
        using var reset = Draw(buffer, CursorStyle.Block);
        Assert.Equal(SKColors.Red, reset.GetPixel(9, 10)); // the configured block covers the cell centre
        Assert.True(buffer.Modes.CursorStyleIsDefault);
        Assert.True(buffer.Modes.IsCursorBlinkEnabled);
    }

    private static SKBitmap Draw(TerminalBuffer buffer, CursorStyle preference)
    {
        var typeface = new Typeface("Cascadia Code, Consolas, Monospace");
        var skTypeface = new SharedSKTypeface(SKTypeface.FromFamilyName(typeface.FontFamily.Name));
        var skFont = new SharedSKFont(new SKFont(skTypeface.Typeface, 14));
        var bitmap = new SKBitmap(38, 40);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);
        using var operation = new TerminalDrawOperation(
            new Rect(0, 0, 38, 40), buffer, 0, new SelectionState(), null, -1,
            new CellMetrics { CellWidth = 10, CellHeight = 20, Baseline = 15, Ascent = 15, Descent = 5 },
            typeface, 14, typeface.GlyphTypeface, skTypeface, skFont, false,
            new ConcurrentDictionary<string, SKTypeface?>(), [], 1, false,
            snapshotRows: 2, snapshotCols: 3, totalLines: buffer.TotalLines,
            cursorRow: buffer.CursorRow, cursorCol: buffer.CursorCol, preferredCursorStyle: preference);
        skFont.Dispose(); // operation owns the remaining references
        skTypeface.Dispose();
        using var snapshot = operation.DrawTerminalInternal(canvas);
        return bitmap;
    }
}
