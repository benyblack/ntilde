using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Xunit;

namespace Ntilde.VT.Tests;

/// <summary>
/// The decoder-independent PNG reader (#1017): every colour type, bit depth, filter and interlace method, against pixels an independent
/// test encoder wrote, and refusal of anything malformed or oversized. A host decoder is not involved.
/// </summary>
public sealed class PngPixelReaderTests
{
    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc(byte[] bytes)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte value in bytes) crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }

    private static byte[] Chunk(string type, byte[] data)
    {
        byte[] body = [.. System.Text.Encoding.ASCII.GetBytes(type), .. data], output = new byte[body.Length + 8];
        BinaryPrimitives.WriteUInt32BigEndian(output, (uint)data.Length);
        body.CopyTo(output, 4);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(body.Length + 4), Crc(body));
        return output;
    }

    private static readonly (int X, int Y, int Dx, int Dy)[] Adam7 = [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)];

    private static byte[] Pack(int[] samples, int depth)
    {
        int bits = samples.Length * depth;
        var bytes = new byte[(bits + 7) / 8];
        for (int i = 0; i < samples.Length; i++)
        {
            if (depth == 16) { bytes[i * 2] = (byte)(samples[i] >> 8); bytes[i * 2 + 1] = (byte)samples[i]; }
            else if (depth == 8) bytes[i] = (byte)samples[i];
            else bytes[i * depth / 8] |= (byte)(samples[i] << (8 - depth - i * depth % 8));
        }
        return bytes;
    }

    private static byte Forward(int filter, byte value, byte left, byte up, byte upLeft)
    {
        static int Paeth(int a, int b, int c)
        {
            int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }
        return filter switch
        {
            1 => (byte)(value - left),
            2 => (byte)(value - up),
            3 => (byte)(value - ((left + up) >> 1)),
            4 => (byte)(value - Paeth(left, up, upLeft)),
            _ => value,
        };
    }

    /// <summary>One image of <paramref name="channels"/> samples per pixel, filtered row by row (filter = row index mod 5) and optionally Adam7 interlaced.</summary>
    internal static byte[] Encode(int width, int height, int colorType, int depth, bool interlace, int[][] pixels, byte[]? palette = null, byte[]? trns = null, int? filterOverride = null)
    {
        int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 }, bpp = Math.Max(1, channels * depth / 8);
        var raw = new MemoryStream();
        IEnumerable<(int X, int Y, int Dx, int Dy)> passes = interlace ? Adam7 : [(0, 0, 1, 1)];
        foreach ((int x0, int y0, int dx, int dy) in passes)
        {
            int w = width > x0 ? (width - x0 + dx - 1) / dx : 0, h = height > y0 ? (height - y0 + dy - 1) / dy : 0;
            if (w == 0 || h == 0) continue;
            byte[] previous = [];
            for (int row = 0; row < h; row++)
            {
                byte[] current = Pack(Enumerable.Range(0, w).SelectMany(col => pixels[(y0 + row * dy) * width + x0 + col * dx]).ToArray(), depth);
                int filter = filterOverride ?? row % 5;
                raw.WriteByte((byte)filter);
                for (int i = 0; i < current.Length; i++)
                    raw.WriteByte(Forward(filter, current[i], i >= bpp ? current[i - bpp] : (byte)0, previous.Length == 0 ? (byte)0 : previous[i],
                        i >= bpp && previous.Length != 0 ? previous[i - bpp] : (byte)0));
                previous = current;
            }
        }
        var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) zlib.Write(raw.ToArray());
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        (header[8], header[9], header[12]) = ((byte)depth, (byte)colorType, (byte)(interlace ? 1 : 0));
        var output = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        output.AddRange(Chunk("IHDR", header));
        if (palette is not null) output.AddRange(Chunk("PLTE", palette));
        if (trns is not null) output.AddRange(Chunk("tRNS", trns));
        output.AddRange(Chunk("IDAT", compressed.ToArray()));
        output.AddRange(Chunk("IEND", []));
        return [.. output];
    }

    private static byte Scale(int sample, int depth) => depth switch { 8 => (byte)sample, 16 => (byte)(sample >> 8), _ => (byte)((sample * 255 + ((1 << depth) - 1) / 2) / ((1 << depth) - 1)) };

    internal static int[][] Samples(int width, int height, int channels, int depth, int seed)
    {
        var random = new Random(seed);
        int max = (1 << depth) - 1;
        return Enumerable.Range(0, width * height).Select(_ => Enumerable.Range(0, channels).Select(_ => random.Next(0, max + 1)).ToArray()).ToArray();
    }

    private static bool Read(byte[] png, out int width, out int height, out byte[] rgba) => PngPixelReader.TryRead(png, 4096, out width, out height, out rgba);

    public static IEnumerable<object[]> Matrix()
    {
        foreach ((int type, int[] depths) in new[] { (0, new[] { 1, 2, 4, 8, 16 }), (2, new[] { 8, 16 }), (3, new[] { 1, 2, 4, 8 }), (4, new[] { 8, 16 }), (6, new[] { 8, 16 }) })
            foreach (int depth in depths)
                foreach (bool interlace in new[] { false, true })
                    yield return [type, depth, interlace];
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Every_colour_type_depth_and_interlace_reads_back_the_pixels_that_were_written(int colorType, int depth, bool interlace)
    {
        const int width = 19, height = 13;                                  // not multiples of 8, so every Adam7 pass has a different size
        int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
        int[][] pixels = Samples(width, height, channels, depth, seed: colorType * 100 + depth);
        byte[]? palette = null;
        byte[]? alpha = null;
        if (colorType == 3)
        {
            int entries = 1 << depth;
            palette = Enumerable.Range(0, entries * 3).Select(i => (byte)(i * 37 + 11)).ToArray();
            alpha = Enumerable.Range(0, Math.Min(entries, 5)).Select(i => (byte)(i * 50)).ToArray();
        }
        byte[] png = Encode(width, height, colorType, depth, interlace, pixels, palette, alpha);
        Assert.True(Read(png, out int w, out int h, out byte[] rgba));
        Assert.Equal((width, height), (w, h));
        for (int i = 0; i < width * height; i++)
        {
            int[] p = pixels[i];
            byte[] expected = colorType switch
            {
                0 => [Scale(p[0], depth), Scale(p[0], depth), Scale(p[0], depth), 255],
                2 => [Scale(p[0], depth), Scale(p[1], depth), Scale(p[2], depth), 255],
                3 => [palette![p[0] * 3], palette[p[0] * 3 + 1], palette[p[0] * 3 + 2], p[0] < alpha!.Length ? alpha[p[0]] : (byte)255],
                4 => [Scale(p[0], depth), Scale(p[0], depth), Scale(p[0], depth), Scale(p[1], depth)],
                _ => [Scale(p[0], depth), Scale(p[1], depth), Scale(p[2], depth), Scale(p[3], depth)],
            };
            Assert.Equal(expected, rgba[(i * 4)..(i * 4 + 4)]);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Each_row_filter_is_reversed_exactly(int filter)
    {
        int[][] pixels = Samples(11, 7, 4, 8, seed: 5 + filter);
        Assert.True(Read(Encode(11, 7, 6, 8, false, pixels, filterOverride: filter), out _, out _, out byte[] rgba));
        Assert.Equal(pixels.SelectMany(p => p.Select(v => (byte)v)).ToArray(), rgba);
    }

    [Fact]
    public void A_colour_key_makes_the_matching_gray_and_RGB_pixels_transparent()
    {
        int[][] gray = [[3], [7], [3], [1]];
        Assert.True(Read(Encode(2, 2, 0, 8, false, gray, trns: [0, 3]), out _, out _, out byte[] g));
        Assert.Equal(new byte[] { 3, 3, 3, 0, 7, 7, 7, 255, 3, 3, 3, 0, 1, 1, 1, 255 }, g);
        int[][] rgb = [[1, 2, 3], [4, 5, 6]];
        Assert.True(Read(Encode(2, 1, 2, 8, false, rgb, trns: [0, 1, 0, 2, 0, 3]), out _, out _, out byte[] c));
        Assert.Equal(new byte[] { 1, 2, 3, 0, 4, 5, 6, 255 }, c);
    }

    [Fact]
    public void A_known_one_pixel_png_decodes_to_its_colour()
    {
        // 1x1, 8-bit RGBA (10, 200, 30, 128).
        Assert.True(Read(Encode(1, 1, 6, 8, false, [[10, 200, 30, 128]]), out int w, out int h, out byte[] rgba));
        Assert.Equal((1, 1), (w, h));
        Assert.Equal(new byte[] { 10, 200, 30, 128 }, rgba);
    }

    [Fact]
    public void Truncated_corrupt_or_malformed_data_is_refused()
    {
        byte[] good = Encode(8, 8, 6, 8, false, Samples(8, 8, 4, 8, 1));
        Assert.True(Read(good, out _, out _, out _));
        Assert.False(Read([], out _, out _, out _));
        Assert.False(Read(good[..(good.Length - 1)], out _, out _, out _));                  // no complete IEND
        Assert.False(Read(good[..20], out _, out _, out _));
        byte[] badCrc = (byte[])good.Clone(); badCrc[30] ^= 1;
        Assert.False(Read(badCrc, out _, out _, out _));
        byte[] badSignature = (byte[])good.Clone(); badSignature[1] = (byte)'X';
        Assert.False(Read(badSignature, out _, out _, out _));
        // A filter byte past 4, and an IDAT stream that inflates to the wrong size.
        Assert.False(Read(Encode(8, 8, 6, 8, false, Samples(8, 8, 4, 8, 1), filterOverride: 9), out _, out _, out _));
    }

    [Fact]
    public void An_unsupported_header_or_a_missing_palette_is_refused()
    {
        Assert.False(Read(Encode(4, 4, 3, 8, false, Samples(4, 4, 1, 8, 1)), out _, out _, out _));                // palette type without PLTE
        Assert.False(Read(Encode(4, 4, 2, 4, false, Samples(4, 4, 3, 4, 1)), out _, out _, out _));                // 4-bit RGB does not exist
        Assert.False(Read(Encode(4, 4, 0, 3, false, Samples(4, 4, 1, 3, 1)), out _, out _, out _));
    }

    [Fact]
    public void An_image_past_the_dimension_bound_is_refused_before_anything_is_inflated()
    {
        byte[] png = Encode(16, 16, 6, 8, false, Samples(16, 16, 4, 8, 1));
        Assert.True(PngPixelReader.TryRead(png, 16, out _, out _, out _));
        Assert.False(PngPixelReader.TryRead(png, 15, out int w, out int h, out byte[] rgba));
        Assert.Equal((0, 0, 0), (w, h, rgba.Length));
    }

    [Fact]
    public void A_stream_that_inflates_past_what_the_header_implies_is_refused()
    {
        // A valid 4x4 header whose IDAT inflates to far more than 4 rows: the bounded inflate refuses it rather than reading it.
        var raw = new byte[10_000];
        var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) zlib.Write(raw);
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, 4); BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 4);
        (header[8], header[9]) = (8, 6);
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Chunk("IHDR", header), .. Chunk("IDAT", compressed.ToArray()), .. Chunk("IEND", [])];
        Assert.False(Read(png, out _, out _, out _));
    }

    [Fact]
    public void Unknown_ancillary_chunks_are_skipped_but_an_unknown_critical_chunk_is_refused()
    {
        byte[] good = Encode(2, 2, 6, 8, false, Samples(2, 2, 4, 8, 1));
        byte[] ancillary = [.. good[..33], .. Chunk("tEXt", "k\0v"u8.ToArray()), .. good[33..]];
        Assert.True(Read(ancillary, out _, out _, out _));
        byte[] critical = [.. good[..33], .. Chunk("XYZW", [1]), .. good[33..]];
        Assert.False(Read(critical, out _, out _, out _));
    }
}
