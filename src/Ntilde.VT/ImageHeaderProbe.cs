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
        format = Detect(data);
        long w = 0, h = 0;
        bool sized = format switch
        {
            ImageContainerFormat.Png => TryPngSize(data, out w, out h),
            ImageContainerFormat.Gif => TryGifSize(data, out w, out h),
            ImageContainerFormat.Jpeg => TryJpegSize(data, out w, out h),
            ImageContainerFormat.WebP => TryWebPSize(data, out w, out h),
            _ => false,
        };
        if (!sized || w <= 0 || h <= 0 || w > int.MaxValue || h > int.MaxValue)
        {
            format = ImageContainerFormat.Unknown;
            width = height = 0;
            return false;
        }
        (width, height) = ((int)w, (int)h);
        return true;
    }

    /// <summary>Which format the leading signature claims; its header is then checked by that format's reader.</summary>
    private static ImageContainerFormat Detect(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith(PngSignature)) return ImageContainerFormat.Png;
        if (data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8)) return ImageContainerFormat.Gif;
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xD8) return ImageContainerFormat.Jpeg;
        if (data.Length >= 12 && data.StartsWith("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8)) return ImageContainerFormat.WebP;
        return ImageContainerFormat.Unknown;
    }

    /// <summary>Signature, then the first chunk, which must be IHDR: length, type, width, height.</summary>
    private static bool TryPngSize(ReadOnlySpan<byte> data, out long width, out long height)
    {
        width = height = 0;
        if (data.Length < 24 || !data.Slice(12, 4).SequenceEqual("IHDR"u8)) return false;
        width = BinaryPrimitives.ReadUInt32BigEndian(data[16..]);
        height = BinaryPrimitives.ReadUInt32BigEndian(data[20..]);
        return true;
    }

    /// <summary>The logical screen descriptor follows the six-byte signature.</summary>
    private static bool TryGifSize(ReadOnlySpan<byte> data, out long width, out long height)
    {
        width = height = 0;
        if (data.Length < 10) return false;
        width = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
        height = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);
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
        while (TryNextJpegMarker(data, ref i, out byte marker))
        {
            if (marker is 0x01 or (>= 0xD0 and <= 0xD7)) continue; // no length
            if (marker is 0xD9 or 0xDA) return false; // end of image, start of scan: no frame
            if (i + 2 > data.Length) return false;
            int length = BinaryPrimitives.ReadUInt16BigEndian(data[i..]);
            if (length < 2) return false;
            if (IsJpegFrameMarker(marker)) return TryJpegFrameSize(data, i, length, out width, out height);
            i += length;
        }
        return false;
    }

    /// <summary>Reads the marker at <paramref name="i"/>, skipping fill bytes, and moves past it.</summary>
    private static bool TryNextJpegMarker(ReadOnlySpan<byte> data, ref int i, out byte marker)
    {
        marker = 0;
        if (i >= data.Length || data[i] != 0xFF) return false;
        while (i < data.Length && data[i] == 0xFF) i++;
        if (i >= data.Length) return false;
        marker = data[i++];
        return true;
    }

    private static bool IsJpegFrameMarker(byte marker) => marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC;

    /// <summary>A frame segment: length, precision, then height and width.</summary>
    private static bool TryJpegFrameSize(ReadOnlySpan<byte> data, int segment, int length, out long width, out long height)
    {
        width = height = 0;
        if (length < 7 || segment + 7 > data.Length) return false;
        height = BinaryPrimitives.ReadUInt16BigEndian(data[(segment + 3)..]);
        width = BinaryPrimitives.ReadUInt16BigEndian(data[(segment + 5)..]);
        return true;
    }

    /// <summary>The first chunk after <c>RIFF....WEBP</c> declares the canvas.</summary>
    private static bool TryWebPSize(ReadOnlySpan<byte> data, out long width, out long height)
    {
        width = height = 0;
        if (data.Length < 16) return false;
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
