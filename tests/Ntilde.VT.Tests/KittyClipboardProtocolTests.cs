using System.Text.Json;

namespace Ntilde.VT.Tests;

public class KittyClipboardProtocolTests
{
    [Fact]
    public void Every_input_split_preserves_packet_and_mode_state()
    {
        const string sequence = "\x1b[?5522h\x1b]5522;type=read:id=a;Lg==\x1b\\\x1b[?5522$p";
        for (int split = 0; split <= sequence.Length; split++)
        {
            var buffer = new TerminalBuffer(20, 4);
            var parser = new AnsiParser(buffer) { KittyClipboardEnabled = true };
            var packets = new List<string>();
            var replies = new List<string>();
            parser.OnKittyClipboardPacket = packets.Add;
            parser.OnResponse = replies.Add;
            parser.Process(sequence[..split]);
            parser.Process(sequence[split..]);
            Assert.Equal("type=read:id=a;Lg==", Assert.Single(packets));
            Assert.Equal("\x1b[?5522;1$y", Assert.Single(replies));
            Assert.True(buffer.Modes.IsClipboardPasteMode);
            Assert.Equal(0, buffer.CursorCol);
        }
    }

    [Fact]
    public void DEC_reset_clears_MIME_paste_mode_at_every_input_split()
    {
        const string sequence = "\x1b[?5522h\x1b[?5522l\x1b[?5522$p";
        for (int split = 0; split <= sequence.Length; split++)
        {
            var buffer = new TerminalBuffer(20, 4);
            var parser = new AnsiParser(buffer) { KittyClipboardEnabled = true };
            var replies = new List<string>();
            parser.OnResponse = replies.Add;
            parser.Process(sequence[..split]); parser.Process(sequence[split..]);
            Assert.False(buffer.Modes.IsClipboardPasteMode);
            Assert.Equal("\x1b[?5522;2$y", Assert.Single(replies));
        }
    }

    [Fact]
    public void Headless_host_does_not_advertise_or_enable_desktop_clipboard()
    {
        var buffer = new TerminalBuffer(20, 4);
        var parser = new AnsiParser(buffer);
        var replies = new List<string>();
        parser.OnResponse = replies.Add;
        parser.Process("\x1b[?5522h\x1b[?5522$p");
        Assert.False(buffer.Modes.IsClipboardPasteMode);
        Assert.Equal("\x1b[?5522;0$y", Assert.Single(replies));
    }

    [Fact]
    public void Mode_survives_complete_state_and_clone_but_RIS_and_prompt_clear_it()
    {
        var buffer = new TerminalBuffer(20, 4);
        var parser = new AnsiParser(buffer) { KittyClipboardEnabled = true };
        parser.Process("\x1b[?5522h");
        parser.Process("\x1b[!p");
        Assert.True(buffer.Modes.Clone().IsClipboardPasteMode);
        var other = new TerminalBuffer(20, 4);
        other.ImportState(JsonSerializer.Deserialize<TerminalStateSnapshot>(JsonSerializer.Serialize(buffer.ExportState(0)))!);
        Assert.True(other.Modes.IsClipboardPasteMode);
        other.Modes.ResetTransientInputReporting();
        Assert.False(other.Modes.IsClipboardPasteMode);
        int resets = 0;
        parser.OnKittyClipboardReset = () => resets++;
        parser.Process("\u001bc");
        Assert.False(buffer.Modes.IsClipboardPasteMode);
        Assert.Equal(1, resets);
    }

    [Theory]
    [InlineData("Zg", false)]
    [InlineData("Zg==\n", false)]
    [InlineData("Zg=", false)]
    [InlineData("Zh==", false)]
    [InlineData("Zm9=", false)]
    [InlineData("Zg==", true)]
    [InlineData("", true)]
    [InlineData("AAEC/w==", true)]
    public void Base64_requires_standard_alphabet_padding_and_canonical_bits(string encoded, bool valid)
        => Assert.Equal(valid, KittyClipboardPacket.TryDecode(encoded, out _));

    [Fact]
    public void Metadata_is_bounded_and_id_is_safe_to_echo()
    {
        var packet = KittyClipboardPacket.Parse("type=read:id=a\r\nb$!+._-;Lg==");
        Assert.Equal("ab+._-", packet!.Id);
        Assert.Null(KittyClipboardPacket.Parse("type=read:type=write;Lg=="));
        Assert.Null(KittyClipboardPacket.Parse("type=read:loc=other;Lg=="));
        Assert.Null(KittyClipboardPacket.Parse("type=read;" + new string('A', 8192)));
    }
}
