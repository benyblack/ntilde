using Avalonia.Headless.XUnit;
using Ntilde.Rendering;
using Ntilde.Shell;
using Ntilde.VT;
using SkiaSharp;
using System;
using Xunit;

namespace Ntilde.Tests.RenderTests;

[Collection("GoldenPng")]
[Trait("Lane", "PlatformBoot")]
public sealed class BoldGlyphPixelTests
{
    private static readonly CellMetrics Metrics = new()
    {
        CellWidth = 9,
        CellHeight = 22,
        Baseline = 17,
        Ascent = 17,
        Descent = 5
    };

    [AvaloniaTheory]
    [InlineData(false, false, 1.0)]
    [InlineData(true, false, 1.0)]
    [InlineData(false, true, 1.0)]
    [InlineData(true, true, 1.0)]
    [InlineData(false, false, 1.5)]
    [InlineData(true, false, 1.5)]
    public void Explicit_RGB_bold_changes_ink_and_SGR22_restores_normal(bool cached, bool italic, double scale)
    {
        var buffer = new TerminalBuffer(20, 2);
        var parser = new AnsiParser(buffer);
        using var cache = cached ? new GlyphCache() : null;
        var options = new TerminalSnapshotOptions
        {
            TypefaceFamily = BundledFontCatalog.DefaultTerminalFontFamily,
            FontResolution = SnapshotFontResolution.LiveParity,
            HideCursor = true,
            FillBackground = true,
            GlyphCache = cache,
            RenderScaling = scale
        };
        string prefix = "\x1b[38;2;96;192;240m" + (italic ? "\x1b[3m" : "");
        parser.Process("\x1b[2;1Hcontrol\x1b[1;1H" + prefix + "HMWqg_|");
        using var normal = Capture();
        parser.Process("\x1b[1;1H\x1b[1mHMWqg_|");
        using var bold = Capture();
        parser.Process("\x1b[1;1H\x1b[22mHMWqg_|");
        using var restored = Capture();
        Assert.Equal(Bytes(normal), Bytes(restored));
        Assert.NotEqual(Bytes(normal), Bytes(bold));
        Assert.Equal(Row(normal, 1), Row(bold, 1));
        Assert.True(Ink(normal) > 0, "The normal control must contain actual glyph pixels.");
        Assert.True(Ink(bold) > Ink(normal), "Bold must thicken glyph coverage, not merely change a stored flag.");

        SKBitmap Capture() => TerminalSnapshotRenderer.Capture(buffer, Metrics, 188, 44, options);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Adjacent_RGB_runs_keep_normal_bold_and_reset_separate(bool cached)
    {
        var buffer = new TerminalBuffer(20, 2);
        var parser = new AnsiParser(buffer);
        using var cache = cached ? new GlyphCache() : null;
        var options = new TerminalSnapshotOptions
        {
            TypefaceFamily = BundledFontCatalog.DefaultTerminalFontFamily,
            FontResolution = SnapshotFontResolution.LiveParity,
            HideCursor = true,
            FillBackground = true,
            GlyphCache = cache
        };
        // Hidden blanks isolate the reference glyphs at exact cell anchors even on the direct-run path.
        parser.Process("\x1b[38;2;96;192;240mM\x1b[8m  \x1b[28mM\x1b[8m  \x1b[28mM");
        using var control = TerminalSnapshotRenderer.Capture(buffer, Metrics, 188, 44, options);
        parser.Process("\x1b[1;1H\x1b[28mM  \x1b[1mM  \x1b[22mM");
        using var mixed = TerminalSnapshotRenderer.Capture(buffer, Metrics, 188, 44, options);
        Assert.Equal(Region(control, 0, 3), Region(mixed, 0, 3));
        Assert.NotEqual(Region(control, 3, 3), Region(mixed, 3, 3));
        // A normal run after bold must not inherit the bold font's mutable state.
        Assert.Equal(Region(control, 6, 3), Region(mixed, 6, 3));
    }

    [AvaloniaFact]
    public void Sized_text_uses_bold_without_leaking_into_the_next_block()
    {
        using var normal = RenderSized(false);
        using var bold = RenderSized(true);
        Assert.NotEqual(Region(normal, 0, 4), Region(bold, 0, 4));
        Assert.Equal(Region(normal, 8, 4), Region(bold, 8, 4));

        static SKBitmap RenderSized(bool bold)
        {
            var buffer = new TerminalBuffer(20, 2);
            new AnsiParser(buffer).Process("\x1b[38;2;96;192;240m" + (bold ? "\x1b[1m" : "") +
                "\x1b]66;w=4;MMMM\a\x1b[22m\x1b[1;9H\x1b]66;w=4;MMMM\a");
            return TerminalSnapshotRenderer.Capture(buffer, Metrics, 188, 44, new TerminalSnapshotOptions
            {
                TypefaceFamily = BundledFontCatalog.DefaultTerminalFontFamily,
                FontResolution = SnapshotFontResolution.LiveParity,
                HideCursor = true,
                FillBackground = true
            });
        }
    }

    private static byte[] Region(SKBitmap bitmap, int column, int width)
    {
        var result = new byte[width * 9 * 22 * 4];
        int offset = 0;
        for (int y = 0; y < 22; y++)
            for (int x = 4 + column * 9; x < 4 + (column + width) * 9; x++)
            {
                var c = bitmap.GetPixel(x, y);
                result[offset++] = c.Red; result[offset++] = c.Green;
                result[offset++] = c.Blue; result[offset++] = c.Alpha;
            }
        return result;
    }

    private static byte[] Bytes(SKBitmap bitmap) => bitmap.Bytes;
    private static byte[] Row(SKBitmap bitmap, int row)
    {
        int y = (int)Math.Round(row * Metrics.CellHeight * bitmap.Height / 44.0);
        return bitmap.Bytes.AsSpan(y * bitmap.RowBytes).ToArray();
    }
    private static long Ink(SKBitmap bitmap)
    {
        long total = 0;
        int height = bitmap.Height / 2;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                var color = bitmap.GetPixel(x, y);
                total += color.Red + color.Green + color.Blue;
            }
        return total;
    }
}
