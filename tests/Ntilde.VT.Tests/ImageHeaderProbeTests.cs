using System.Buffers.Binary;
using Ntilde.VT;

namespace Ntilde.VT.Tests;

/// <summary>
/// The decoder-free header reader Kitty and iTerm2 placement uses (#838). Real encoders' output is
/// checked against Skia in <c>Ntilde.Rendering.Tests</c>; these pin the header layouts and the
/// failure cases byte for byte.
/// </summary>
public sealed class ImageHeaderProbeTests
{
    [Fact]
    public void Png_is_sized_from_IHDR()
    {
        Assert.True(ImageHeaderProbe.TryGetSize(TestImages.PngHeader(640, 480), out var format, out int w, out int h));
        Assert.Equal((ImageContainerFormat.Png, 640, 480), (format, w, h));
    }

    [Theory]
    [InlineData("GIF87a")]
    [InlineData("GIF89a")]
    public void Gif_is_sized_from_the_logical_screen(string version)
    {
        byte[] gif = [.. System.Text.Encoding.ASCII.GetBytes(version), 0x2C, 0x01, 0x90, 0x00, 0, 0, 0];
        Assert.True(ImageHeaderProbe.TryGetSize(gif, out var format, out int w, out int h));
        Assert.Equal((ImageContainerFormat.Gif, 300, 144), (format, w, h));
    }

    [Theory]
    [InlineData(0xC0)] // baseline
    [InlineData(0xC2)] // progressive
    [InlineData(0xC1)]
    public void Jpeg_is_sized_from_the_first_frame_after_other_segments(int sof)
    {
        byte[] jpeg = Jpeg(App0(), Segment(0xDB, new byte[65]), Segment(0xC4, new byte[20]), Sof(sof, 123, 456));
        Assert.True(ImageHeaderProbe.TryGetSize(jpeg, out var format, out int w, out int h));
        Assert.Equal((ImageContainerFormat.Jpeg, 456, 123), (format, w, h));
    }

