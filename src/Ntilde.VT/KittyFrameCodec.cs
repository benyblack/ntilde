using System;
using System.Collections.Generic;

namespace Ntilde.VT;

/// <summary>
/// The retained form of a Kitty animation frame in a persistent session (#1017): RGBA packed as runs of whole pixels. It is deliberately not zlib:
/// the packed length is charged to the shared image-source budget, and the daemon and every client must account the same bytes for the same frame,
/// which a runtime's compressor does not promise across versions. A control byte below 128 starts a literal of <c>control + 1</c> pixels
/// (up to 128); one from 128 up repeats the next pixel <c>control - 128 + 2</c> times (2 to 129). Solid and flat frames shrink to a few bytes; a frame
/// with no repeats costs one extra byte per 128 pixels.
/// </summary>
internal static class KittyFrameCodec
{
    private const int MaxLiteral = 128, MinRun = 2, MaxRun = 129;

    /// <summary>The packed form of <paramref name="rgba"/> (a whole number of 4-byte pixels).</summary>
    internal static byte[] Encode(ReadOnlySpan<byte> rgba)
    {
        if (rgba.Length % 4 != 0) throw new ArgumentException("Pixels are four bytes each.", nameof(rgba));
        var output = new List<byte>(Math.Min(rgba.Length, 1 << 20));
        int pixels = rgba.Length / 4, i = 0;
        while (i < pixels)
        {
            int run = RunLength(rgba, i, pixels);
            if (run >= MinRun)
            {
                output.Add((byte)(128 + run - MinRun));
                output.AddRange(rgba.Slice(i * 4, 4).ToArray());
                i += run;
                continue;
            }
            int start = i;
            i++;
            while (i < pixels && i - start < MaxLiteral && RunLength(rgba, i, pixels) < MinRun) i++;
            output.Add((byte)(i - start - 1));
            output.AddRange(rgba.Slice(start * 4, (i - start) * 4).ToArray());
        }
        return output.ToArray();
    }

    private static int RunLength(ReadOnlySpan<byte> rgba, int index, int pixels)
    {
        ReadOnlySpan<byte> first = rgba.Slice(index * 4, 4);
        int length = 1;
        while (index + length < pixels && length < MaxRun && rgba.Slice((index + length) * 4, 4).SequenceEqual(first)) length++;
        return length;
    }

    /// <summary>Whether <paramref name="packed"/> is a well-formed packing of exactly <paramref name="pixelCount"/> pixels, checked without building them.</summary>
    internal static bool IsValid(ReadOnlySpan<byte> packed, int pixelCount)
    {
        if (pixelCount <= 0) return false;
        int at = 0;
        long written = 0;
        while (at < packed.Length)
        {
            int control = packed[at++];
            int count = control < 128 ? control + 1 : control - 128 + MinRun, bytes = control < 128 ? count * 4 : 4;
            if (written + count > pixelCount || at + bytes > packed.Length) return false;
            at += bytes;
            written += count;
        }
        return written == pixelCount;
    }

    /// <summary>True with exactly <paramref name="pixelCount"/> pixels when <paramref name="packed"/> is a well-formed packing of that many; never reads or writes past it.</summary>
    internal static bool TryDecode(ReadOnlySpan<byte> packed, int pixelCount, out byte[] rgba)
    {
        rgba = [];
        if (pixelCount <= 0 || (long)pixelCount * 4 > int.MaxValue) return false;
        var output = new byte[pixelCount * 4];
        int at = 0, written = 0;
        while (at < packed.Length)
        {
            int control = packed[at++];
            if (control < 128)
            {
                int count = control + 1;
                if (written + count > pixelCount || at + count * 4 > packed.Length) return false;
                packed.Slice(at, count * 4).CopyTo(output.AsSpan(written * 4));
                at += count * 4;
                written += count;
                continue;
            }
            int repeat = control - 128 + MinRun;
            if (written + repeat > pixelCount || at + 4 > packed.Length) return false;
            for (int k = 0; k < repeat; k++) packed.Slice(at, 4).CopyTo(output.AsSpan((written + k) * 4));
            at += 4;
            written += repeat;
        }
        if (written != pixelCount) return false;
        rgba = output;
        return true;
    }
}
