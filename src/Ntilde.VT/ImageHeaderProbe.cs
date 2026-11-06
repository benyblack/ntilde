using System.Buffers.Binary;

namespace Ntilde.VT;

/// <summary>An image container format <see cref="ImageHeaderProbe"/> can size.</summary>
public enum ImageContainerFormat
{
    /// <summary>Not recognised.</summary>
    Unknown,
    /// <summary>PNG, sized from <c>IHDR</c>.</summary>
    Png,
    /// <summary>JPEG, sized from the first start-of-frame segment.</summary>
    Jpeg,
    /// <summary>GIF, sized from the logical screen descriptor.</summary>
    Gif,
    /// <summary>WebP, sized from its <c>VP8 </c>, <c>VP8L</c> or <c>VP8X</c> chunk.</summary>
    WebP,
}

/// <summary>
/// Reads an image's pixel size from its header, without decoding it (#838). Kitty and iTerm2
/// images are placed from this size, never from a decoder, so a parser with no decoder (the
/// persistent-session daemon) and one with a decoder (a window) reserve the same cells and give
/// the same replies for the same bytes. Only the header is read; a valid header followed by a
/// corrupt body still sizes, and the decoder's failure then only means nothing is drawn.
/// </summary>
public static class ImageHeaderProbe
{
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// The format and pixel size the header declares. False for anything else: an unknown or
    /// truncated header, or a declared size of zero.
    /// </summary>
    public static bool TryGetSize(ReadOnlySpan<byte> data, out ImageContainerFormat format, out int width, out int height)
    {
        format = ImageContainerFormat.Unknown;
        width = height = 0;
        long w, h;
        if (data.StartsWith(PngSignature))
        {
            // Signature, then the first chunk, which must be IHDR: length, type, width, height.
            if (data.Length < 24 || !data.Slice(12, 4).SequenceEqual("IHDR"u8)) return false;
            (format, w, h) = (ImageContainerFormat.Png, BinaryPrimitives.ReadUInt32BigEndian(data[16..]), BinaryPrimitives.ReadUInt32BigEndian(data[20..]));
        }
        else if (data.Length >= 10 && (data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8)))
        {
            (format, w, h) = (ImageContainerFormat.Gif, BinaryPrimitives.ReadUInt16LittleEndian(data[6..]), BinaryPrimitives.ReadUInt16LittleEndian(data[8..]));
        }
        else if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8)
        {
            if (!TryJpegSize(data, out w, out h)) return false;
            format = ImageContainerFormat.Jpeg;
        }
        else if (data.Length >= 16 && data.StartsWith("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            if (!TryWebPSize(data, out w, out h)) return false;
            format = ImageContainerFormat.WebP;
        }
        else return false;

        if (w <= 0 || h <= 0 || w > int.MaxValue || h > int.MaxValue)
        {
            format = ImageContainerFormat.Unknown;
            return false;
        }
        (width, height) = ((int)w, (int)h);
        return true;
    }

    /// <summary>
    /// Walks the marker segments to the first start-of-frame (SOF0-SOF15 except DHT, JPG and DAC),
    /// whose body is precision, height, width. Stops at start-of-scan or the end of the data.
    /// </summary>
    private static bool TryJpegSize(ReadOnlySpan<byte> data, out long width, out long height)
    {
        width = height = 0;
        int i = 2;
        while (i + 1 < data.Length)
        {
            if (data[i] != 0xFF) return false;
            byte marker = data[i + 1];
            i += 2;
            if (marker == 0xFF) { i--; continue; } // fill byte
            if (marker is 0x01 or (>= 0xD0 and <= 0xD7)) continue; // no length
            if (marker is 0xD9 or 0xDA) return false; // end of image, start of scan: no frame
            if (i + 2 > data.Length) return false;
            int length = BinaryPrimitives.ReadUInt16BigEndian(data[i..]);
            if (length < 2) return false;
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                if (length < 7 || i + 7 > data.Length) return false;
                height = BinaryPrimitives.ReadUInt16BigEndian(data[(i + 3)..]);
                width = BinaryPrimitives.ReadUInt16BigEndian(data[(i + 5)..]);
                return true;
            }
            i += length;
        }
        return false;
    }

    /// <summary>The first chunk after <c>RIFF....WEBP</c> declares the canvas.</summary>
    private static bool TryWebPSize(ReadOnlySpan<byte> data, out long width, out long height)
    {
        width = height = 0;
        ReadOnlySpan<byte> chunk = data.Slice(12, 4);
        if (chunk.SequenceEqual("VP8 "u8))
        {
            // Lossy: a 3-byte frame tag, the start code 9D 01 2A, then 14-bit width and height.
            if (data.Length < 30 || data[23] != 0x9D || data[24] != 0x01 || data[25] != 0x2A) return false;
            width = BinaryPrimitives.ReadUInt16LittleEndian(data[26..]) & 0x3FFF;
            height = BinaryPrimitives.ReadUInt16LittleEndian(data[28..]) & 0x3FFF;
            return true;
        }
        if (chunk.SequenceEqual("VP8L"u8))
        {
            // Lossless: signature 0x2F, then width-1 and height-1 in 14 bits each.
            if (data.Length < 25 || data[20] != 0x2F) return false;
            uint bits = BinaryPrimitives.ReadUInt32LittleEndian(data[21..]);
            width = (bits & 0x3FFF) + 1;
            height = ((bits >> 14) & 0x3FFF) + 1;
            return true;
        }
        if (chunk.SequenceEqual("VP8X"u8))
        {
            // Extended: flags and reserved bytes, then canvas width-1 and height-1 in 24 bits each.
            if (data.Length < 30) return false;
            width = (data[24] | (data[25] << 8) | (data[26] << 16)) + 1L;
            height = (data[27] | (data[28] << 8) | (data[29] << 16)) + 1L;
            return true;
        }
        return false;
    }
}
