using System.Text;
using Ntilde.Replay;

namespace Ntilde.VT.Tests;

public sealed class Latin1ShiftTests
{
    [Theory]
    [InlineData("\x1b)A\x1b~")]
    [InlineData("\x1b*A\x1b}")]
    [InlineData("\x1b+A\x1b|")]
    [InlineData("\x1b-A\x1b~")]
    [InlineData("\x1b.A\x1b}")]
    [InlineData("\x1b/A\x1b|")]
    public void Right_locking_shifts_preserve_raw_Latin1_and_valid_UTF8(string setup)
    {
        var buffer = new TerminalBuffer(20, 3);
        var parser = new AnsiParser(buffer);
        parser.ProcessBytes(Encoding.ASCII.GetBytes(setup));
        parser.ProcessBytes([0xa1, 0xa3, 0xe9, 0xff]);
        parser.ProcessBytes("€é"u8);
        Assert.Equal("¡£éÿ€é", Line(buffer));
    }

    [Theory]
    [InlineData("\x1b*A\x1bn")]
    [InlineData("\x1b+A\x1bo")]
    [InlineData("\x1b.A\x1bn")]
    [InlineData("\x1b/A\x1bo")]
    public void Left_locking_shifts_use_the_full_Latin1_range_until_shift_in(string setup)
    {
        var buffer = new TerminalBuffer(16, 3);
        new AnsiParser(buffer).Process(setup + "!\x7f#\x0f#");
        Assert.Equal("¡ÿ£#", Line(buffer));
    }

    [Theory]
    [InlineData("\x1b*A", 0x8e)]
    [InlineData("\x1b+A", 0x8f)]
    public void Single_shifts_repeatably_map_one_GL_character_and_preserve_intervening_SGR(string designation, byte c1)
    {
        var buffer = new TerminalBuffer(28, 3);
        var parser = new AnsiParser(buffer);
        parser.ProcessBytes(Encoding.ASCII.GetBytes(designation));
        parser.ProcessBytes([c1, 0x1b]);
        parser.ProcessBytes("[1m#q"u8);
        parser.ProcessBytes(c1 == 0x8e ? "\x1bN\x7fZ"u8 : "\x1bO\x7fZ"u8);
        Assert.Equal("£qÿZ", Line(buffer));
    }

    [Fact]
    public void Invoking_different_designations_proves_G2_and_G3_are_distinct()
    {
        var buffer = new TerminalBuffer(30, 3);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b*0\x1b+A\x1bnq\x0f\x1bNqq\x1bO##\x1b[?42h\x1bO#q");
        parser.ProcessBytes("\x1b}"u8);
        parser.ProcessBytes([0xf1, 0xf8]);
        Assert.Equal("──q£#£q─│", Line(buffer));
    }

    [Fact]
    public void Explicit_coding_selection_resolves_ambiguous_bytes_and_RIS_restores_automatic_mode()
    {
        var buffer = new TerminalBuffer(30, 3);
        var parser = new AnsiParser(buffer);
        parser.ProcessBytes("\x1b%@\x1b-A\x1b~"u8);
        parser.ProcessBytes([0xc2, 0xa3, 0xff]);
        parser.ProcessBytes("\x1b%G£"u8);
        parser.ProcessBytes([0xa3]);
        Assert.Equal("Â£ÿ£�", Line(buffer));
        parser.ProcessBytes("\u001bc"u8);
        parser.ProcessBytes("£"u8);
        parser.ProcessBytes([0xff]);
        Assert.Equal("£�", Line(buffer));
    }

    [Fact]
    [Trait("Category", "Replay")]
    public void Every_split_restores_charset_shift_and_pending_raw_output_at_the_same_cut()
    {
        byte[] bytes = [.. "\x1b.A\x1b}\x1b+A\x1bO\x1b[1m#Q"u8, 0xa3, .. "é€😀\x1bN#B"u8];
        for (int cut = 0; cut <= bytes.Length; cut++)
        {
            var buffer = new TerminalBuffer(40, 3);
            var parser = new AnsiParser(buffer);
            parser.ProcessBytes(bytes.AsSpan(0, cut));

            var snapshot = buffer.ExportState(0);
            snapshot.Parser = parser.ExportState();
            snapshot.DecoderTail = parser.PendingOutputBytes.ToArray();
            var restored = new TerminalBuffer(40, 3);
            var target = new AnsiParser(restored);
            TerminalStateTransfer.Restore(restored, target,
                TerminalStateSerializer.FromBytes(TerminalStateSerializer.ToBytes(snapshot)));
            target.ProcessBytes(bytes.AsSpan(cut));
            Assert.Equal("£Q£é€😀£B", Line(restored));
        }
    }

    [Fact]
    public void Saved_cursor_restores_the_right_set_and_one_shot_shift()
    {
        var buffer = new TerminalBuffer(20, 3);
        var parser = new AnsiParser(buffer);
        parser.ProcessBytes("\x1b-A\x1b~\x1b*0\x1bN\u001b7\x1b+0\x1b|\x1bOQ\u001b8q"u8);
        parser.ProcessBytes([0xf1, (byte)'X']);
        Assert.Equal("─ñX", Line(buffer));
    }

    [Fact]
    public void Default_byte_parser_matches_UTF8_replacement_policy_across_chunk_boundaries()
    {
        byte[] bytes = [.. "é€😀"u8, 0xe2, 0x82, (byte)'a', 0xc0, 0xaf, 0xf4, 0x90, (byte)'Z'];
        string unicode = Encoding.UTF8.GetString(bytes);
        var reference = new TerminalBuffer(32, 3);
        new AnsiParser(reference).Process(unicode);
        for (int cut = 0; cut <= bytes.Length; cut++)
        {
            var buffer = new TerminalBuffer(32, 3);
            var parser = new AnsiParser(buffer);
            parser.ProcessBytes(bytes.AsSpan(0, cut));
            parser.ProcessBytes(bytes.AsSpan(cut));
            Assert.Equal(Line(reference), Line(buffer));
            Assert.True(parser.PendingOutputBytes.IsEmpty);
        }
    }

    private static string Line(TerminalBuffer buffer) => BufferSnapshot.Capture(buffer).Lines[0].TrimEnd();
}
