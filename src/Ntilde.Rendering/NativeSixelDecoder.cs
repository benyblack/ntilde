using System.Runtime.InteropServices;
using Ntilde.VT;
using SkiaSharp;

namespace Ntilde.Rendering;

/// <summary>Fixed-storage decoder for the native-policy owner. Legacy decoding remains unchanged.</summary>
internal static class NativeSixelDecoder
{
    private readonly record struct RasterCanvas(ushort[] Pixels, uint[] Palette, int Width, int Height);
    /// <summary>
    /// Decodes only the canvas and source length admitted by the reservation using fixed charged storage.
    /// A geometry mismatch returns null; failed bitmap construction disposes the partial bitmap.
    /// </summary>
    internal static SKBitmap? Decode(string data, NativeSixelAllocation allocation)
    {
        if (data.Length != allocation.SourceChars ||
            SixelGeometry.Measure(data) != (allocation.Width, allocation.Height)) return null;
        // Both arrays and the exact BGRA bitmap are charged before entry. No substrings,
        // growing bands or per-command collections are allocated from remote input.
        var registers = new ushort[checked(allocation.Width * allocation.Height)];
        var palette = new uint[SixelGeometry.DefaultMaxPaletteRegister + 1];
        ReadOnlySpan<uint> defaults = [0xff000000, 0xff0000ff, 0xffff0000, 0xff00ff00,
            0xffff00ff, 0xff00ffff, 0xffffff00, 0xffffffff];
        defaults.CopyTo(palette);
        PaintRegisters(data.AsSpan(), registers, palette, allocation.Width, allocation.Height);
        SKBitmap? bitmap = null;
        try
        {
            var info = new SKImageInfo(allocation.Width, allocation.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            bitmap = new SKBitmap(info, checked(allocation.Width * sizeof(uint)));
            if (bitmap.GetPixels() == IntPtr.Zero) { bitmap.Dispose(); return null; }
            Span<uint> pixels = MemoryMarshal.Cast<byte, uint>(bitmap.GetPixelSpan());
            for (int i = 0; i < registers.Length; i++)
                pixels[i] = registers[i] == 0 ? 0 : palette[registers[i] - 1];
            return bitmap;
        }
        catch
        {
            bitmap?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Interprets SIXEL commands into the fixed register plane; palette lookup occurs after all commands.
    /// </summary>
    private static void PaintRegisters(ReadOnlySpan<char> data, ushort[] pixels, uint[] palette, int width, int height)
    {
        var canvas = new RasterCanvas(pixels, palette, width, height);
        int x = 0, y = 0, current = 0;
        for (int i = data.IndexOf('q') + 1; i < data.Length;)
        {
            char c = data[i++];
            switch (c)
            {
                case '#': ReadPalette(data, ref i, palette, ref current); continue;
                case '"':
                    SkipParameters(data, ref i);
                    continue;
                case '$': x = 0; continue;
                case '-': x = 0; y = Math.Min(y + 6, SixelGeometry.DefaultMaxPixelDimension); continue;
                case '!':
                    PaintRepeat(data, ref i, ref x, y, current, canvas);
                    continue;
                default:
                    if (c is >= '?' and <= '~') Paint(c, 1, ref x, y, current, canvas);
                    continue;
            }
        }
    }

    /// <summary>
    /// Consumes raster attributes without allocating fields; admitted canvas geometry already bounds storage.
    /// </summary>
    private static void SkipParameters(ReadOnlySpan<char> data, ref int i)
    {
        while (i < data.Length && (char.IsDigit(data[i]) || data[i] == ';')) i++;
    }

    /// <summary>
    /// Saturates remote repeat counts and paints only a following SIXEL data character within the canvas.
    /// </summary>
    private static void PaintRepeat(ReadOnlySpan<char> data, ref int i, ref int x, int y, int current,
        RasterCanvas canvas)
    {
        int start = i;
        long count = 0;
        while (i < data.Length && char.IsAsciiDigit(data[i]))
            count = Math.Min(count * 10 + data[i++] - '0', int.MaxValue);
        if (i > start && i < data.Length && data[i] is >= '?' and <= '~')
            Paint(data[i++], (int)count, ref x, y, current, canvas);
    }

    /// <summary>
    /// Clips a SIXEL band to admitted storage while preserving legacy transparent and undefined-register behavior.
    /// </summary>
    private static void Paint(char c, int count, ref int x, int y, int current, RasterCanvas canvas)
    {
        int width = canvas.Width, height = canvas.Height;
        int end = (int)Math.Min((long)x + count, width);
        if (end <= x || y >= height) return;
        int start = x;
        x = end;
        int bits = c - '?';
        if (bits == 0 || canvas.Palette[current] == 0) return;
        for (int row = 0; row < Math.Min(6, height - y); row++)
            if ((bits & (1 << row)) != 0)
                canvas.Pixels.AsSpan((y + row) * width + start, end - start).Fill((ushort)(current + 1));
    }

    /// <summary>
    /// Applies bounded register selection or RGB/HLS definition with the legacy malformed-field rules.
    /// </summary>
    private static void ReadPalette(ReadOnlySpan<char> data, ref int i, uint[] palette, ref int current)
    {
        int start = i;
        while (i < data.Length && (char.IsDigit(data[i]) || data[i] == ';')) i++;
        ReadOnlySpan<char> command = data[start..i];
        Span<int> values = stackalloc int[5];
        int count = ReadParameters(command, values, out bool valid);
        // A selection accepts any non-five-part command with a valid first register,
        // matching the legacy decoder, even if a later unused field is malformed.
        int firstEnd = data[start..i].IndexOf(';');
        ReadOnlySpan<char> first = firstEnd < 0 ? data[start..i] : data.Slice(start, firstEnd);
        if (!int.TryParse(first, out int index) || index < 0 || index >= palette.Length) return;
        if (count != 5) { current = index; return; }
        if (!valid) return;
        if (values[1] == 2)
            palette[index] = Argb((byte)(Math.Clamp(values[2], 0, 100) * 255 / 100),
                (byte)(Math.Clamp(values[3], 0, 100) * 255 / 100), (byte)(Math.Clamp(values[4], 0, 100) * 255 / 100));
        else if (values[1] == 1)
        {
            var (r, g, b) = SixelDecoder.HlsToRgb(values[2], values[3], values[4]);
            palette[index] = Argb(r, g, b);
        }
    }

    /// <summary>
    /// Counts every field but stores only the fixed prefix, reporting malformed fields without input-sized allocation.
    /// </summary>
    private static int ReadParameters(ReadOnlySpan<char> command, Span<int> values, out bool valid)
    {
        int count = 0;
        valid = true;
        while (true)
        {
            int separator = command.IndexOf(';');
            ReadOnlySpan<char> part = separator < 0 ? command : command[..separator];
            bool parsed = int.TryParse(part, out int value);
            if (count < values.Length) values[count] = value;
            valid &= parsed;
            count++;
            if (separator < 0) break;
            command = command[(separator + 1)..];
        }
        return count;
    }

    private static uint Argb(byte r, byte g, byte b) => 0xff000000u | (uint)r << 16 | (uint)g << 8 | b;
}
