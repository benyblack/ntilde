using System.Text;

namespace Ntilde.VT;

public partial class AnsiParser
{
    /// <summary>
    /// Encodes a parser-generated reply with raw C1 framing when S8C1T is active.
    /// Returns null when the host should write the original reply as UTF-8.
    /// </summary>
    public byte[]? Encode8BitReply(string reply)
    {
        if (!_send8BitControls || reply.Length < 2 || reply[0] != '\x1b') return null;

        byte introducer = reply[1] switch
        {
            '[' => 0x9b, // CSI
            'P' => 0x90, // DCS
            ']' => 0x9d, // OSC
            '_' => 0x9f, // APC
            _ => 0,
        };
        if (introducer == 0) return null;

        bool hasSt = introducer != 0x9b && reply.AsSpan(2).EndsWith("\x1b\\", StringComparison.Ordinal);
        ReadOnlySpan<char> payload = reply.AsSpan(2, reply.Length - 2 - (hasSt ? 2 : 0));
        var bytes = new byte[1 + Encoding.UTF8.GetByteCount(payload) + (hasSt ? 1 : 0)];
        bytes[0] = introducer;
        Encoding.UTF8.GetBytes(payload, bytes.AsSpan(1));
        if (hasSt) bytes[^1] = 0x9c;
        return bytes;
    }
}
