using System;
using Ntilde.Rendering;
using Ntilde.VT;
using SkiaSharp;
using Xunit;

namespace Ntilde.Rendering.Tests;

public class SkiaImageDecoderTests
{
    // Decoding renders through the SkiaSharp native library. Same convention as
    // SixelDecoderTests: present on Windows CI / dev machines, absent on the Linux
    // gating runner — skip there rather than fail.
    private static readonly bool SkiaAvailable = CheckSkiaAvailable();

    private static bool CheckSkiaAvailable()
    {
        try
        {
            using var surface = SKSurface.Create(new SKImageInfo(1, 1, SKColorType.Rgba8888));
            return surface != null;
        }
        catch
        {
            return false;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Capture_omits_an_evicted_source_while_its_parser_placement_is_waiting_to_be_pruned(bool isolated)
    {
        var buffer = new TerminalBuffer(10, 6) { SixelSources = new SixelSourceBudget(1024, 1) };
        var parser = new AnsiParser(buffer) { RetainSixelSources = true, ImageDecoder = new SkiaImageDecoder { DeferSixelDecoding = true } };
        parser.Process("\x1b[?80h\x1bPq!30~\x1b\\text");
        var image = Assert.Single(buffer.Images);
        // Reproduce Retain succeeding before the producer can take the write lock to prune.
        using var next = buffer.SixelSources.Retain("q!40~");
        Assert.Null(image.SixelSource!.Data);
        using var captured = buffer.CaptureRenderSnapshot(new RenderSnapshotRequest { ViewportRows = 6, ViewportCols = 10, Isolated = isolated }, out _);
        Assert.Equal(0, captured.Images.Length);
        Assert.True(captured.DirtySpans.Length > 0);
        Assert.Equal(0, buffer.SixelSources.SnapshotChars);
        Assert.Single(buffer.Images); // capture never mutates placements under its read lock
    }

    [Fact]
    public void A_captured_undecoded_source_survives_replacement_and_releases_with_its_session()
    {
        var buffer = new TerminalBuffer(10, 6) { SixelSources = new SixelSourceBudget(1024, 4) };
        var parser = new AnsiParser(buffer) { RetainSixelSources = true, ImageDecoder = new SkiaImageDecoder { DeferSixelDecoding = true } };
        const string red = "q#1;2;100;0;0#1!30~";
        parser.Process("\x1b[?80h\x1bP" + red + "\x1b\\");
        int session = buffer.BeginSnapshotSession();
        try
        {
            using var captured = buffer.CaptureRenderSnapshot(new RenderSnapshotRequest { ViewportRows = 6, ViewportCols = 10, SnapshotSessionId = session }, out _);
            var old = Assert.IsType<DeferredSixelImage>(captured.Images.Array[0].ImageHandle);
            for (int i = 0; i < 100; i++) parser.Process("\x1bPq#1;2;0;0;100#1!30~\x1b\\");
            Assert.Null(old.Source.Data);
            Assert.Equal(red, old.Source.DecodeData);
            Assert.InRange(buffer.SixelSources.SnapshotChars, 1, 1024);
            var retired = new List<object>();
            Assert.False(buffer.DrainRetiredImageHandles(retired, long.MinValue));
            buffer.EndSnapshotSession(session);
            Assert.Equal(0, buffer.SixelSources.SnapshotChars);
            Assert.Null(old.Source.DecodeData);
            Assert.True(buffer.DrainRetiredImageHandles(retired, long.MinValue));
            foreach (var handle in retired) ((IDisposable)handle).Dispose();
        }
        finally { buffer.EndSnapshotSession(session); }
    }

    [Fact]
    public void A_captured_source_materializes_its_original_pixels_after_it_is_replaced()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");
        var buffer = new TerminalBuffer(10, 6) { SixelSources = new SixelSourceBudget(1024, 2) };
        var parser = new AnsiParser(buffer) { RetainSixelSources = true, ImageDecoder = new SkiaImageDecoder { DeferSixelDecoding = true } };
        parser.Process("\x1b[?80h\x1bPq#1;2;100;0;0#1!30~\x1b\\");
        int session = buffer.BeginSnapshotSession();
        try
        {
            using var captured = buffer.CaptureRenderSnapshot(new RenderSnapshotRequest { ViewportRows = 6, ViewportCols = 10, SnapshotSessionId = session }, out _);
            var old = Assert.IsType<DeferredSixelImage>(captured.Images.Array[0].ImageHandle);
            for (int i = 0; i < 20; i++) parser.Process("\x1bPq#1;2;0;0;100#1!30~\x1b\\");
            Assert.Null(old.Source.Data);
            var original = Assert.IsType<SKBitmap>(old.GetBitmap());
            Assert.Equal(SKColors.Red, original.GetPixel(0, 0));
            buffer.EndSnapshotSession(session);
            var retired = new List<object>();
            Assert.True(buffer.DrainRetiredImageHandles(retired, long.MinValue));
            foreach (var handle in retired) ((IDisposable)handle).Dispose();
        }
        finally { buffer.EndSnapshotSession(session); }
    }

    [Fact]
    public void Snapshot_source_limit_rejects_atomically_and_recovers_when_the_previous_capture_ends()
    {
        var buffer = new TerminalBuffer(10, 6) { SixelSources = new SixelSourceBudget(1024, 1) };
        var parser = new AnsiParser(buffer) { RetainSixelSources = true, ImageDecoder = new SkiaImageDecoder { DeferSixelDecoding = true } };
        parser.Process("\x1b[?80h\x1bPq!30~\x1b\\");
        var first = buffer.CaptureRenderSnapshot(new RenderSnapshotRequest { ViewportRows = 6, ViewportCols = 10 }, out _);
        parser.Process("\x1bPq!40~\x1b\\\x1b[3;1Hupdated");
        Assert.Throws<InvalidOperationException>(() => buffer.CaptureRenderSnapshot(new RenderSnapshotRequest { ViewportRows = 6, ViewportCols = 10 }, out _));
        Assert.InRange(buffer.SixelSources.SnapshotChars, 1, 1024);
        first.Dispose();
        Assert.Equal(0, buffer.SixelSources.SnapshotChars);
        using (var second = buffer.CaptureRenderSnapshot(new RenderSnapshotRequest { ViewportRows = 6, ViewportCols = 10 }, out _))
        {
            Assert.InRange(buffer.SixelSources.SnapshotChars, 1, 1024);
            Assert.True(second.DirtySpans.Length > 0); // the rejected frame must not advance the live baseline
        }
        Assert.Equal(0, buffer.SixelSources.SnapshotChars);
    }

    [Fact]
    public void Replaced_pending_frames_do_not_evict_an_uncovered_live_image_source()
    {
        var buffer = new TerminalBuffer(10, 6) { SixelSources = new SixelSourceBudget(1024, 4) };
        var parser = new AnsiParser(buffer) { RetainSixelSources = true, ImageDecoder = new SkiaImageDecoder { DeferSixelDecoding = true } };
        parser.Process("\x1b[?80h\x1bPq#1;2;100;0;0#1!30~\x1b\\");
        for (int i = 0; i < 100; i++) parser.Process("\x1bPq#1;2;0;0;100#1!10~\x1b\\");
        Assert.Equal(2, buffer.Images.Count);
        Assert.Equal(2, buffer.ExportState(0).Sixels!.Count);
        Assert.Equal("q#1;2;100;0;0#1!30~", buffer.Images[0].SixelSource?.Data);
        Assert.InRange(buffer.SixelSources.Count, 2, 4);
        var retired = new List<object>();
        buffer.DrainRetiredImageHandles(retired, long.MinValue);
        foreach (var handle in retired) ((IDisposable)handle).Dispose();
        foreach (var image in buffer.Images) ((IDisposable)image.ImageHandle).Dispose();
    }

    [Fact]
    public void Source_eviction_preserves_materialized_bitmaps_and_gates_undecoded_handle_disposal()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");
        var buffer = new TerminalBuffer(10, 6) { SixelSources = new SixelSourceBudget(1024, 1) };
        var parser = new AnsiParser(buffer) { RetainSixelSources = true, ImageDecoder = new SkiaImageDecoder { DeferSixelDecoding = true } };
        parser.Process("\x1bPq#1;2;100;0;0#1!30~\x1b\\");
        var materialized = Assert.IsType<DeferredSixelImage>(Assert.Single(buffer.Images).ImageHandle);
        var pixels = Assert.IsType<SKBitmap>(materialized.GetBitmap());
        parser.Process("\x1b[1;1H\x1bPq~\x1b\\");
        Assert.Equal(2, buffer.Images.Count);
        Assert.Same(pixels, materialized.GetBitmap());
        Assert.Equal(SKColors.Red, pixels.GetPixel(0, 0));
        int session = buffer.BeginSnapshotSession();
        parser.Process("\x1b[1;1H\x1bPq!2~\x1b\\");
        Assert.Equal(2, buffer.Images.Count); // materialized frame plus newest undecoded frame
        var retired = new List<object>();
        Assert.False(buffer.DrainRetiredImageHandles(retired, long.MinValue));
        buffer.EndSnapshotSession(session);
        Assert.True(buffer.DrainRetiredImageHandles(retired, long.MinValue));
        Assert.Single(retired);
        foreach (var handle in retired) ((IDisposable)handle).Dispose();
        foreach (var image in buffer.Images) ((IDisposable)image.ImageHandle).Dispose();
    }

