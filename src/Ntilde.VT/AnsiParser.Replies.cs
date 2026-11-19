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

        ReadOnlySpan<char> body = reply.AsSpan(2);
        int stLength = introducer == 0x9b ? 0 : body switch
        {
            [.., '\u009c'] => 1,
            [.., '\x1b', '\\'] => 2,
            _ => 0,
        };
        ReadOnlySpan<char> payload = body[..(body.Length - stLength)];
        var bytes = new byte[1 + Encoding.UTF8.GetByteCount(payload) + (stLength > 0 ? 1 : 0)];
        bytes[0] = introducer;
        Encoding.UTF8.GetBytes(payload, bytes.AsSpan(1));
        if (stLength > 0) bytes[^1] = 0x9c;
        return bytes;
    }
}
