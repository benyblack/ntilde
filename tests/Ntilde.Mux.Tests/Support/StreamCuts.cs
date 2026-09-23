namespace Ntilde.Mux.Tests.Support;

internal static class StreamCuts
{
    /// <summary>
    /// Cut points that stress an attach: one byte into the first escape sequence, at the first
    /// UTF-8 continuation byte (mid code point, so the decoder holds a tail), and the midpoint.
    /// </summary>
    public static IReadOnlyList<int> Interesting(byte[] bytes)
    {
        var cuts = new SortedSet<int>();
        int esc = Array.IndexOf(bytes, (byte)0x1B);
        if (esc >= 0 && esc + 1 < bytes.Length) cuts.Add(esc + 1);
        for (int i = 1; i < bytes.Length; i++)
        {
            if ((bytes[i] & 0xC0) == 0x80) { cuts.Add(i); break; }
        }

        if (bytes.Length > 1) cuts.Add(bytes.Length / 2);
        return cuts.ToArray();
    }
}
