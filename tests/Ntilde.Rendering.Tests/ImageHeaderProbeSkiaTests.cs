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
}
