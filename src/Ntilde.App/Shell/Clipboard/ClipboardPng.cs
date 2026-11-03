using System.Buffers.Binary;
using Avalonia.Media.Imaging;

namespace Ntilde.Shell.Clipboard;

internal static class ClipboardPng
{
    // Avalonia's Win32 OLE adapter renders PNG formats from DataFormat.Bitmap.
    // Retain the original encoded bytes separately so our MIME reads/aliases stay exact.
    // Not a valid MIME name: an OSC client cannot shadow this native-only slot.
    internal const string OriginalFormat = "Ntilde Original PNG";
    private static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];

    public static byte[] FrameOriginal(byte[] data)
    {
        byte[] framed = new byte[checked(data.Length + sizeof(uint))];
        BinaryPrimitives.WriteUInt32BigEndian(framed, checked((uint)data.Length));
        data.CopyTo(framed, sizeof(uint));
        return framed;
    }

    public static byte[] ReadOriginal(byte[] framed, int budget)
    {
        if (framed.Length < sizeof(uint)) throw new InvalidDataException("Missing original PNG length.");
        uint length = BinaryPrimitives.ReadUInt32BigEndian(framed);
        if (length > framed.Length - sizeof(uint)) throw new InvalidDataException("Truncated original PNG.");
        if (length > Math.Max(0, budget)) throw new IOException("Clipboard item exceeds the remaining read budget.");
        // HGLOBAL may include allocation padding. Only the declared payload belongs to this MIME value.
        return framed.AsSpan(sizeof(uint), checked((int)length)).ToArray();
    }

    public static int PixelBytes(byte[] data, int budget)
    {
        if (data.Length < 33 || !data.AsSpan(0, 8).SequenceEqual(Signature) ||
            BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8, 4)) != 13 || !data.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            throw new InvalidDataException("Invalid PNG header.");
        uint width = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16, 4));
        uint height = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(20, 4));
        if (width == 0 || height == 0) throw new InvalidDataException("Invalid PNG dimensions.");
        if (budget < 0 || width > (ulong)budget / 4 / height)
            throw new ClipboardSizeLimitException("Clipboard bitmap exceeds the native pixel budget.");
        return checked((int)((ulong)width * height * 4));
    }

    public static Bitmap Decode(byte[] data, int budget)
    {
        _ = PixelBytes(data, budget);
        using var source = new MemoryStream(data, writable: false);
        var bitmap = new Bitmap(source);
        if (bitmap.PixelSize.Width == BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16, 4)) &&
            bitmap.PixelSize.Height == BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(20, 4))) return bitmap;
        bitmap.Dispose();
        throw new InvalidDataException("Decoded PNG dimensions differ from its header.");
    }
}
