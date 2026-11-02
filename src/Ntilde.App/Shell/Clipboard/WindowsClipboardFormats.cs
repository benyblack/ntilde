using System.Globalization;
using System.Text;

namespace Ntilde.Shell.Clipboard;

/// <summary>Win32's registered PNG and CF_HTML formats, independent of the UI/OS API.</summary>
internal static class WindowsClipboardFormats
{
    internal const string Html = "HTML Format";
    internal const string Png = "PNG";
    private const string Header = "Version:1.0\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
    private static readonly CompositeFormat HeaderFormat = CompositeFormat.Parse(Header);
    private const string Prefix = "<html><body><!--StartFragment-->";
    private const string Suffix = "<!--EndFragment--></body></html>";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string ToNative(string mime) => mime switch { "text/html" => Html, "image/png" => Png, _ => mime };
    public static string? ToMime(string format)
    {
        if (format.Equals(Html, StringComparison.OrdinalIgnoreCase)) return "text/html";
        return format.Equals(Png, StringComparison.OrdinalIgnoreCase) ? "image/png" : null;
    }

    public static byte[] EncodeHtml(byte[] fragment)
    {
        _ = StrictUtf8.GetCharCount(fragment);
        int start = string.Format(CultureInfo.InvariantCulture, HeaderFormat, 0, 0, 0, 0).Length;
        int end = checked(start + Prefix.Length + fragment.Length + Suffix.Length);
        string header = string.Format(CultureInfo.InvariantCulture, HeaderFormat, start, end, start + Prefix.Length, end - Suffix.Length);
        byte[] output = new byte[end];
        Encoding.ASCII.GetBytes(header + Prefix).CopyTo(output, 0);
        fragment.CopyTo(output, start + Prefix.Length);
        Encoding.ASCII.GetBytes(Suffix).CopyTo(output, end - Suffix.Length);
        return output;
    }

    public static byte[] DecodeHtml(byte[] data, int maxBytes)
    {
        if (data.Length > 2L * Ntilde.VT.KittyClipboardPacket.MaxDataBytes)
            throw new ClipboardSizeLimitException("Native HTML exceeds the encoded representation budget.");
        var start = Offset(data, "StartFragment:"u8);
        var end = Offset(data, "EndFragment:"u8);
        var htmlStart = Offset(data, "StartHTML:"u8);
        var htmlEnd = Offset(data, "EndHTML:"u8);
        int headerEnd = Math.Max(Math.Max(start.End, end.End), Math.Max(htmlStart.End, htmlEnd.End));
        bool noContext = htmlStart.Value == -1 && htmlEnd.Value == -1;
        if (start.Value < headerEnd || end.Value < start.Value || end.Value > data.Length ||
            (!noContext && (htmlStart.Value < headerEnd || htmlStart.Value > start.Value || htmlEnd.Value < end.Value || htmlEnd.Value > data.Length)))
            throw new InvalidDataException("Invalid CF_HTML fragment offsets.");
        if (end.Value - start.Value > maxBytes) throw new IOException("Clipboard HTML exceeds the remaining read budget.");
        ReadOnlySpan<byte> fragment = data.AsSpan(start.Value, end.Value - start.Value);
        _ = StrictUtf8.GetCharCount(fragment);
        return fragment.ToArray();
    }

    private static (int Value, int End) Offset(byte[] data, ReadOnlySpan<byte> key)
    {
        ReadOnlySpan<byte> header = data;
        int field = header.IndexOf(key);
        if (field < 0) return (-2, 0);
        ReadOnlySpan<byte> tail = header[(field + key.Length)..];
        int length = tail.IndexOfAny((byte)'\r', (byte)'\n');
        if (length < 0) return (-2, 0);
        int lineEnd = field + key.Length + length + 1;
        if (lineEnd < header.Length && header[lineEnd - 1] == (byte)'\r' && header[lineEnd] == (byte)'\n') lineEnd++;
        return int.TryParse(tail[..length], NumberStyles.Integer, CultureInfo.InvariantCulture, out int offset)
            ? (offset, lineEnd) : (-2, 0);
    }
}
