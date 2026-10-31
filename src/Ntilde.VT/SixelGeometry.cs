namespace Ntilde.VT;

/// <summary>An immutable pair, so host updates and snapshot capture cannot observe half a cell size.</summary>
public sealed record SixelCellSize(float WidthPx, float HeightPx);

/// <summary>Allocation-free SIXEL bounds for layout when a host has no bitmap decoder.</summary>
public static class SixelGeometry
{
    public const int DefaultMaxPixelDimension = 2000;

    /// <summary>Measures the painted extent using the same clipping and raster-size policy as the bitmap decoder.</summary>
    public static (int Width, int Height) Measure(ReadOnlySpan<char> dcs, int maxPixelDimension = DefaultMaxPixelDimension)
    {
        int q = dcs.IndexOf('q');
        int limit = Math.Max(0, maxPixelDimension);
        if (q < 0 || limit == 0) return (0, 0);
        int x = 0, y = 0, width = 0, height = 0;
        for (int i = q + 1; i < dcs.Length;)
        {
            char c = dcs[i++];
            int count = 1;
            switch (c)
            {
                case '#':
                case '"':
                    SkipParameters(dcs, ref i);
                    continue;
                case '$': x = 0; continue;
                case '-': x = 0; y = (int)Math.Min((long)y + 6, limit); continue;
                case '!': count = ReadRepeat(dcs, ref i, out c); break;
            }
            if (c is < '?' or > '~' || y >= limit || count <= 0) continue;
            int end = (int)Math.Min((long)x + count, limit);
            if (end <= x) continue;
            x = end;
            width = Math.Max(width, x);
            height = Math.Max(height, (int)Math.Min((long)y + 6, limit));
        }
        return (width, height);
    }

    private static void SkipParameters(ReadOnlySpan<char> data, ref int i)
    {
        // Palette/raster declarations never allocate or resize the decoded image.
        while (i < data.Length && (char.IsDigit(data[i]) || data[i] == ';')) i++;
    }

    private static int ReadRepeat(ReadOnlySpan<char> data, ref int i, out char sixel)
    {
        int start = i;
        long parsed = 0;
        while (i < data.Length && char.IsAsciiDigit(data[i]))
            parsed = Math.Min(parsed * 10 + data[i++] - '0', int.MaxValue);
        sixel = '\0';
        if (i == start || i == data.Length || data[i] is < '?' or > '~') return 0;
        sixel = data[i++];
        return (int)parsed;
    }
}
