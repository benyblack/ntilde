namespace Ntilde.VT.Tests;

// A whole-row erase (EL 2, ED) ends the logical line that ran through the row, so the row's
// soft-wrap flag must go with its cells. Before this, a cursor-addressed repaint that erased a
// wrapped row and wrote shorter content left IsWrapped set, and every reader that rejoins soft
// wraps (reflow, the screen-inference capture) glued the repainted row to the untouched row
// below it — including gluing a token onto an unrelated prefix so the secrets filter missed it.
public class EraseLineClearsWrapTests
{
    private const int Cols = 20;
    private const int Rows = 5;

    private static (TerminalBuffer buffer, AnsiParser parser) WrappedTerminal()
    {
        var buffer = new TerminalBuffer(Cols, Rows);
        var parser = new AnsiParser(buffer);
        // 44 characters: row 0 wraps into row 1, row 1 into row 2.
        parser.Process("key ghp_abcdefghijklmnopqrstuvwxyz0123456789\r\n$ ");
        Assert.True(buffer.ViewportRows[0].IsWrapped);
        Assert.True(buffer.ViewportRows[1].IsWrapped);
        Assert.False(buffer.ViewportRows[2].IsWrapped);
        return (buffer, parser);
    }

    [Fact]
    public void EraseLineAll_ClearsTheRowsWrapFlag()
    {
        var (buffer, parser) = WrappedTerminal();

        parser.Process("\x1b[1;1H\x1b[2Kprefix");

        Assert.False(buffer.ViewportRows[0].IsWrapped);
        Assert.True(buffer.ViewportRows[1].IsWrapped, "the untouched row keeps its own wrap");
    }

    [Fact]
    public void EraseInDisplay_ClearsEveryErasedRowsWrapFlag()
    {
        var (buffer, parser) = WrappedTerminal();

        parser.Process("\x1b[2J");

        Assert.All(buffer.ViewportRows, row => Assert.False(row.IsWrapped));
    }

    [Fact]
    public void RewritingAnErasedRowToTheLastColumn_SetsTheWrapFlagAgain()
    {
        var (buffer, parser) = WrappedTerminal();

        parser.Process("\x1b[1;1H\x1b[2K" + new string('x', Cols + 3));

        Assert.True(buffer.ViewportRows[0].IsWrapped);
    }
}
