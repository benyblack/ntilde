using System;
using System.Collections.Generic;
using System.Reflection;
using Ntilde.VT;

namespace Ntilde.VT.Tests;

// RAM audit, 2026-10-05: one 4.9 MB iTerm2 image left ~51 MB of large-object garbage behind and
// kept the parser's 8 Mi-char OSC buffer (16.9 MB) alive for as long as the pane lived. The payload
// was copied three times before decoding - List.ToArray, new string, then Split(':', 2) - and the
// accumulation buffers only ever Clear(), which keeps their capacity.
public class ParserStringSequenceMemoryTests
{
    private const int LargeSequenceChars = 1_000_000;

    // Kept generous: the point is that a megabyte-scale buffer is not retained, not where the line is.
    private const int MaxRetainedCapacity = 64 * 1024;

    [Theory]
    [InlineData("\x1b]2;", "\x07", "_oscStringBuffer")]
    [InlineData("\x1bP", "\x1b\\", "_dcsStringBuffer")]
    [InlineData("\x1b_", "\x1b\\", "_apcStringBuffer")]
    public void A_large_string_sequence_does_not_leave_its_buffer_allocated(string introducer, string terminator, string bufferField)
    {
        var parser = new AnsiParser(new TerminalBuffer(80, 24)) { ImageDecoder = new RecordingImageDecoder() };

        parser.Process(introducer + new string('x', LargeSequenceChars) + terminator);

        Assert.True(
            BufferCapacity(parser, bufferField) <= MaxRetainedCapacity,
            $"{bufferField} kept a capacity of {BufferCapacity(parser, bufferField):N0} chars after the sequence ended.");
    }

    [Fact]
    public void A_large_kitty_image_does_not_leave_its_payload_buffer_allocated()
    {
        var decoder = new RecordingImageDecoder();
        // forceConPtyFiltering: false - a parser that believes ConPTY is filtering drops non-tunneled
        // Kitty sequences before they reach the payload buffer, so on Windows this would test nothing.
        var parser = new AnsiParser(new TerminalBuffer(80, 24), forceConPtyFiltering: false) { ImageDecoder = decoder };
        string base64 = Convert.ToBase64String(new byte[LargeSequenceChars * 3 / 4]);

        parser.Process($"\x1b_Ga=T,f=100;{base64}\x1b\\");

        Assert.NotNull(decoder.LastImageBytes);
        var info = typeof(AnsiParser).GetField("_kittyPayloadBuffer", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(info);
        int capacity = ((System.Text.StringBuilder)info!.GetValue(parser)!).Capacity;
        Assert.True(capacity <= MaxRetainedCapacity, $"_kittyPayloadBuffer kept a capacity of {capacity:N0} chars after the image.");
    }

    [Fact]
    public void An_iterm2_image_payload_is_copied_once_before_decoding()
    {
        var decoder = new RecordingImageDecoder();
        var parser = new AnsiParser(new TerminalBuffer(80, 24)) { ImageDecoder = decoder };
        string base64 = Convert.ToBase64String(new byte[LargeSequenceChars * 3 / 4]);
        string sequence = $"\x1b]1337;File=inline=1:{base64}\x07";

        long before = GC.GetAllocatedBytesForCurrentThread();
        parser.Process(sequence);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.NotNull(decoder.LastImageBytes);
        // Unavoidable: the accumulation list's growth (~4 bytes per char across its doublings), one
        // string (2), and the decoded bytes (0.75). Each extra full copy of the payload adds 2 more.
        double bytesPerChar = allocated / (double)base64.Length;
        Assert.True(bytesPerChar < 8.5, $"Processing allocated {bytesPerChar:F2} bytes per payload char.");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(10)]
    public void An_iterm2_image_reaches_the_decoder_byte_for_byte(int length)
    {
        var decoder = new RecordingImageDecoder();
        var parser = new AnsiParser(new TerminalBuffer(80, 24)) { ImageDecoder = decoder };
        byte[] original = new byte[length];
        for (int i = 0; i < length; i++) original[i] = (byte)(i * 37 + 11);

        parser.Process($"\x1b]1337;File=inline=1:{Convert.ToBase64String(original)}\x07");

        Assert.Equal(original, decoder.LastImageBytes);
    }

    [Fact]
    public void An_iterm2_payload_broken_across_lines_still_decodes()
    {
        var decoder = new RecordingImageDecoder();
        var parser = new AnsiParser(new TerminalBuffer(80, 24)) { ImageDecoder = decoder };
        byte[] original = new byte[200];
        for (int i = 0; i < original.Length; i++) original[i] = (byte)i;

        string wrapped = Convert.ToBase64String(original, Base64FormattingOptions.InsertLineBreaks);
        parser.Process($"\x1b]1337;File=inline=1:{wrapped}\x07");

        Assert.Equal(original, decoder.LastImageBytes);
    }

    [Fact]
    public void An_iterm2_payload_that_is_not_base64_is_ignored()
    {
        var decoder = new RecordingImageDecoder();
        var parser = new AnsiParser(new TerminalBuffer(80, 24)) { ImageDecoder = decoder };

        parser.Process("\x1b]1337;File=inline=1:not*base64\x07");

        Assert.Null(decoder.LastImageBytes);
    }

    private static int BufferCapacity(AnsiParser parser, string field)
    {
        var info = typeof(AnsiParser).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(info);
        return ((List<char>)info!.GetValue(parser)!).Capacity;
    }

    private sealed class RecordingImageDecoder : IImageDecoder
    {
        public byte[]? LastImageBytes { get; private set; }

        public object? DecodeImageBytes(byte[] imageData, out int pixelWidth, out int pixelHeight)
        {
            LastImageBytes = imageData;
            pixelWidth = 30;
            pixelHeight = 40;
            return new object();
        }

        public object? DecodeSixel(string sixelData, out int pixelWidth, out int pixelHeight)
        {
            pixelWidth = 0;
            pixelHeight = 0;
            return null;
        }

        public object? DecodeRawImage(byte[] data, int bytesPerPixel, int width, int height, out int pixelWidth, out int pixelHeight)
        {
            pixelWidth = 0;
            pixelHeight = 0;
            return null;
        }
    }
}
