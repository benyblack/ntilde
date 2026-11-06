using Ntilde.Rendering;
using Ntilde.VT;
using SkiaSharp;
using Xunit;

namespace Ntilde.Rendering.Tests;

/// <summary>
/// The decoder-free header reader agrees with Skia on what real encoders produce (#838): Kitty and
/// iTerm2 images are placed from the header size, and a window then decodes the same bytes with Skia.
/// </summary>
public sealed class ImageHeaderProbeSkiaTests
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

    [Theory]
    [InlineData(SKEncodedImageFormat.Png, 100, 37, 23)]
    [InlineData(SKEncodedImageFormat.Jpeg, 90, 37, 23)]
    [InlineData(SKEncodedImageFormat.Webp, 80, 37, 23)]  // lossy
    [InlineData(SKEncodedImageFormat.Webp, 100, 1999, 3)] // lossless
    [InlineData(SKEncodedImageFormat.Png, 100, 2000, 1)]
    public void The_header_size_matches_what_Skia_encodes_and_decodes(SKEncodedImageFormat format, int quality, int width, int height)
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(new SKColor(10, 200, 30, 255));
        using SKData encoded = bitmap.Encode(format, quality);
        byte[] bytes = encoded.ToArray();

        Assert.True(ImageHeaderProbe.TryGetSize(bytes, out _, out int probedWidth, out int probedHeight));
        Assert.Equal((width, height), (probedWidth, probedHeight));

        object? handle = new SkiaImageDecoder().DecodeImageBytes(bytes, out int decodedWidth, out int decodedHeight);
        Assert.NotNull(handle);
        (handle as IDisposable)?.Dispose();
        Assert.Equal((decodedWidth, decodedHeight), (probedWidth, probedHeight));
    }

    [Fact]
    public void A_gif_frame_larger_than_its_logical_screen_cannot_bypass_the_pixel_quota()
    {
        // The header says 1 x 1; the first frame is a complete 300 x 300 image. Skia decodes the frame,
        // but the retained image must hold no more pixels than the size it was placed and charged at.
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");
        byte[] gif = OverflowingGif(frameWidth: 300, frameHeight: 300);
        Assert.True(ImageHeaderProbe.TryGetSize(gif, out _, out int probedWidth, out int probedHeight));
        Assert.Equal((1, 1), (probedWidth, probedHeight));
        object? decoded = new SkiaImageDecoder().DecodeImageBytes(gif, out int decodedWidth, out int decodedHeight);
        Assert.NotNull(decoded); // the case is real: Skia decodes more than the header declares
        (decoded as IDisposable)?.Dispose();
        Assert.NotEqual((1, 1), (decodedWidth, decodedHeight));

        var buffer = new TerminalBuffer(80, 24);
        var replies = new List<string>();
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { ImageDecoder = new SkiaImageDecoder(), OnResponse = replies.Add };
        parser.Process($"\x1b_Ga=T,i=1,f=100;{Convert.ToBase64String(gif)}\x1b\\");

        Assert.Equal(["\x1b_Gi=1;OK\x1b\\"], replies);
        Assert.IsType<UndecodedImage>(Assert.Single(buffer.Images).ImageHandle);
    }

    /// <summary>
    /// A valid GIF whose logical screen is 1 x 1 but whose only frame is larger, filled with colour 0.
    /// The frame is stored uncompressed: 8-bit LZW codes with a clear code every 100 pixels, so the
    /// code width never grows.
    /// </summary>
    private static byte[] OverflowingGif(int frameWidth, int frameHeight)
    {
        var codes = new List<byte>();
        for (int pixel = 0; pixel < frameWidth * frameHeight; pixel++)
        {
            if (pixel % 100 == 0) codes.Add(128); // clear
            codes.Add(0);
        }
        codes.Add(129); // end of information

        var gif = new List<byte>();
        gif.AddRange("GIF89a"u8.ToArray());
        gif.AddRange([1, 0, 1, 0, 0x80, 0, 0]); // 1 x 1 screen, two-colour global table
        gif.AddRange([0, 0, 0, 255, 255, 255]);
        gif.Add(0x2C);
        gif.AddRange([0, 0, 0, 0, (byte)frameWidth, (byte)(frameWidth >> 8), (byte)frameHeight, (byte)(frameHeight >> 8), 0]);
        gif.Add(7); // minimum code size: 8-bit codes
        for (int i = 0; i < codes.Count; i += 255)
        {
            int length = Math.Min(255, codes.Count - i);
            gif.Add((byte)length);
            gif.AddRange(codes.GetRange(i, length));
        }
        gif.Add(0); // block terminator
        gif.Add(0x3B);
        return gif.ToArray();
    }
}
