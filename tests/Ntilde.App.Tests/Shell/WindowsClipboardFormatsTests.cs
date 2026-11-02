using System.Text;
using Ntilde.Shell.Clipboard;

namespace Ntilde.Tests.Shell;

public class WindowsClipboardFormatsTests
{
    [Fact]
    public void Header_offsets_beyond_4096_bytes_are_not_truncated()
    {
        string template = "Version:1.0\r\nStartHTML:{0:D6000}\r\nEndHTML:{1:D6000}\r\nStartFragment:{0:D6000}\r\nEndFragment:{1:D6000}\r\n";
        var format = System.Text.CompositeFormat.Parse(template);
        string empty = string.Format(System.Globalization.CultureInfo.InvariantCulture, format, 0, 0);
        byte[] fragment = Encoding.UTF8.GetBytes("<b>long native header</b>");
        string header = string.Format(System.Globalization.CultureInfo.InvariantCulture, format, empty.Length, empty.Length + fragment.Length);
        Assert.Equal(fragment, WindowsClipboardFormats.DecodeHtml(Encoding.ASCII.GetBytes(header).Concat(fragment).ToArray(), fragment.Length));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\r\n")]
    public void Contextless_HTML_supports_native_line_endings_and_space_padded_offsets(string newline)
    {
        byte[] fragment = Encoding.UTF8.GetBytes("<i>héllo</i>");
        string template = "Version:1.0" + newline + "StartHTML:-1" + newline + "EndHTML:-1" + newline +
            "StartFragment: {0:D10}" + newline + "EndFragment: {1:D10}" + newline;
        string empty = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, 0, 0);
        string header = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, empty.Length, empty.Length + fragment.Length);
        byte[] bytes = Encoding.UTF8.GetBytes(header).Concat(fragment).ToArray();
        Assert.Equal(fragment, WindowsClipboardFormats.DecodeHtml(bytes, fragment.Length));
    }

    [Fact]
    public void Html_offsets_are_UTF8_byte_offsets_and_native_names_round_trip()
    {
        byte[] fragment = Encoding.UTF8.GetBytes("<b>héllo 🧪</b>");
        byte[] encoded = WindowsClipboardFormats.EncodeHtml(fragment);
        Assert.Equal(fragment, WindowsClipboardFormats.DecodeHtml(encoded, fragment.Length));
        Assert.Throws<IOException>(() => WindowsClipboardFormats.DecodeHtml(encoded, fragment.Length - 1));
        Assert.Contains("<!--StartFragment--><b>héllo 🧪</b><!--EndFragment-->", Encoding.UTF8.GetString(encoded));
        Assert.Equal("PNG", WindowsClipboardFormats.ToNative("image/png"));
        Assert.Equal("HTML Format", WindowsClipboardFormats.ToNative("text/html"));
        Assert.Equal("image/png", WindowsClipboardFormats.ToMime("png"));
        Assert.Equal("text/html", WindowsClipboardFormats.ToMime("HTML Format"));
        Assert.Equal("application/x-test", WindowsClipboardFormats.ToNative("application/x-test"));
    }

    [Theory]
    [InlineData("StartHTML:-1\r\nEndHTML:-1\r\nStartFragment:0000001000\r\nEndFragment:0000001001\r\n")]
    [InlineData("StartHTML:-1\r\nEndHTML:-1\r\nStartFragment:0000000020\r\nEndFragment:0000000010\r\n")]
    [InlineData("StartHTML:-1\r\nEndHTML:-1\r\nStartFragment:-1\r\nEndFragment:0000000010\r\n")]
    [InlineData("StartHTML:-1\r\nEndHTML:-1\r\nStartFragment:99999999999999\r\nEndFragment:99999999999999\r\n")]
    [InlineData("<b>missing header</b>")]
    public void Malformed_native_HTML_never_returns_an_arbitrary_slice(string input)
    {
        Assert.Throws<InvalidDataException>(() => WindowsClipboardFormats.DecodeHtml(Encoding.UTF8.GetBytes(input), 1024));
    }
}
