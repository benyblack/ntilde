using System;
using System.Collections.Generic;
using Xunit;

namespace Ntilde.VT.Tests;

public sealed class KittyBase64PayloadTests
{
    public static IEnumerable<object[]> RawCases()
    {
        foreach (int first in new[] { 0x18, 0x1b, 0xff })
            foreach (int format in new[] { 24, 32 })
                foreach (bool tunneled in new[] { false, true })
                    foreach (bool chunked in new[] { false, true })
                        yield return new object[] { first, format, tunneled, chunked };
    }

    [Theory]
    [MemberData(nameof(RawCases))]
    public void Raw_RGB_and_RGBA_preserve_the_complete_base64_payload(int first, int format, bool tunneled, bool chunked)
    {
        byte[] bytes = format == 24 ? [(byte)first, 0x12, 0xab] : [(byte)first, 0x12, 0xab, 0xff];
        string payload = Convert.ToBase64String(bytes);
        Assert.Equal(first == 0xff ? '/' : 'G', payload[0]);
        var decoder = new CapturingDecoder();
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { ImageDecoder = decoder };
        var replies = new List<string>();
        parser.OnResponse = replies.Add;
        string controls = $"a=T,f={format},s=1,v=1,i=42,C=1";
        if (chunked)
        {
            parser.Process(Command(controls + ",m=1", payload[..4]));
            Assert.Null(decoder.Raw);
            Assert.Empty(buffer.Images);
            Assert.Empty(replies);
            parser.Process(Command("m=0", payload[4..]));
        }
        else parser.Process(Command(controls, payload));
        Assert.Equal(bytes, decoder.Raw);
        Assert.Equal(format / 8, decoder.BytesPerPixel);
        Assert.Equal("\x1b_Gi=42;OK\x1b\\", Assert.Single(replies));
        Assert.Equal(42u, Assert.Single(buffer.Images).KittyImageId);
        Assert.Equal(0, buffer.CursorCol);
        Assert.Equal(0, buffer.CursorRow);
        parser.Process(Command("a=d,d=I,i=42", ""));
        Assert.Empty(buffer.Images);
        Assert.Single(replies);

        string Command(string parameters, string data) => tunneled
            ? $"\x1b]1339;Kitty:G{parameters};{data}\x1b\\"
            : $"\x1b_G{parameters};{data}\x1b\\";
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_spurious_G_inside_the_payload_is_not_a_second_command_prefix(bool tunneled)
    {
        var decoder = new CapturingDecoder();
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { ImageDecoder = decoder };
        var replies = new List<string>(); parser.OnResponse = replies.Add;
        const string body = "a=T,f=32,s=1,v=1,i=42,C=1;G/wAA/w==";
        parser.Process(tunneled ? "\x1b]1339;Kitty:G" + body + "\x1b\\" : "\x1b_G" + body + "\x1b\\");
        Assert.Null(decoder.Raw);
        Assert.Empty(buffer.Images);
        Assert.Contains("EINVAL", Assert.Single(replies));
    }

    private sealed class CapturingDecoder : IImageDecoder
    {
        public byte[]? Raw { get; private set; }
        public int BytesPerPixel { get; private set; }
        public object? DecodeRawImage(byte[] data, int bytesPerPixel, int width, int height, out int pixelWidth, out int pixelHeight)
        {
            Raw = data; BytesPerPixel = bytesPerPixel;
            pixelWidth = width; pixelHeight = height;
            return new object();
        }
        public object? DecodeImageBytes(byte[] data, out int width, out int height) { width = height = 0; return null; }
        public object? DecodeSixel(string data, out int width, out int height) { width = height = 0; return null; }
    }
}
