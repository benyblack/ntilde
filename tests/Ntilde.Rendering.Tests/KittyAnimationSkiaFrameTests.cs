using Ntilde.Rendering;
using Ntilde.VT;
using SkiaSharp;
using Xunit;

namespace Ntilde.Rendering.Tests;

/// <summary>
/// Kitty animation (#1015) with the real Skia decoder: a PNG frame is decoded and read back as straight-alpha RGBA, and the root frame
/// is read back from its decoded bitmap to serve as a base. Deterministic pixel checks, not desktop measurement.
/// </summary>
public sealed class KittyAnimationSkiaFrameTests
{
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

    private static string Command(string controls, string payload = "") => $"\x1b_G{controls};{payload}\x1b\\";

    private static byte[] EncodePng(int width, int height, SKColor color)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        bitmap.Erase(color);
        using SKData data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public void A_decoded_png_and_a_raw_frame_read_back_as_straight_alpha_rgba()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");
        var decoder = new SkiaImageDecoder();
        object? png = decoder.DecodeImageBytes(EncodePng(3, 2, new SKColor(10, 200, 30, 255)), out int w, out int h);
        Assert.NotNull(png);
        try
        {
            Assert.True(decoder.TryReadPixels(png, out byte[] rgba, out int rw, out int rh));
            Assert.Equal((3, 2, 3 * 2 * 4), (rw, rh, rgba.Length));
            Assert.Equal((w, h), (rw, rh));
            for (int i = 0; i < 6; i++) Assert.Equal(new byte[] { 10, 200, 30, 255 }, rgba[(i * 4)..(i * 4 + 4)]);
        }
        finally { (png as IDisposable)?.Dispose(); }

        byte[] raw = [255, 0, 0, 128, 0, 0, 255, 255];
        object? handle = decoder.DecodeRawImage(raw, 4, 2, 1, out _, out _);
        Assert.NotNull(handle);
        try
        {
            Assert.True(decoder.TryReadPixels(handle, out byte[] read, out _, out _));
            Assert.Equal(raw[4..], read[4..]);
            Assert.Equal(255, read[0]);                          // straight alpha: the color survives the half-transparent pixel
            Assert.InRange(read[3], 127, 129);
        }
        finally { (handle as IDisposable)?.Dispose(); }
    }

    [Fact]
    public void A_retired_or_foreign_handle_is_not_readable()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");
        var decoder = new SkiaImageDecoder();
        Assert.False(decoder.TryReadPixels(new object(), out byte[] none, out _, out _));
        Assert.Empty(none);
        object? handle = decoder.DecodeRawImage([1, 2, 3, 255], 4, 1, 1, out _, out _);
        ((IDisposable)handle!).Dispose();
        Assert.False(decoder.TryReadPixels(handle, out _, out _, out _));
    }

    [Fact]
    public void A_png_frame_composes_over_the_root_frame_through_the_real_decoder()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { ImageDecoder = new SkiaImageDecoder() };
        var replies = new List<string>(); parser.OnResponse = replies.Add;
        // Root: red | blue, 2x1. Frame 2: a 1x1 opaque green PNG at x=1 over the root.
        parser.Process(Command("a=T,f=32,s=2,v=1,i=1,C=1", Convert.ToBase64String([255, 0, 0, 255, 0, 0, 255, 255])));
        replies.Clear();
        parser.Process(Command("a=f,i=1,f=100,x=1,c=1,z=80", Convert.ToBase64String(EncodePng(1, 1, new SKColor(0, 255, 0, 255)))));
        Assert.Equal("\x1b_Gi=1;OK\x1b\\", Assert.Single(replies));
        KittyImageAsset asset = buffer.FindKittyImage(1, 0)!;
        Assert.Equal(2, asset.Animation!.Frames.Count);
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 255, 0, 255 }, asset.Animation.Frames[1].Rgba);
        // The displayed handle is still the root's real bitmap; the frame's own bitmap is a separate one.
        Assert.IsType<SKBitmap>(asset.Animation.Frames[1].Handle);
        Assert.NotSame(asset.Animation.Frames[0].Handle, asset.Animation.Frames[1].Handle);
        buffer.ClearImages();
    }
}
