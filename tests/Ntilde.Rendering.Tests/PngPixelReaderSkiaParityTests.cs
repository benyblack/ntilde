using Ntilde.Rendering;
using Ntilde.VT;
using SkiaSharp;
using Xunit;

namespace Ntilde.Rendering.Tests;

/// <summary>
/// The decoder-independent PNG reader (#1017) against Skia's own decode of PNGs Skia encodes: where they must agree (opaque pixels, and transparent ones'
/// alpha) they do exactly, and a half-transparent pixel agrees within the rounding Skia's premultiplied round trip allows.
/// </summary>
public sealed class PngPixelReaderSkiaParityTests
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

    private static byte[] Encode(int width, int height, Func<int, int, SKColor> pixel)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) bitmap.SetPixel(x, y, pixel(x, y));
        using SKData data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(17, 9)]
    [InlineData(64, 48)]
    public void Opaque_images_read_exactly_as_Skia_reads_them(int width, int height)
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");
        byte[] png = Encode(width, height, (x, y) => new SKColor((byte)(x * 7), (byte)(y * 11), (byte)(x * y), 255));
        Assert.True(PngPixelReader.TryRead(png, 4096, out int w, out int h, out byte[] rgba));
        var decoder = new SkiaImageDecoder();
        object handle = decoder.DecodeImageBytes(png, out int sw, out int sh)!;
        try
        {
            Assert.True(decoder.TryReadPixels(handle, out byte[] skia, out _, out _));
            Assert.Equal((sw, sh), (w, h));
            Assert.Equal(skia, rgba);
        }
        finally { (handle as IDisposable)?.Dispose(); }
    }

    [Fact]
    public void Transparent_images_agree_on_alpha_and_within_rounding_on_colour()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");
        byte[] png = Encode(32, 8, (x, y) => new SKColor((byte)(x * 8), (byte)(255 - x * 8), (byte)(y * 30), (byte)(x * 8)));
        Assert.True(PngPixelReader.TryRead(png, 4096, out _, out _, out byte[] rgba));
        var decoder = new SkiaImageDecoder();
        object handle = decoder.DecodeImageBytes(png, out _, out _)!;
        try
        {
            Assert.True(decoder.TryReadPixels(handle, out byte[] skia, out _, out _));
            for (int i = 0; i < rgba.Length; i += 4)
            {
                Assert.Equal(skia[i + 3], rgba[i + 3]);                                   // alpha is never rounded
                if (rgba[i + 3] == 0) continue;                                           // a fully transparent pixel's colour is unspecified
                for (int c = 0; c < 3; c++) Assert.InRange(Math.Abs(skia[i + c] - rgba[i + c]), 0, (int)Math.Ceiling(255.0 / rgba[i + 3]) + 1);   // a premultiplied round trip loses about 255/alpha
            }
        }
        finally { (handle as IDisposable)?.Dispose(); }
    }

    [Fact]
    public void A_PNG_frame_composes_the_same_in_a_persistent_session_as_through_the_decoder_in_an_ordinary_tab()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");
        byte[] png = Encode(1, 1, (_, _) => new SKColor(255, 255, 255, 255));
        string frame = "\x1b_Ga=f,f=100,i=1,x=1,c=1;" + Convert.ToBase64String(png) + "\x1b\\";
        string root = "\x1b_Ga=T,f=32,s=2,v=1,i=1,p=1;" + Convert.ToBase64String([255, 0, 0, 255, 0, 0, 255, 255]) + "\x1b\\";
        byte[] Compose(bool persistent)
        {
            var buffer = new TerminalBuffer(40, 20);
            var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { ImageDecoder = persistent ? null : new SkiaImageDecoder() };
            if (persistent) PersistentImagePolicy.Apply(parser, PersistentImagePolicy.AnimationImagesVersion);
            parser.Process(root + frame);
            KittyImageAsset asset = buffer.FindKittyImage(1, 0)!;
            KittyFrame second = asset.Animation!.Frames[1];
            byte[]? pixels = buffer.ReadKittyFramePixels(asset, 1, null, fromSources: persistent);
            buffer.ClearImages();
            return pixels ?? throw new Xunit.Sdk.XunitException("no pixels");
        }
        Assert.Equal(Compose(persistent: false), Compose(persistent: true));
    }
}
