using System.Runtime.InteropServices;

namespace Ntilde.Shell.Clipboard;

/// <summary>macOS exposes UTIs; other platforms can carry MIME-named custom byte formats.</summary>
internal static class ClipboardMimeNames
{
    public static string ToPlatform(string mime) => OperatingSystem.IsMacOS() ? ConvertMac(mime, toMime: false) ?? mime : mime;
    public static string? ToMime(string platform) => platform.Contains('/') ? platform : OperatingSystem.IsMacOS() ? ConvertMac(platform, toMime: true) : null;

    private static string? ConvertMac(string value, bool toMime)
    {
        const uint utf8 = 0x08000100;
        nint tagClass = CFStringCreateWithCString(0, System.Text.Encoding.UTF8.GetBytes("public.mime-type\0"), utf8);
        nint input = CFStringCreateWithCString(0, System.Text.Encoding.UTF8.GetBytes(value + "\0"), utf8);
        nint output = 0;
        try
        {
            output = toMime ? UTTypeCopyPreferredTagWithClass(input, tagClass) : UTTypeCreatePreferredIdentifierForTag(tagClass, input, 0);
            if (output == 0) return null;
            var bytes = new byte[2048];
            if (!CFStringGetCString(output, bytes, bytes.Length, utf8)) return null;
            int length = Array.IndexOf(bytes, (byte)0);
            return length < 0 ? null : System.Text.Encoding.UTF8.GetString(bytes, 0, length);
        }
        finally
        {
            if (output != 0) CFRelease(output);
            if (input != 0) CFRelease(input);
            if (tagClass != 0) CFRelease(tagClass);
        }
    }

    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string CoreServices = "/System/Library/Frameworks/CoreServices.framework/CoreServices";
    [DllImport(CoreFoundation)] private static extern nint CFStringCreateWithCString(nint allocator, byte[] text, uint encoding);
    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFStringGetCString(nint value, byte[] buffer, nint size, uint encoding);
    [DllImport(CoreFoundation)] private static extern void CFRelease(nint value);
    [DllImport(CoreServices)] private static extern nint UTTypeCopyPreferredTagWithClass(nint uti, nint tagClass);
    [DllImport(CoreServices)] private static extern nint UTTypeCreatePreferredIdentifierForTag(nint tagClass, nint tag, nint conformsTo);
}