    [Fact]
    public void Unseen_inline_images_share_an_aggregate_source_budget_with_snapshot_metadata()
    {
        var budget = new SixelSourceBudget(1024, 4);
        var buffer = new TerminalBuffer(10, 6) { SixelSources = budget };
        var parser = new AnsiParser(buffer)
        {
            RetainSixelSources = true,
            ImageDecoder = new SkiaImageDecoder { DeferSixelDecoding = true },
        };
        string data = "q" + string.Concat(Enumerable.Repeat("#1;2;100;0;0", 20)) + "#1~";
        parser.Process("\x1bP" + data + "\x1b\\");
        var first = Assert.IsType<DeferredSixelImage>(Assert.Single(buffer.Images).ImageHandle);
        for (int i = 0; i < 500; i++) parser.Process("\x1b[1;1H\x1bP" + data + "\x1b\\");
        Assert.InRange(budget.RetainedChars, 1, 1024);
        Assert.InRange(budget.Count, 1, 4);
        Assert.InRange(buffer.Images.Count, 1, 4);
        Assert.DoesNotContain(buffer.Images, image => ReferenceEquals(image.ImageHandle, first));
        Assert.Null(first.GetBitmap());
        Assert.All(buffer.Images, image => Assert.False(Assert.IsType<DeferredSixelImage>(image.ImageHandle).IsDecoded));
        Assert.InRange(buffer.ExportState(int.MaxValue).Sixels!.Sum(image => image.Source.Length), 1, 1024);
        var retired = new List<object>();
        buffer.DrainRetiredImageHandles(retired, long.MinValue);
        foreach (var handle in retired) ((IDisposable)handle).Dispose();
        foreach (var image in buffer.Images) ((IDisposable)image.ImageHandle).Dispose();
        Assert.Equal(0, budget.RetainedChars);
    }

