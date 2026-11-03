namespace Ntilde.VT.Tests;

/// <summary>Count parameters in xterm-compatible ECH/IL/DL treat zero as the one-count default.</summary>
public sealed class EditingDefaultParameterTests
{
    public static TheoryData<string, string, string[]> Cases
    {
        get
        {
            var cases = new TheoryData<string, string, string[]>();
            foreach (string parameter in new[] { "", "0", "1" })
            {
                cases.Add("X", parameter, ["ROW0", "ROW1", "RO 2", "ROW3", "ROW4", "ROW5"]);
                cases.Add("L", parameter, ["ROW0", "ROW1", "", "ROW2", "ROW3", "ROW5"]);
                cases.Add("M", parameter, ["ROW0", "ROW1", "ROW3", "ROW4", "", "ROW5"]);
            }
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Zero_and_omitted_counts_apply_one_operation_preserving_cursor_margins_and_history(string command, string parameter, string[] expected)
    {
        var buffer = new TerminalBuffer(12, 6);
        var parser = new AnsiParser(buffer);
        parser.Process("HISTORY0\r\nHISTORY1" + string.Concat(Enumerable.Repeat("\r\n", 6)));
        for (int row = 0; row < 6; row++) parser.Process($"\x1b[{row + 1};1HROW{row}");
        parser.Process("\x1b[2;5r\x1b[3;3H\x1b[38;2;204;1;77;48;2;1;44;199m");
        parser.Process($"\x1b[{parameter}{command}");

        buffer.Lock.EnterReadLock();
        try
        {
            for (int row = 0; row < 6; row++)
            {
                for (int col = 0; col < 12; col++)
                {
                    char wanted = col < expected[row].Length ? expected[row][col] : ' ';
                    Assert.Equal(wanted, buffer.GetCell(col, row).Character);
                }
            }
            Assert.Equal((2, 2), (buffer.CursorRow, buffer.CursorCol));
            Assert.Equal((1, 4), (buffer.ScrollTop, buffer.ScrollBottom));
            Assert.Equal(2, buffer.Scrollback.Count);
            TerminalCell erased = buffer.GetCell(command == "X" ? 2 : 0, command == "M" ? 4 : 2);
            if (command == "X")
            {
                Assert.False(erased.IsDefaultBackground);
                Assert.False(erased.IsPaletteBackground);
                Assert.Equal(0xff012cc7u, erased.Bg);
            }
            else
            {
                Assert.True(erased.IsDefaultBackground);
            }
        }
        finally { buffer.Lock.ExitReadLock(); }
    }
}
