using System.Text;
using Ntilde.VT;

namespace Ntilde.VT.Tests;

// The reflow keeps a right-aligned prompt against the right edge when the width changes. That
// heuristic looks at the cursor row and the two rows above it, and used to treat any 10-space gap
// there as the gap before a right prompt, whether or not the text after it touched the edge. A
// printf table's padded columns leave exactly that gap, so a widening resize (a pane going from
// scale 1 to 2 re-snaps its cell width, 106 -> 112 columns in hero-split) pushed the last two
// table rows' tails to the new right edge.
public class ReflowRightPromptTests
{
    [Fact]
    public void Widen_PaddedOutputAboveThePrompt_KeepsItsColumns()
    {
        var buffer = new TerminalBuffer(106, 22);
        var parser = new AnsiParser(buffer);
        for (int i = 0; i < 4; i++)
        {
            parser.Process(Row($"{1230 + i}", "dev", " 0.8", " 0.5", "sleeping", "00:00:07", "theme-loader") + "\r\n");
        }
        parser.Process("$ ");

        buffer.Resize(112, 23);

        // The prompt is on row 4, so rows 2 and 3 are the two the heuristic used to move.
        for (int r = 0; r < 4; r++)
        {
            Assert.Equal(Row($"{1230 + r}", "dev", " 0.8", " 0.5", "sleeping", "00:00:07", "theme-loader"), RowText(buffer, r));
        }
        Assert.Equal("$", RowText(buffer, 4));
    }

    [Theory]
    [InlineData(0)] // drawn up to the last column
    [InlineData(1)] // zsh's default ZLE_RPROMPT_INDENT
    public void Widen_RightPromptAgainstTheEdge_StaysAgainstTheEdge(int indent)
    {
        const int oldCols = 40;
        const int newCols = 50;
        var buffer = new TerminalBuffer(oldCols, 5);
        var parser = new AnsiParser(buffer);

        // "~ $ " on the left, "[main]" right-aligned, cursor back after the left prompt.
        const string badge = "[main]";
        int badgeStart = oldCols - indent - badge.Length + 1; // 1-based column for CHA
        parser.Process($"~ $ \u001b[{badgeStart}G{badge}\u001b[5G");

        buffer.Resize(newCols, 5);

        string row = RowText(buffer, 0);
        Assert.StartsWith("~ $", row);
        Assert.EndsWith(badge, row);
        Assert.True(row.Length >= newCols - GridQueryReader.MaxRightPromptIndent, $"badge left the edge: '{row}'");
    }

    private static string Row(string pid, string user, string cpu, string mem, string state, string time, string command)
        => $"{pid,-6} {user,-10} {cpu,5} {mem,5} {state,-9} {time,-8} {command}";

    private static string RowText(TerminalBuffer buffer, int viewportRow)
    {
        var sb = new StringBuilder();
        foreach (var cell in buffer.ViewportRows[viewportRow].Cells)
        {
            sb.Append(cell.Character == '\0' ? ' ' : cell.Character);
        }

        return sb.ToString().TrimEnd();
    }
}