    [Fact]
    public void Jpeg_fill_bytes_and_restart_markers_are_skipped()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xFF, 0xFF, .. App0()[1..], 0xFF, 0xD0, .. Sof(0xC0, 2, 3)];
        Assert.True(ImageHeaderProbe.TryGetSize(jpeg, out _, out int w, out int h));
        Assert.Equal((3, 2), (w, h));
    }

    [Theory]
    [InlineData(0xC4)] // DHT, not a frame
    [InlineData(0xC8)] // JPG extension
    [InlineData(0xCC)] // DAC
    public void Jpeg_table_markers_in_the_frame_range_are_not_frames(int marker)
    {
        Assert.False(ImageHeaderProbe.TryGetSize(Jpeg(Segment(marker, [8, 0, 10, 0, 20, 3])), out _, out _, out _));
    }

    [Fact]
    public void Jpeg_without_a_frame_before_the_scan_is_refused()
    {
        Assert.False(ImageHeaderProbe.TryGetSize(Jpeg(App0(), Segment(0xDA, new byte[10]), Sof(0xC0, 2, 3)), out _, out _, out _));
    }

    [Fact]
    public void WebP_lossy_is_sized_from_VP8()
    {
        byte[] vp8 = new byte[10];
        vp8[3] = 0x9D; vp8[4] = 0x01; vp8[5] = 0x2A;
        BinaryPrimitives.WriteUInt16LittleEndian(vp8.AsSpan(6), 0xC000 | 400); // scale bits are ignored
        BinaryPrimitives.WriteUInt16LittleEndian(vp8.AsSpan(8), 300);
        Assert.True(ImageHeaderProbe.TryGetSize(WebP("VP8 ", vp8), out var format, out int w, out int h));
        Assert.Equal((ImageContainerFormat.WebP, 400, 300), (format, w, h));
    }

    [Fact]
    public void WebP_lossless_is_sized_from_VP8L()
    {
        byte[] vp8l = new byte[5];
        vp8l[0] = 0x2F;
        BinaryPrimitives.WriteUInt32LittleEndian(vp8l.AsSpan(1), (uint)(1999 | (14 << 14))); // 2000 x 15
        Assert.True(ImageHeaderProbe.TryGetSize(WebP("VP8L", vp8l), out _, out int w, out int h));
        Assert.Equal((2000, 15), (w, h));
    }

    [Fact]
    public void WebP_extended_is_sized_from_the_VP8X_canvas()
    {
        byte[] vp8x = new byte[10];
        (vp8x[4], vp8x[5], vp8x[6]) = (0x0F, 0x27, 0x00); // 9999 + 1
        (vp8x[7], vp8x[8], vp8x[9]) = (0x00, 0x00, 0x00); // 0 + 1
        Assert.True(ImageHeaderProbe.TryGetSize(WebP("VP8X", vp8x), out _, out int w, out int h));
        Assert.Equal((10000, 1), (w, h));
    }

    public static TheoryData<string, byte[]> Refused => new()
    {
        { "empty", [] },
        { "png signature only", TestImages.PngHeader(1, 1)[..8] },
        { "png truncated in IHDR", TestImages.PngHeader(1, 1)[..20] },
        { "png truncated after the dimensions", TestImages.PngHeader(1, 1)[..24] },
        { "png IHDR without its CRC", TestImages.PngHeader(1, 1)[..29] },
        { "png IHDR length not 13", PngWithIhdrLength(12) },
        { "png first chunk not IHDR", PngWithChunk("tEXt") },
        { "png zero width", TestImages.PngHeader(0, 5) },
        { "png width past int", PngPastInt() },
        { "gif truncated", "GIF89a\x01"u8.ToArray() },
        { "gif zero height", [.. "GIF89a"u8.ToArray(), 1, 0, 0, 0] },
        { "jpeg soi only", [0xFF, 0xD8] },
        { "jpeg garbage between segments", [0xFF, 0xD8, 0x00, 0xFF, 0xC0] },
        { "jpeg truncated frame", Jpeg(Sof(0xC0, 2, 3))[..9] },
        { "jpeg zero size", Jpeg(Sof(0xC0, 0, 3)) },
        { "webp unknown chunk", WebP("ALPH", new byte[10]) },
        { "webp lossy bad start code", WebP("VP8 ", new byte[10]) },
        { "webp lossless bad signature", WebP("VP8L", new byte[5]) },
        { "bmp", [.. "BM"u8.ToArray(), .. new byte[60]] },
        { "text", "not an image at all"u8.ToArray() },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void Anything_else_is_refused(string name, byte[] data)
    {
        Assert.False(ImageHeaderProbe.TryGetSize(data, out var format, out int w, out int h), name);
        Assert.Equal((ImageContainerFormat.Unknown, 0, 0), (format, w, h));
    }

    [Fact]
    public void Only_the_header_is_read_so_a_corrupt_body_still_sizes()
    {
        byte[] png = [.. TestImages.PngHeader(9, 4), .. "this is not a valid PNG body"u8.ToArray()];
        Assert.True(ImageHeaderProbe.TryGetSize(png, out _, out int w, out int h));
        Assert.Equal((9, 4), (w, h));
    }

    private static byte[] Jpeg(params byte[][] segments) => [0xFF, 0xD8, .. segments.SelectMany(s => s)];

    private static byte[] Segment(int marker, byte[] body)
    {
        var segment = new byte[4 + body.Length];
        segment[0] = 0xFF; segment[1] = (byte)marker;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)(body.Length + 2));
        body.CopyTo(segment, 4);
        return segment;
    }

    private static byte[] App0() => Segment(0xE0, [.. "JFIF\0"u8.ToArray(), 1, 1, 0, 0, 1, 0, 1, 0, 0]);

    private static byte[] Sof(int marker, int height, int width)
    {
        var body = new byte[15];
        body[0] = 8;
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(1), (ushort)height);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(3), (ushort)width);
        body[5] = 3;
        return Segment(marker, body);
    }

    private static byte[] WebP(string chunk, byte[] payload)
    {
        byte[] data = [.. "RIFF"u8.ToArray(), 0, 0, 0, 0, .. "WEBP"u8.ToArray(), .. System.Text.Encoding.ASCII.GetBytes(chunk), 0, 0, 0, 0, .. payload];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)(data.Length - 8));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), (uint)payload.Length);
        return data;
    }

    private static byte[] PngWithChunk(string type)
    {
        byte[] png = TestImages.PngHeader(1, 1);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(png, 12);
        return png;
    }

    private static byte[] PngWithIhdrLength(uint length)
    {
        byte[] png = TestImages.PngHeader(1, 1);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(8), length);
        return png;
    }

    private static byte[] PngPastInt()
    {
        byte[] png = TestImages.PngHeader(1, 1);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), 0x80000000);
        return png;
    }
}
