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
}
