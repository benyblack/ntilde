using Ntilde.Rendering;
using SkiaSharp;
using Xunit;

namespace Ntilde.Rendering.Tests;

// Regression tests for #169: sixel payloads are remote-controlled input, so
// malformed color parameters must be skipped, not thrown into the parser loop.
public class SixelDecoderTests
{
    // SixelDecoder renders through the SkiaSharp native library. Same convention as
    // GlyphCacheTests: present on Windows CI / dev machines, absent on the Linux
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
    [InlineData(0, 50, 100, 0, 0, 255)]
    [InlineData(120, 50, 100, 255, 0, 0)]
    [InlineData(240, 50, 100, 0, 255, 0)]
    [InlineData(121, 50, 100, 255, 3, 0)]
    [InlineData(42, 0, 100, 0, 0, 0)]
    [InlineData(42, 100, 100, 255, 255, 255)]
    [InlineData(42, 50, 0, 128, 128, 128)]
    public void HlsToRgb_ReturnsExpectedColor(
        int hue,
        int lightness,
        int saturation,
        byte red,
        byte green,
        byte blue)
    {
        Assert.Equal((red, green, blue), SixelDecoder.HlsToRgb(hue, lightness, saturation));
    }

    [Fact]
    public void HlsToRgb_ClampsOutOfRangeInputs()
    {
        Assert.Equal(SixelDecoder.HlsToRgb(0, 50, 100), SixelDecoder.HlsToRgb(-1, 50, 100));
        Assert.Equal(SixelDecoder.HlsToRgb(360, 50, 100), SixelDecoder.HlsToRgb(361, 50, 100));
        Assert.Equal((byte.MinValue, byte.MinValue, byte.MinValue), SixelDecoder.HlsToRgb(120, -1, 100));
        Assert.Equal((byte.MaxValue, byte.MaxValue, byte.MaxValue), SixelDecoder.HlsToRgb(120, 101, 100));
        Assert.Equal(SixelDecoder.HlsToRgb(120, 50, 0), SixelDecoder.HlsToRgb(120, 50, -1));
        Assert.Equal(SixelDecoder.HlsToRgb(120, 50, 100), SixelDecoder.HlsToRgb(120, 50, 101));
    }

    [Fact]
    public void Decode_HlsRedMatchesRgbRed()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        using var hls = new SixelDecoder().Decode("0;0;0q#1;1;120;50;100#1~");
        using var rgb = new SixelDecoder().Decode("0;0;0q#1;2;100;0;0#1~");

