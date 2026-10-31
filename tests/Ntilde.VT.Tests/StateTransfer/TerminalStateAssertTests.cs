using Ntilde.VT;
using Xunit.Sdk;

namespace Ntilde.VT.Tests.StateTransfer;

public sealed class TerminalStateAssertTests
{
    private static (TerminalBuffer Buffer, AnsiParser Parser) Terminal(string input)
    {
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { ImageDecoder = null };
        parser.Process(input);
        return (buffer, parser);
    }

    [Fact]
    public void Identical_terminals_are_equivalent()
    {
        var a = Terminal("hello\r\n\x1b[1mworld");
        var b = Terminal("hello\r\n\x1b[1mworld");

        TerminalStateAssert.AssertEquivalent("[same]", a.Buffer, a.Parser, b.Buffer, b.Parser);
    }

    [Fact]
    public void One_different_cell_is_caught()
    {
        var a = Terminal("hello");
        var b = Terminal("hellO");

        Assert.ThrowsAny<XunitException>(() =>
            TerminalStateAssert.AssertEquivalent("[cell]", a.Buffer, a.Parser, b.Buffer, b.Parser));
    }

    [Fact]
    public void A_parser_left_mid_escape_is_caught_even_when_the_screens_match()
    {
        var a = Terminal("hi");
        var b = Terminal("hi\x1b[3");

        Assert.ThrowsAny<XunitException>(() =>
            TerminalStateAssert.AssertEquivalent("[parser]", a.Buffer, a.Parser, b.Buffer, b.Parser));
    }

    [Theory]
    [InlineData("\u001b[?7l\u001b[?1048h\u001b[?7h")]
    [InlineData("\u001b[?6h\u001b[?1048h\u001b[?6l")]
    [InlineData("\u001b(0\u001b[?1048h\u001b(B")]
    [InlineData("\u001b[4:3m\u001b[?1048h\u001b[0m")]
    public void Different_saved_cursor_context_is_caught_even_when_live_state_matches(string input)
    {
        var a = Terminal(input);
        var b = Terminal("\u001b[?1048h");
        Assert.ThrowsAny<XunitException>(() =>
            TerminalStateAssert.AssertEquivalent("[saved context]", a.Buffer, a.Parser, b.Buffer, b.Parser));
    }

}
