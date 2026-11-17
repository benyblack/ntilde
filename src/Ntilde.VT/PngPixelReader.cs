using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Ntilde.VT;

/// <summary>
/// Reads a PNG into straight-alpha RGBA without any image decoder (#1017). A persistent session's daemon has no decoder while its clients do,
/// yet all of them must accept or refuse a Kitty PNG frame from the same bytes, so the pixels animation composes cannot come from a host decoder.
/// Every standard colour type, bit depth and interlace method is read; sample values are taken as stored (no gamma or profile), 16-bit samples
/// by their high byte and gray samples scaled to 8 bits. Anything malformed or past the bounds is refused, never partially read.
/// </summary>
internal static class PngPixelReader
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();

    private readonly record struct Header(int Width, int Height, int BitDepth, int ColorType, bool Interlaced)
    {
        internal int Channels => ColorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
        internal int BitsPerPixel => Channels * BitDepth;
        internal int FilterBytes => Math.Max(1, BitsPerPixel / 8);
        internal int RowBytes(int width) => (int)(((long)width * BitsPerPixel + 7) / 8);
    }

    /// <summary>Palette and transparency chunks, as read.</summary>
    private sealed class Ancillary
    {
        internal byte[] Palette = [];
        internal byte[] PaletteAlpha = [];
        internal int[]? TransparentSample;
    }

    /// <summary>Adam7 passes: first column, first row, column step, row step.</summary>
    private static readonly (int X, int Y, int Dx, int Dy)[] Passes = [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)];

    /// <summary>
    /// True with the image's size and top-down RGBA when <paramref name="png"/> is a valid PNG no larger than <paramref name="maxDimension"/>
    /// on either side. The unfiltered data is never allowed past what the header's own size implies.
    /// </summary>
    internal static bool TryRead(ReadOnlySpan<byte> png, int maxDimension, out int width, out int height, out byte[] rgba)
    {
        (width, height, rgba) = (0, 0, []);
        if (!TryReadChunks(png, maxDimension, out Header header, out Ancillary ancillary, out byte[] compressed)) return false;
        if (!TryUnfilter(header, compressed, out byte[] raster)) return false;
        rgba = ToRgba(header, ancillary, raster);
        (width, height) = (header.Width, header.Height);
        return true;
    }

    private static bool TryReadChunks(ReadOnlySpan<byte> png, int maxDimension, out Header header, out Ancillary ancillary, out byte[] compressed)
    {
        header = default;
        ancillary = new Ancillary();
        compressed = [];
        if (!png.StartsWith(Signature)) return false;
        var idat = new MemoryStream();
        bool haveHeader = false, ended = false, haveData = false;
        int position = Signature.Length;
        while (position + 12 <= png.Length && !ended)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(png[position..]);
            if (length > int.MaxValue || position + 12L + length > png.Length) return false;
            ReadOnlySpan<byte> type = png.Slice(position + 4, 4), data = png.Slice(position + 8, (int)length);
            if (BinaryPrimitives.ReadUInt32BigEndian(png[(position + 8 + (int)length)..]) != Crc(png.Slice(position + 4, 4 + (int)length))) return false;
            position += 12 + (int)length;
            if (type.SequenceEqual("IHDR"u8))
            {
                if (haveHeader || !TryReadHeader(data, maxDimension, out header)) return false;
                haveHeader = true;
            }
            else if (!haveHeader) return false;
            else if (type.SequenceEqual("PLTE"u8)) { if (!TryReadPalette(data, header, ancillary)) return false; }
            else if (type.SequenceEqual("tRNS"u8)) { if (!TryReadTransparency(data, header, ancillary)) return false; }
            else if (type.SequenceEqual("IDAT"u8)) { idat.Write(data); haveData = true; }
            else if (type.SequenceEqual("IEND"u8)) ended = true;
            else if ((type[0] & 0x20) == 0) return false; // an unknown critical chunk
        }
        if (!haveHeader || !haveData || !ended || (header.ColorType == 3 && ancillary.Palette.Length == 0)) return false;
        compressed = idat.ToArray();
        return true;
    }

    private static bool TryReadHeader(ReadOnlySpan<byte> data, int maxDimension, out Header header)
    {
        header = default;
        if (data.Length != 13) return false;
        long width = BinaryPrimitives.ReadUInt32BigEndian(data), height = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        int depth = data[8], type = data[9];
        bool valid = width >= 1 && height >= 1 && width <= maxDimension && height <= maxDimension
            && data[10] == 0 && data[11] == 0 && data[12] <= 1 && ValidDepth(type, depth);
        if (valid) header = new Header((int)width, (int)height, depth, type, data[12] == 1);
        return valid;
    }

    private static bool ValidDepth(int colorType, int depth) => colorType switch
    {
        0 => depth is 1 or 2 or 4 or 8 or 16,
        3 => depth is 1 or 2 or 4 or 8,
        2 or 4 or 6 => depth is 8 or 16,
        _ => false,
    };

    private static bool TryReadPalette(ReadOnlySpan<byte> data, Header header, Ancillary ancillary)
    {
        if (ancillary.Palette.Length != 0 || data.Length == 0 || data.Length % 3 != 0 || data.Length > 3 * 256 || header.ColorType is 0 or 4) return false;
        ancillary.Palette = data.ToArray();
        return true;
    }

    private static bool TryReadTransparency(ReadOnlySpan<byte> data, Header header, Ancillary ancillary)
    {
        switch (header.ColorType)
        {
            case 3:
                if (data.Length > ancillary.Palette.Length / 3) return false;
                ancillary.PaletteAlpha = data.ToArray();
                return true;
            case 0 when data.Length == 2:
                ancillary.TransparentSample = [BinaryPrimitives.ReadUInt16BigEndian(data)];
                return true;
            case 2 when data.Length == 6:
                ancillary.TransparentSample = [BinaryPrimitives.ReadUInt16BigEndian(data), BinaryPrimitives.ReadUInt16BigEndian(data[2..]), BinaryPrimitives.ReadUInt16BigEndian(data[4..])];
                return true;
            default:
                return false;
        }
    }

    /// <summary>Inflates the IDAT stream to exactly what the header's size implies, then reverses the row filters (and the Adam7 passes).</summary>
    private static bool TryUnfilter(Header header, byte[] compressed, out byte[] raster)
    {
        raster = [];
        long expected = 0;
        foreach ((int w, int h) in PassSizes(header)) expected += w == 0 || h == 0 ? 0 : (long)h * (1 + header.RowBytes(w));
        if (expected <= 0 || expected > int.MaxValue || AnsiParser.InflateZlib(compressed, (int)expected) is not { } data || data.Length != expected) return false;
        int stride = header.RowBytes(header.Width);
        raster = new byte[(long)stride * header.Height];
        int offset = 0;
        if (!header.Interlaced)
        {
            if (!UnfilterPass(header, data, ref offset, header.Width, header.Height, out byte[] pass)) return false;
            pass.CopyTo(raster, 0);
            return true;
        }
        for (int index = 0; index < Passes.Length; index++)
        {
            (int w, int h) = PassSize(header, index);
            if (w == 0 || h == 0) continue;
            if (!UnfilterPass(header, data, ref offset, w, h, out byte[] pass)) return false;
            Scatter(header, index, pass, w, h, raster);
        }
        return true;
    }

    private static IEnumerable<(int Width, int Height)> PassSizes(Header header)
    {
        if (!header.Interlaced) { yield return (header.Width, header.Height); yield break; }
        for (int index = 0; index < Passes.Length; index++) yield return PassSize(header, index);
    }

    private static (int Width, int Height) PassSize(Header header, int index)
    {
        (int x, int y, int dx, int dy) = Passes[index];
        int w = header.Width > x ? (header.Width - x + dx - 1) / dx : 0, h = header.Height > y ? (header.Height - y + dy - 1) / dy : 0;
        return (w, h);
    }

    private static bool UnfilterPass(Header header, byte[] data, ref int offset, int width, int height, out byte[] pass)
    {
        int rowBytes = header.RowBytes(width), bpp = header.FilterBytes;
        pass = new byte[rowBytes * height];
        for (int row = 0; row < height; row++)
        {
            byte filter = data[offset++];
            ReadOnlySpan<byte> source = data.AsSpan(offset, rowBytes);
            offset += rowBytes;
            Span<byte> current = pass.AsSpan(row * rowBytes, rowBytes);
            ReadOnlySpan<byte> previous = row == 0 ? [] : pass.AsSpan((row - 1) * rowBytes, rowBytes);
            if (filter > 4) return false;
            for (int i = 0; i < rowBytes; i++) current[i] = Reverse(filter, source[i], i >= bpp ? current[i - bpp] : (byte)0, previous.IsEmpty ? (byte)0 : previous[i],
                i >= bpp && !previous.IsEmpty ? previous[i - bpp] : (byte)0);
        }
        return true;
    }

    private static byte Reverse(byte filter, byte value, byte left, byte up, byte upLeft) => filter switch
    {
        1 => (byte)(value + left),
        2 => (byte)(value + up),
        3 => (byte)(value + ((left + up) >> 1)),
        4 => (byte)(value + Paeth(left, up, upLeft)),
        _ => value,
    };

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>Places one Adam7 pass's pixels into the full-width raster, bit-exactly for sub-byte depths.</summary>
    private static void Scatter(Header header, int index, byte[] pass, int width, int height, byte[] raster)
    {
        (int x0, int y0, int dx, int dy) = Passes[index];
        int passStride = header.RowBytes(width), stride = header.RowBytes(header.Width), bits = header.BitsPerPixel;
        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                int destination = x0 + col * dx, destinationRow = y0 + row * dy;
                if (bits >= 8)
                {
                    pass.AsSpan(row * passStride + col * (bits / 8), bits / 8).CopyTo(raster.AsSpan(destinationRow * stride + destination * (bits / 8)));
                    continue;
                }
                int value = (pass[row * passStride + (col * bits) / 8] >> (8 - bits - (col * bits) % 8)) & ((1 << bits) - 1);
                int at = destinationRow * stride + (destination * bits) / 8, shift = 8 - bits - (destination * bits) % 8;
                raster[at] = (byte)(raster[at] | (value << shift));
            }
        }
    }

    private static byte[] ToRgba(Header header, Ancillary ancillary, byte[] raster)
    {
        var rgba = new byte[checked(header.Width * header.Height * 4)];
        int stride = header.RowBytes(header.Width), depth = header.BitDepth, max = (1 << depth) - 1;
        for (int y = 0; y < header.Height; y++)
        {
            for (int x = 0; x < header.Width; x++)
            {
                ReadOnlySpan<byte> row = raster.AsSpan(y * stride, stride);
                (byte r, byte g, byte b, byte a) = Pixel(header, ancillary, row, x, depth, max);
                int at = (y * header.Width + x) * 4;
                rgba[at] = r; rgba[at + 1] = g; rgba[at + 2] = b; rgba[at + 3] = a;
            }
        }
        return rgba;
    }

    private static (byte R, byte G, byte B, byte A) Pixel(Header header, Ancillary ancillary, ReadOnlySpan<byte> row, int x, int depth, int max)
    {
        switch (header.ColorType)
        {
            case 0:
                {
                    int sample = Sample(row, x, depth, 0, 1);
                    byte gray = Scale(sample, depth, max);
                    return (gray, gray, gray, ancillary.TransparentSample is { } key && key[0] == sample ? (byte)0 : (byte)255);
                }
            case 2:
                {
                    int r = Sample(row, x, depth, 0, 3), g = Sample(row, x, depth, 1, 3), b = Sample(row, x, depth, 2, 3);
                    bool transparent = ancillary.TransparentSample is { } key && key[0] == r && key[1] == g && key[2] == b;
                    return (Scale(r, depth, max), Scale(g, depth, max), Scale(b, depth, max), transparent ? (byte)0 : (byte)255);
                }
            case 3:
                {
                    int index = Sample(row, x, depth, 0, 1);
                    if (index * 3 + 2 >= ancillary.Palette.Length) return (0, 0, 0, 255); // an out-of-range index reads as opaque black, as readers commonly do
                    return (ancillary.Palette[index * 3], ancillary.Palette[index * 3 + 1], ancillary.Palette[index * 3 + 2],
                        index < ancillary.PaletteAlpha.Length ? ancillary.PaletteAlpha[index] : (byte)255);
                }
            case 4:
                {
                    byte gray = Scale(Sample(row, x, depth, 0, 2), depth, max);
                    return (gray, gray, gray, Scale(Sample(row, x, depth, 1, 2), depth, max));
                }
            default:
                return (Scale(Sample(row, x, depth, 0, 4), depth, max), Scale(Sample(row, x, depth, 1, 4), depth, max),
                    Scale(Sample(row, x, depth, 2, 4), depth, max), Scale(Sample(row, x, depth, 3, 4), depth, max));
        }
    }

    /// <summary>The <paramref name="channel"/>th sample of pixel <paramref name="x"/>.</summary>
    private static int Sample(ReadOnlySpan<byte> row, int x, int depth, int channel, int channels)
    {
        if (depth == 16) return BinaryPrimitives.ReadUInt16BigEndian(row[((x * channels + channel) * 2)..]);
        if (depth == 8) return row[x * channels + channel];
        int bit = (x * channels + channel) * depth;
        return (row[bit / 8] >> (8 - depth - bit % 8)) & ((1 << depth) - 1);
    }

    private static byte Scale(int sample, int depth, int max) => depth switch
    {
        8 => (byte)sample,
        16 => (byte)(sample >> 8),
        _ => (byte)((sample * 255 + max / 2) / max),
    };

    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte value in bytes) crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