        Assert.NotNull(hls);
        Assert.NotNull(rgb);
        Assert.Equal(rgb.GetPixel(0, 0), hls.GetPixel(0, 0));
    }

    [Theory]
    [InlineData("0;0;0q#1;;2;3;4~~")]          // empty param via consecutive ';'
    [InlineData("0;0;0q#1;2;;3;~~")]           // multiple empties
    [InlineData("0;0;0q#1;2;99999999999;3;4~")] // overflow int.Parse territory
    public void Decode_MalformedColorParams_DoesNotThrow(string dcs)
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        var decoder = new SixelDecoder();

        var ex = Record.Exception(() => decoder.Decode(dcs));

        Assert.Null(ex);
    }

    [Fact]
    public void Decode_RgbValuesAbove100_AreClampedNotWrapped()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        var decoder = new SixelDecoder();

        // type 2 = RGB percentages; 200% would previously wrap the byte cast.
        var ex = Record.Exception(() => decoder.Decode("0;0;0q#1;2;200;200;200#1~~-"));

        Assert.Null(ex);
    }

    [Fact]
    public void Decode_ValidMinimalSixel_ProducesBitmap()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        var decoder = new SixelDecoder();

        // One 6-pixel column in palette color 1.
        var bitmap = decoder.Decode("0;0;0q#1;2;100;0;0#1~");

        Assert.NotNull(bitmap);
    }

    private const int Cap = SixelDecoder.DefaultMaxPixelDimension;

    // Everything between the DCS introducer and ST reaches the decoder, so this is the whole
    // of the 20-byte "ESC P q !2000000000~ ESC \" that any program output can carry.
    private const string HugeRepeat = "q!2000000000~";

    // Decodes here take milliseconds. The timeout only exists so a decoder that loops over a
    // declared count again fails the test instead of hanging the run.
    private static readonly TimeSpan DecodeTimeout = TimeSpan.FromSeconds(10);

    private static Task<SKBitmap?> DecodeWithTimeoutAsync(string dcs)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        return Task.Run(() => new SixelDecoder().Decode(dcs), ct).WaitAsync(DecodeTimeout, ct);
    }

    [Fact]
    public async Task Decode_HugeRepeatCount_FinishesFastWithWidthClippedToCap()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        using var bitmap = await DecodeWithTimeoutAsync(HugeRepeat);

        Assert.NotNull(bitmap);
        Assert.Equal(Cap, bitmap.Width);
        Assert.Equal(6, bitmap.Height);
        // Clipped, not discarded: the run still paints up to the edge (default register 0 is black).
        Assert.Equal(SKColors.Black, bitmap.GetPixel(0, 0));
        Assert.Equal(SKColors.Black, bitmap.GetPixel(Cap - 1, 5));
    }

    [Fact]
    public async Task Decode_HugeRepeatsAcrossManyBands_ClipsBothDimensionsToCap()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        // 1000 bands is 6000 rows, each band a maximum-count run.
        string dcs = "q" + string.Join("-", Enumerable.Repeat("!2000000000~", 1000));

        using var bitmap = await DecodeWithTimeoutAsync(dcs);

        Assert.NotNull(bitmap);
        Assert.Equal(Cap, bitmap.Width);
        Assert.Equal(Cap, bitmap.Height);
        // The band straddling the bottom edge keeps the rows that fit.
        Assert.Equal(SKColors.Black, bitmap.GetPixel(Cap - 1, Cap - 1));
    }

    [Fact]
    public async Task Decode_HugeRasterAttributes_DoNotSizeTheImage()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        // "Pan;Pad;Ph;Pv declares a 2e9 x 2e9 image; one column of one band is all that is drawn.
        using var bitmap = await DecodeWithTimeoutAsync("q\"1;1;2000000000;2000000000#1;2;100;0;0#1~");

        Assert.NotNull(bitmap);
        Assert.Equal(1, bitmap.Width);
        Assert.Equal(6, bitmap.Height);
    }

    [Fact]
    public async Task Decode_OverdrawnBand_KeepsAllocationsBounded()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        // '$' returns to column 0 of the same band, so every "!2000~$" repaints the full width.
        // Each run is inside the caps; how many there are is limited only by the payload length.
        string dcs = "q" + string.Concat(Enumerable.Repeat("!2000~$", 20_000));
        CancellationToken ct = TestContext.Current.CancellationToken;
        long allocated = 0;

        using var bitmap = await Task.Run(() =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            SKBitmap? decoded = new SixelDecoder().Decode(dcs);
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            return decoded;
        }, ct).WaitAsync(DecodeTimeout, ct);

        Assert.NotNull(bitmap);
        Assert.Equal(Cap, bitmap.Width);
        Assert.Equal(6, bitmap.Height);
        // A 140 K-char payload and a 2000 x 6 image: storage that grew with the number of runs
        // rather than with the pixels they cover would need hundreds of MB here.
        Assert.True(allocated < 4 * 1024 * 1024, $"decode allocated {allocated:N0} bytes");
    }

    [Fact]
    public void Decode_SmallMultiColorImage_PixelsAreExact()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        // Band 1: three red columns, then '$' back to column 0 and green over row 1 of columns
        // 1-2 ('?' paints nothing). Band 2: a red dot on its top row, then a blue register
        // defined mid-stream and drawn on its bottom row with a repeat.
        using var bitmap = new SixelDecoder().Decode(
            "0;0;0q#1;2;100;0;0#2;2;0;100;0#1!3~$#2?!2A-#1@#3;2;0;0;100#3!2_");

        string[] expected =
        [
            "RRR",
            "RGG",
            "RRR",
            "RRR",
            "RRR",
            "RRR",
            "R..",
            "...",
            "...",
            "...",
            "...",
            ".BB",
        ];
        Assert.NotNull(bitmap);
        Assert.Equal(expected, ToRows(bitmap));
    }

    private static string[] ToRows(SKBitmap bitmap)
    {
        var rows = new string[bitmap.Height];
        for (int y = 0; y < bitmap.Height; y++)
        {
            var row = new char[bitmap.Width];
            for (int x = 0; x < bitmap.Width; x++)
            {
                SKColor c = bitmap.GetPixel(x, y);
                row[x] = c.Alpha == 0 ? '.'
                    : c == new SKColor(255, 0, 0) ? 'R'
                    : c == new SKColor(0, 255, 0) ? 'G'
                    : c == new SKColor(0, 0, 255) ? 'B'
                    : '?';
            }
            rows[y] = new string(row);
        }
        return rows;
    }
}