    [Fact]
    public void Direct_deferred_decoder_calls_share_one_budget_and_oversized_images_are_not_retained()
    {
        var budget = new SixelSourceBudget(40, 2);
        var decoder = new SkiaImageDecoder { DeferSixelDecoding = true, SixelSources = budget };
        var first = Assert.IsType<DeferredSixelImage>(decoder.DecodeSixel("q!1~", out _, out _));
        decoder.DecodeSixel("q!2~", out _, out _);
        decoder.DecodeSixel("q!3~", out _, out _);
        Assert.Null(first.GetBitmap());
        Assert.Equal(2, budget.Count);
        Assert.Null(decoder.DecodeSixel("q" + new string('?', 100), out _, out _));
        Assert.Equal(2, budget.Count);
    }

    [Fact]
    public void Frame_decode_budget_defers_remaining_images_and_reuses_materialized_pixels()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");
        using var first = new DeferredSixelImage(new Ntilde.VT.SixelSourceBudget().Retain("q#1;2;100;0;0#1~"), 2000);
        using var second = new DeferredSixelImage(new Ntilde.VT.SixelSourceBudget().Retain("q#1;2;0;0;100#1~"), 2000);
        int budget = 1;
        var red = Assert.IsType<SkiaSharp.SKBitmap>(first.GetBitmap(ref budget, out bool firstPending));
        Assert.False(firstPending);
        Assert.Equal(0, budget);
        Assert.Null(second.GetBitmap(ref budget, out bool secondPending));
        Assert.True(secondPending);
        Assert.Same(red, first.GetBitmap(ref budget, out firstPending));
        Assert.False(firstPending);
        budget = 1;
        var blue = Assert.IsType<SkiaSharp.SKBitmap>(second.GetBitmap(ref budget, out secondPending));
        Assert.False(secondPending);
        Assert.Equal(SkiaSharp.SKColors.Red, red.GetPixel(0, 0));
        Assert.Equal(SkiaSharp.SKColors.Blue, blue.GetPixel(0, 0));
    }

    [Fact]
    public void Deferred_SIXEL_preserves_pixels_and_bounds_until_first_materialization()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");
        var decoder = new SkiaImageDecoder { DeferSixelDecoding = true, MaxPixelDimension = 8 };
        using var handle = Assert.IsType<DeferredSixelImage>(decoder.DecodeSixel("q#1;2;100;0;0#1!100~-!100~", out int width, out int height));
        Assert.Equal((8, 8), (width, height));
        Assert.False(handle.IsDecoded);
        var bitmap = Assert.IsType<SKBitmap>(handle.GetBitmap());
        Assert.True(handle.IsDecoded);
        Assert.Equal((8, 8), (bitmap.Width, bitmap.Height));
        Assert.Equal(SKColors.Red, bitmap.GetPixel(7, 7));
        Assert.Same(bitmap, handle.GetBitmap());
        handle.Dispose();
        Assert.Null(handle.GetBitmap());
    }

    [Fact]
    public void Unseen_page_origin_frames_are_retired_without_decoding_or_losing_the_latest_source()
    {
        var buffer = new TerminalBuffer(10, 6);
        var parser = new AnsiParser(buffer) { ImageDecoder = new SkiaImageDecoder { DeferSixelDecoding = true } };
        parser.Process("\x1b[?80h\x1bPq!30~\x1b\\");
        var first = Assert.IsType<DeferredSixelImage>(Assert.Single(buffer.Images).ImageHandle);
        Assert.False(first.IsDecoded);
        for (int i = 0; i < 50; i++) parser.Process("\x1bPq!30~\x1b\\");
        Assert.False(first.IsDecoded);
        var retired = new List<object>();
        Assert.True(buffer.DrainRetiredImageHandles(retired, long.MinValue));
        Assert.Equal(50, retired.Count);
        foreach (var handle in retired) Assert.IsType<DeferredSixelImage>(handle).Dispose();
        Assert.Null(first.GetBitmap()); // The render owner disposes retired source without materialization.
        using var latest = Assert.IsType<DeferredSixelImage>(Assert.Single(buffer.Images).ImageHandle);
        Assert.False(latest.IsDecoded);
        Assert.NotSame(first, latest);
    }

    [Fact]
    public async Task Concurrent_deferred_readers_share_one_bitmap()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");
        using var handle = new DeferredSixelImage(new Ntilde.VT.SixelSourceBudget().Retain("q~"), 2000);
        SKBitmap?[] results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(handle.GetBitmap)));
        Assert.NotNull(results[0]);
        Assert.All(results, bitmap => Assert.Same(results[0], bitmap));
    }

    [Fact]
    public void DecodeSixel_MinimalPayload_ReturnsBitmapWithPixelDimensions()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        // One 6-pixel band, one column wide (same payload as SixelDecoderTests).
        var handle = new SkiaImageDecoder().DecodeSixel("0;0;0q#1;2;100;0;0#1~", out int width, out int height);

        var bitmap = Assert.IsType<SKBitmap>(handle);
        Assert.Equal(bitmap.Width, width);
        Assert.Equal(bitmap.Height, height);
        Assert.True(width > 0);
        Assert.True(height > 0);
    }

    [Fact]
    public void DecodeSixel_ClipsToMaxPixelDimension()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        // A 100-column run over two bands (12 rows) against an 8-pixel bound: the one knob that
        // bounds kitty and iTerm2 images bounds sixel too.
        var decoder = new SkiaImageDecoder { MaxPixelDimension = 8 };
        var handle = decoder.DecodeSixel("q!100~-!100~", out int width, out int height);

        using var bitmap = Assert.IsType<SKBitmap>(handle);
        Assert.Equal(8, width);
        Assert.Equal(8, height);
        Assert.Equal(8, bitmap.Width);
        Assert.Equal(8, bitmap.Height);
    }

    [Theory]
    [InlineData("1;2;3")]   // no 'q' header terminator — anything below '?' is not sixel data
    [InlineData("0;0;0q")]  // header but no pixel data
    [InlineData("")]
    public void DecodeSixel_PayloadWithoutRenderableData_ReturnsNull(string dcs)
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        var handle = new SkiaImageDecoder().DecodeSixel(dcs, out int width, out int height);

        Assert.Null(handle);
        Assert.Equal(0, width);
        Assert.Equal(0, height);
    }

    [Fact]
    public void DecodeImageBytes_PngPayload_ReturnsBitmapWithPixelDimensions()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        byte[] png;
        var source = new SKBitmap(3, 5);
        using (var image = SKImage.FromBitmap(source))
        using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
        {
            png = data.ToArray();
        }

        var handle = new SkiaImageDecoder().DecodeImageBytes(png, out int width, out int height);

        var bitmap = Assert.IsType<SKBitmap>(handle);
        Assert.Equal(3, bitmap.Width);
        Assert.Equal(5, bitmap.Height);
        Assert.Equal(3, width);
        Assert.Equal(5, height);
    }

    [Fact]
    public void DecodeImageBytes_GarbageBytes_ReturnsNull()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        var handle = new SkiaImageDecoder().DecodeImageBytes(new byte[] { 0x01, 0x02, 0x03 }, out int width, out int height);

        Assert.Null(handle);
        Assert.Equal(0, width);
        Assert.Equal(0, height);
    }

    /// <summary>
    /// The dimension bound must fire from the container header BEFORE pixels are
    /// materialized: a compressed payload declaring huge dimensions would otherwise force
    /// the full decode allocation before any post-decode guard could reject it.
    /// </summary>
    [Fact]
    public void DecodeImageBytes_DimensionsOverBound_RejectedBeforeMaterialization()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        byte[] png;
        var source = new SKBitmap(3, 5);
        using (var image = SKImage.FromBitmap(source))
        using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
        {
            png = data.ToArray();
        }

        var decoder = new SkiaImageDecoder { MaxPixelDimension = 1 };
        var handle = decoder.DecodeImageBytes(png, out int width, out int height);

        Assert.Null(handle);
        Assert.Equal(0, width);
        Assert.Equal(0, height);
    }

    [Fact]
    public void DecodeImageBytes_EmptyBytes_ReturnsNull()
    {
        var handle = new SkiaImageDecoder().DecodeImageBytes(Array.Empty<byte>(), out int width, out int height);

        Assert.Null(handle);
        Assert.Equal(0, width);
        Assert.Equal(0, height);
    }

    /// <summary>
    /// End-to-end through the real parser: with the production decoder wired, a DCS sixel
    /// sequence must place an image in the buffer sized from the decoded bitmap's pixels
    /// (parser fallback cell metrics are 10x20 px). Without a decoder this path silently
    /// no-ops — the exact defect this wiring fixes.
    /// </summary>
    [Fact]
    public void AnsiParser_WithWiredDecoder_PlacesSixelImageInBuffer()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer) { ImageDecoder = new SkiaImageDecoder() };

        parser.Process("\x1bP0;0;0q#1;2;100;0;0#1~\x1b\\");

        buffer.Lock.EnterReadLock();
        try
        {
            var image = Assert.Single(buffer.Images);
            Assert.IsType<SKBitmap>(image.ImageHandle);
            Assert.Equal(1, image.CellWidth);  // ceil(1 px / 10 px fallback cell width)
            Assert.Equal(1, image.CellHeight); // ceil(6 px / 20 px fallback cell height)
        }
        finally
        {
            buffer.Lock.ExitReadLock();
        }
    }

    [Fact]
    public void DecodeRawImage_RgbaOddWidth_ProducesCorrectPixels()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        // Odd width on purpose: a stride/row-length bug would smear pixel (1,1).
        var pixels = new byte[3 * 2 * 4];
        pixels[0] = 0xFF; pixels[1] = 0x00; pixels[2] = 0x00; pixels[3] = 0xFF; // (0,0) red
        pixels[16] = 0x00; pixels[17] = 0x00; pixels[18] = 0xFF; pixels[19] = 0xFF; // (1,1) blue: (1*3+1)*4 = 16

        var handle = new SkiaImageDecoder().DecodeRawImage(pixels, bytesPerPixel: 4, width: 3, height: 2, out int width, out int height);

        var bitmap = Assert.IsType<SKBitmap>(handle);
        Assert.Equal(3, width);
        Assert.Equal(2, height);
        SKColor red = bitmap.GetPixel(0, 0);
        Assert.Equal(0xFF, red.Red);
        SKColor blue = bitmap.GetPixel(1, 1);
        Assert.Equal(0xFF, blue.Blue);
    }

    [Fact]
    public void DecodeRawImage_RgbThreeBytesPerPixel_ProducesOpaqueBitmap()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        var pixels = new byte[] { 0x10, 0x20, 0x30 };

        var handle = new SkiaImageDecoder().DecodeRawImage(pixels, bytesPerPixel: 3, width: 1, height: 1, out _, out _);

        var bitmap = Assert.IsType<SKBitmap>(handle);
        SKColor pixel = bitmap.GetPixel(0, 0);
        Assert.Equal(0x10, pixel.Red);
        Assert.Equal(0x20, pixel.Green);
        Assert.Equal(0x30, pixel.Blue);
        Assert.Equal(0xFF, pixel.Alpha);
    }

    [Theory]
    [InlineData(4)]  // wrong pixel count
    [InlineData(3)]  // 3bpp declared 4 wide 2 high needs 24, not 12
    public void DecodeRawImage_LengthMismatch_ReturnsNull(int bytesPerPixel)
    {
        var decoder = new SkiaImageDecoder();
        var handle = decoder.DecodeRawImage(new byte[12], bytesPerPixel, width: 4, height: 2, out _, out _);
        Assert.Null(handle);
    }

    [Fact]
    public void DecodeRawImage_DimensionOverCap_ReturnsNull()
    {
        var decoder = new SkiaImageDecoder { MaxPixelDimension = 8 };
        var handle = decoder.DecodeRawImage(new byte[3 * 4], bytesPerPixel: 3, width: 16, height: 1, out _, out _);
        Assert.Null(handle);
    }
    [Theory]
    [InlineData("q!99999999999999999999999~")]
    [InlineData("q\"1;1;999999;999999~")]
    [InlineData("q!20~$!2~-!3~")]
    [InlineData("q!0~")]
    [InlineData("q#1;2;100;0;0")]
    [InlineData("q!-~")]
    public void Bitmap_and_headless_measurement_use_the_same_bounded_geometry(string data)
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");
        using var bitmap = new SixelDecoder { MaxPixelDimension = 32 }.Decode(data);
        var expected = SixelGeometry.Measure(data, 32);
        Assert.Equal(expected, bitmap is null ? (0, 0) : (bitmap.Width, bitmap.Height));
    }

}

