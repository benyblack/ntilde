using System.Text;

namespace Ntilde.VT;

/// <summary>Bounded OSC 5522 framing. Host-owned transactions never enter the screen or replay state.</summary>
public sealed record KittyClipboardPacket(string Type, string Id, bool Primary, string Mime, string Password, string Name, string Payload)
{
    public const int MaxPacketChars = 8192;
    public const int MaxMimeChars = 256;
    public const int MaxTypes = 128;
    public const int MaxDataBytes = 64 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static KittyClipboardPacket? Parse(string data)
    {
        if (data.Length > MaxPacketChars) return null;
        int split = data.IndexOf(';');
        string metadata = split < 0 ? data : data[..split];
        if (metadata.Length > 2048) return null;
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string part in metadata.Split(':'))
        {
            int equals = part.IndexOf('=');
            if (equals <= 0 || !keys.TryAdd(part[..equals], part[(equals + 1)..])) return null;
        }
        string Get(string key) => keys.GetValueOrDefault(key, "");
        if (Get("loc") is not ("" or "clipboard" or "primary")) return null;
        string id = new(Get("id").Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '+' or '.').ToArray());
        return new(Get("type"), id, Get("loc") == "primary", Get("mime"), Get("pw"), Get("name"), split < 0 ? "" : data[(split + 1)..]);
    }

    public static bool IsMime(string value) => value.Length is > 0 and <= MaxMimeChars && value.Contains('/') && value.All(c => c is >= '!' and <= '~');

    public static bool TryDecode(string value, out byte[] bytes)
    {
        bytes = [];
        if (value.Length % 4 != 0 || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '='))) return false;
        try { bytes = Convert.FromBase64String(value); }
        catch (FormatException) { return false; }
        // Reject noncanonical padding and nonzero unused bits too.
        return Convert.ToBase64String(bytes) == value;
    }

    public static bool TryDecodeText(string value, out string text)
    {
        text = "";
        if (!TryDecode(value, out var bytes)) return false;
        try { text = StrictUtf8.GetString(bytes); return true; }
        catch (DecoderFallbackException) { return false; }
    }

    public static string Encode(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
    public static string Response(string type, string status, string id = "", string metadata = "", string? payload = null) =>
        $"\x1b]5522;type={type}:status={status}{(id.Length == 0 ? "" : ":id=" + id)}{metadata}{(payload == null ? "" : ";" + payload)}\x1b\\";
}
