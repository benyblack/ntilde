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
    public void Restored_snapshot_sources_render_the_same_partial_page_origin_overlay()
    {
        Assert.SkipUnless(HasSkia(), "SkiaSharp native library not available on this platform.");
        var headless = new TerminalBuffer(10, 6);
        var parser = new AnsiParser(headless) { RetainSixelSources = true, SixelCellSizeOverride = new SixelCellSize(10, 20) };
        parser.Process("\x1b[?80h\x1b[3;5H\x1bPq#1;2;100;0;0#1!30~\x1b\\\x1bPq#1;2;0;0;100#1!10~\x1b\\");
        var snapshot = headless.ExportState(0);
        snapshot.Parser = parser.ExportState();
        var restored = new TerminalBuffer(20, 3);
        var restoredParser = new AnsiParser(restored)
        {
            RetainSixelSources = true,
            ImageDecoder = new SkiaImageDecoder { DeferSixelDecoding = true },
        };
        TerminalStateTransfer.Restore(restored, restoredParser, TerminalStateSerializer.FromBytes(TerminalStateSerializer.ToBytes(snapshot)));
        Assert.Equal((headless.CursorCol, headless.CursorRow), (restored.CursorCol, restored.CursorRow));
        Assert.Equal(2, restored.Images.Count);
        Assert.All(restored.Images, image => Assert.IsType<DeferredSixelImage>(image.ImageHandle));
        var metrics = new CellMetrics { CellWidth = 10, CellHeight = 20, Baseline = 15, Ascent = 15, Descent = 5 };
        using var pixels = SKBitmap.Decode(SnapshotService.CapturePng(restored, metrics, 100, 120, new SnapshotCaptureOptions { HideCursor = true }));
        Assert.Equal(SKColors.Blue, pixels.GetPixel(5, 3));
        Assert.Equal(SKColors.Red, pixels.GetPixel(25, 3));
        foreach (var image in restored.Images) ((IDisposable)image.ImageHandle).Dispose();
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

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void Live_frames_decode_one_image_and_request_the_next_frame_until_complete()
    {
        Assert.SkipUnless(HasSkia(), "SkiaSharp native library not available on this platform.");
        var buffer = new TerminalBuffer(10, 6);
        var parser = new AnsiParser(buffer) { ImageDecoder = new SkiaImageDecoder { DeferSixelDecoding = true }, CellWidth = 10, CellHeight = 20 };
        parser.Process("\x1b[?80h\x1bPq#1;2;100;0;0#1!30~\x1b\\\x1bPq#1;2;0;0;100#1!10~\x1b\\");
        var typeface = new Avalonia.Media.Typeface("Cascadia Code, Consolas, Monospace");
        var skTypeface = new SharedSKTypeface(SKTypeface.FromFamilyName(typeface.FontFamily.Name));
        var skFont = new SharedSKFont(new SKFont(skTypeface.Typeface, 14));
        using var rendered = new SKBitmap(100, 120);
        using var canvas = new SKCanvas(rendered);
        int requestedFrames = 0;
        using var op = new TerminalDrawOperation(
            new Avalonia.Rect(0, 0, 100, 120), buffer, 0, new SelectionState(), null, -1,
            new CellMetrics { CellWidth = 10, CellHeight = 20, Baseline = 15, Ascent = 15, Descent = 5 },
            typeface, 14, typeface.GlyphTypeface, skTypeface, skFont, false,
            new System.Collections.Concurrent.ConcurrentDictionary<string, SKTypeface?>(), [], 1, true,
            snapshotRows: 6, snapshotCols: 10, requestImageFrame: () => requestedFrames++);
        skFont.Dispose(); // the draw operation owns the remaining references
        skTypeface.Dispose();
        op.DrawTerminalInternal(canvas);
        Assert.Equal(1, requestedFrames);
        Assert.Equal(SKColors.Red, rendered.GetPixel(25, 3));
        Assert.NotEqual(SKColors.Blue, rendered.GetPixel(5, 3));
        op.DrawTerminalInternal(canvas);
        Assert.Equal(1, requestedFrames); // no more work remains
        Assert.Equal(SKColors.Blue, rendered.GetPixel(5, 3));
        Assert.Equal(SKColors.Red, rendered.GetPixel(25, 3));
        foreach (var image in buffer.Images) ((IDisposable)image.ImageHandle).Dispose();
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
