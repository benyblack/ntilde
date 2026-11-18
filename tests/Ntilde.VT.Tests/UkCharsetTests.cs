using Ntilde.Replay;

namespace Ntilde.VT.Tests;

/// <summary>SCS designator <c>A</c>: the VT100 UK set, ASCII with the pound sign at 2/3.</summary>
public sealed class UkCharsetTests
{
    private const string Esc = "\u001b";
    private const int Ascii = 0, Graphics = 1, Uk = 2;

    private static readonly string Printable = new(Enumerable.Range(0x20, 0x5F).Select(code => (char)code).ToArray());

    private static (TerminalBuffer Buffer, AnsiParser Parser) Create(int cols = 100, int rows = 4)
    {
        var buffer = new TerminalBuffer(cols, rows);
        return (buffer, new AnsiParser(buffer));
    }

    private static string Line(TerminalBuffer buffer, int row = 0) => BufferSnapshot.Capture(buffer).Lines[row].TrimEnd();

    [Fact]
    public void Designated_into_G0_only_the_number_sign_changes()
    {
        var (buffer, parser) = Create();
        parser.Process($"{Esc}(A{Printable}");
        Assert.Equal(Printable.Replace('#', '£').TrimEnd(), Line(buffer));
    }

    [Fact]
    public void Designated_into_G1_it_applies_only_while_shifted_out()
    {
        var (buffer, parser) = Create();
        parser.Process($"{Esc})A#a\u000e#a\u000f#a");
        Assert.Equal("#a£a#a", Line(buffer));
    }

    [Theory]
    [InlineData("(", 0)]
    [InlineData(")", 1)]
    [InlineData("*", 2)]
    [InlineData("+", 3)]
    public void Each_94_character_introducer_designates_its_own_slot(string introducer, int slot)
    {
        var (_, parser) = Create();
        parser.Process($"{Esc}{introducer}A");
        int[] expected = [Ascii, Ascii, Ascii, Ascii];
        expected[slot] = Uk;
        Assert.Equal(expected, parser.ExportState().Charsets);
    }

    [Fact]
    public void Redesignating_ASCII_restores_the_number_sign()
    {
        var (buffer, parser) = Create();
        parser.Process($"{Esc}(A#{Esc}(B#");
        Assert.Equal("£#", Line(buffer));
        Assert.Equal(new[] { Ascii, Ascii, Ascii, Ascii }, parser.ExportState().Charsets);
    }

    [Theory]
    [InlineData("\u001bc")]   // RIS
    [InlineData("\u001b[!p")] // DECSTR
    public void A_reset_restores_ASCII(string reset)
    {
        var (buffer, parser) = Create();
        parser.Process($"{Esc}(A{Esc})A\u000e{reset}#");
        Assert.Equal(new[] { Ascii, Ascii, Ascii, Ascii }, parser.ExportState().Charsets);
        Assert.Equal(0, parser.ExportState().Gl);
        Assert.Contains("#", Line(buffer));
        Assert.DoesNotContain("£", Line(buffer));
    }

    [Fact]
    public void The_alternate_ROM_designators_follow_xterm()
    {
        var (buffer, parser) = Create();
        parser.Process($"{Esc}(1#q{Esc}(2#q");
        Assert.Equal("#q#─", Line(buffer));
        Assert.Equal(Graphics, parser.ExportState().Charsets[0]);
    }

    [Fact]
    public void The_96_character_introducer_does_not_select_the_UK_set_or_touch_a_slot()
    {
        var (buffer, parser) = Create();
        parser.Process($"{Esc}(0{Esc}-A#q");
        Assert.Equal("#─", Line(buffer));
        Assert.Equal(new[] { Graphics, Ascii, Ascii, Ascii }, parser.ExportState().Charsets);
    }

    [Fact]
    public void A_designation_split_at_every_byte_still_applies()
    {
        string sequence = $"{Esc}(A#";
        for (int split = 0; split <= sequence.Length; split++)
        {
            var (buffer, parser) = Create();
            parser.Process(sequence[..split]);
            parser.Process(sequence[split..]);
            Assert.Equal("£", Line(buffer));
        }
    }

    [Fact]
    public void Cursor_save_and_restore_carry_the_designation()
    {
        var (buffer, parser) = Create();
        parser.Process($"{Esc}(A{Esc}7{Esc}(B#{Esc}8#");
        Assert.Equal("£", Line(buffer)); // DECRC returned to column 0 and to the UK set.
        Assert.Equal(Uk, buffer.ExportState(0).SavedCursorMain.Charsets![0]);
    }

    [Fact]
    public void State_transfer_carries_the_live_and_saved_designation()
    {
        var (buffer, parser) = Create();
        parser.Process($"{Esc})A\u000e{Esc}7");
        var state = buffer.ExportState(0);
        state.Parser = parser.ExportState();

        var (target, targetParser) = Create();
        TerminalStateTransfer.Restore(target, targetParser, state);
        targetParser.Process("#");
        Assert.Equal("£", Line(target));
        targetParser.Process($"{Esc})B{Esc}8#");
        Assert.Equal("£", Line(target));
    }

    [Fact]
    public void DECFRA_reads_its_fill_character_through_the_UK_set()
    {
        var (buffer, parser) = Create(cols: 4, rows: 2);
        parser.Process($"{Esc}(A{Esc}[35;1;1;1;3$x");
        Assert.Equal("£££", Line(buffer));
    }
}
