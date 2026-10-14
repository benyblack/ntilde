using System.Text;

namespace Ntilde.Mux.TextClient;

/// <summary>
/// UTF-8 bytes read from a terminal into chars, carrying a sequence split across reads (spec §6.1).
/// Split out of <see cref="UnixConsoleSurface"/> so the bounds are testable without a TTY.
/// </summary>
internal sealed class TerminalInputDecoder
{
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();

    /// <summary>
    /// How many bytes one read may take for a char buffer of <paramref name="charCapacity"/>. UTF-8
    /// never decodes to more chars than bytes, except that up to 3 bytes carried over from the last
    /// read can complete as a surrogate pair: 2 chars are kept spare so that never overflows.
    /// </summary>
    public static int MaxBytes(int charCapacity, int scratchBytes) => Math.Max(1, Math.Min(scratchBytes, charCapacity - 2));

    /// <summary>0 when every byte belongs to a sequence that completes on a later read.</summary>
    public int Decode(byte[] bytes, int count, char[] buffer) => _decoder.GetChars(bytes, 0, count, buffer, 0, flush: false);
}
