using Ntilde.Platform;
using Ntilde.Rendering;
using Ntilde.Shell;
using Ntilde.Tests.Infra;
using Ntilde.VT;
using SkiaSharp;
using Xunit;

namespace Ntilde.Tests.RenderTests;

[Trait("Lane", "PlatformBoot")]
[Collection("GoldenPng")]
public sealed class SixelScrollingRenderTests
{
    private static bool HasSkia()
    {
        try { using var surface = SKSurface.Create(new SKImageInfo(1, 1)); return surface is not null; }
        catch { return false; }
    }

    [Fact]
    public void Deferred_page_origin_replacement_preserves_uncovered_pixels_when_first_rendered()
    {
        Assert.SkipUnless(HasSkia(), "SkiaSharp native library not available on this platform.");
        var buffer = new TerminalBuffer(10, 6);
        var parser = new AnsiParser(buffer)
        {
            ImageDecoder = new SkiaImageDecoder { DeferSixelDecoding = true },
            CellWidth = 10,
            CellHeight = 20,
        };
        parser.Process("\x1b[?80h\x1bPq#1;2;100;0;0#1!30~\x1b\\\x1bPq#1;2;0;0;100#1!10~\x1b\\");
        Assert.Equal(2, buffer.Images.Count);
        var metrics = new CellMetrics { CellWidth = 10, CellHeight = 20, Baseline = 15, Ascent = 15, Descent = 5 };
        using var rendered = SKBitmap.Decode(SnapshotService.CapturePng(buffer, metrics, 100, 120,
            new SnapshotCaptureOptions { HideCursor = true }));
        Assert.Equal(SKColors.Blue, rendered.GetPixel(5, 3));
        Assert.Equal(SKColors.Red, rendered.GetPixel(25, 3));
    }

    [Fact]
    public void A_scrolled_SIXEL_clips_at_the_region_and_keeps_the_original_bitmap_sampling()
    {
        Assert.SkipUnless(HasSkia(), "SkiaSharp native library not available on this platform.");
        var buffer = new TerminalBuffer(10, 6);
        var parser = new AnsiParser(buffer) { ImageDecoder = new SkiaImageDecoder(), CellWidth = 10, CellHeight = 20 };
        string red = string.Join('-', Enumerable.Repeat("!10~", 5));
        string blue = string.Join('-', Enumerable.Repeat("!10~", 5));
        parser.Process("\x1b[2;5r\x1b[5;1H\x1bPq#1;2;100;0;0#1" + red + "-#2;2;0;0;100#2" + blue + "\x1b\\\x1b[S");
        var image = Assert.Single(buffer.Images);
        Assert.Equal(1, image.ClipTopRows);
        using var source = Assert.IsType<SKBitmap>(image.ImageHandle);
        var metrics = new CellMetrics { CellWidth = 10, CellHeight = 20, Baseline = 15, Ascent = 15, Descent = 5 };
        byte[] png = SnapshotService.CapturePng(buffer, metrics, 100, 120, new SnapshotCaptureOptions { HideCursor = true });
        using var rendered = SKBitmap.Decode(png);
        Assert.NotEqual(SKColors.Red, rendered.GetPixel(5, 5)); // No graphics leak above the scrolling margin.
        Assert.Equal(SKColors.Red, rendered.GetPixel(5, 25)); // Original source row 25, not a stretched crop.
        Assert.Equal(SKColors.Blue, rendered.GetPixel(5, 35));
        Assert.Equal(SKColors.Blue, rendered.GetPixel(5, 55));
    }
    [Fact]
    public void Display_mode_clips_at_the_original_right_page_edge_even_after_a_resize()
    {
        Assert.SkipUnless(HasSkia(), "SkiaSharp native library not available on this platform.");
        var buffer = new TerminalBuffer(10, 6);
        var parser = new AnsiParser(buffer) { ImageDecoder = new SkiaImageDecoder(), CellWidth = 10, CellHeight = 20 };
        parser.Process("\x1b[?80h\x1bPq#1;2;100;0;0#1!200~\x1b\\");
        var image = Assert.Single(buffer.Images);
        using var source = Assert.IsType<SKBitmap>(image.ImageHandle);
        Assert.Equal(10, image.ClipRightColumns);
        buffer.Resize(20, 6);
        var metrics = new CellMetrics { CellWidth = 10, CellHeight = 20, Baseline = 15, Ascent = 15, Descent = 5 };
        using var rendered = SKBitmap.Decode(SnapshotService.CapturePng(buffer, metrics, 200, 120,
            new SnapshotCaptureOptions { HideCursor = true }));
        Assert.Equal(SKColors.Red, rendered.GetPixel(5, 5));
        Assert.NotEqual(SKColors.Red, rendered.GetPixel(125, 5));
    }

}
