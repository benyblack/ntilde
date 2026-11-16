using System.Security.Cryptography;
using System.Text;

namespace Ntilde.VT.Tests;

/// <summary>The exact ordered ASCII fixtures behind #979, including the preceding width experiment.</summary>
public class AsciiReflowQualificationTests
{
    private const string Reset = "\x1b[?1049l\x1b[!p\x1b[?6l\x1b[r\x1b[4l\x1b[0m\x1b[2J\x1b[3J\x1b[H";

    [Theory]
    [InlineData(false, 8192)]
    [InlineData(true, 8192)]
    [InlineData(true, 37)]
    [InlineData(true, 1)]
    public void History_roundtrip_matches_the_model_after_the_width_roundtrip(bool precedingWidthExperiment, int chunkSize)
    {
        var buffer = new TerminalBuffer(140, 40);
        var parser = new AnsiParser(buffer);
        string width = WidthSeed();
        string history = HistorySeed();
        // Pins from makeReflowCases(40, 140), ascii-reflow-contracts.mjs v1. The test model
        // below slices authored lines independently; it never uses production reflow output.
        Assert.Equal("b1214bde0bf57ea881136da4783f72ac03167c5072661db07c27bd33ebaa9492", Hash(width));
        Assert.Equal("5e54ac5596d68b647bff7ea378191b5e4608eaca45fe9720b00dbb09a7f54d27", Hash(history));
        if (precedingWidthExperiment)
        {
            Feed(parser, width, chunkSize);
            AssertWidthLayout(buffer, 140, 46);
            Assert.Equal(new[] { 0, 3, 6, 9, 12, 15 }, WrappedViewportRows(buffer));
            buffer.Resize(133, 40);
            AssertWidthLayout(buffer, 133, 53);
            buffer.Resize(140, 40);
            AssertWidthLayout(buffer, 140, 46);
            Assert.Equal(new[] { 0, 3, 6, 9, 12, 15 }, WrappedViewportRows(buffer));
        }

        Feed(parser, history, chunkSize);
        AssertHistoryLayout(buffer, 140, 40, 0, 39, 138);
        buffer.Resize(133, 36);
        AssertHistoryLayout(buffer, 133, 36, 44, 35, 5);
        buffer.Resize(140, 40);
        // Growing retains the first visible logical line (22), adding bottom padding.
        AssertHistoryLayout(buffer, 140, 40, 22, 17, 138);
    }

    private static void AssertHistoryLayout(TerminalBuffer buffer, int cols, int rows, int history, int cursorRow, int cursorCol)
    {
        Assert.Equal((cols, rows), (buffer.Cols, buffer.Rows));
        Assert.Equal(history, buffer.Scrollback.Count);
        Assert.Equal((cursorRow, cursorCol), (buffer.CursorRow, buffer.CursorCol));
        var physical = Enumerable.Range(0, 40).SelectMany(index =>
        {
            string text = LongText(index, 138);
            return Enumerable.Range(0, (text.Length + cols - 1) / cols)
                .Select(part => (Text: text.Substring(part * cols, Math.Min(cols, text.Length - part * cols)),
                    Wrapped: (part + 1) * cols < text.Length));
        }).ToList();
        while (physical.Count < history + rows) physical.Add(("", false));
        Assert.Equal(history + rows, physical.Count);
        for (int row = 0; row < physical.Count; row++)
        {
            var expected = physical[row];
            buffer.Lock.EnterReadLock();
            try
            {
                string actual = new(Enumerable.Range(0, cols).Select(col => buffer.GetCellAbsolute(col, row).Character).ToArray());
                Assert.Equal(expected.Text.PadRight(cols), actual);
                bool wrapped = row < history ? buffer.Scrollback.IsRowWrapped(row) : buffer.ViewportRows[row - history].IsWrapped;
                Assert.Equal(expected.Wrapped, wrapped);
            }
            finally { buffer.Lock.ExitReadLock(); }
        }
        Assert.False(buffer.IsAltScreenActive);
        Assert.Equal((0, rows - 1), (buffer.ScrollTop, buffer.ScrollBottom));
    }

    private static void AssertWidthLayout(TerminalBuffer buffer, int cols, int cursorCol)
    {
        Assert.Equal((cols, 40), (buffer.Cols, buffer.Rows));
        Assert.Equal(0, buffer.Scrollback.Count);
        Assert.Equal((1, cursorCol), (buffer.CursorRow, buffer.CursorCol));
        var lines = Enumerable.Range(0, 12).Select(index => index % 2 == 0
            ? LongText(index / 2, 210) : ShortText(index / 2))
            .Concat(Enumerable.Range(6, 16).Select(ShortText));
        int rowIndex = 0;
        foreach (string text in lines)
        {
            for (int offset = 0; offset < text.Length; offset += cols)
            {
                var row = buffer.ViewportRows[rowIndex];
                string expected = text.Substring(offset, Math.Min(cols, text.Length - offset)).PadRight(cols);
                Assert.Equal(expected, new string(row.Cells.Select(cell => cell.Character).ToArray()));
                Assert.Equal(offset + cols < text.Length, row.IsWrapped);
                for (int col = 0; col < cols; col++)
                {
                    bool probe = rowIndex == 2 && col < 5;
                    Assert.Equal(probe, row.Cells[col].IsBold);
                    Assert.Equal(probe, row.Cells[col].IsUnderline);
                }
                rowIndex++;
            }
        }
        foreach (var row in buffer.ViewportRows.Skip(rowIndex))
        {
            Assert.False(row.IsWrapped);
            Assert.All(row.Cells, cell => Assert.Equal(' ', cell.Character));
        }
    }

    private static void Feed(AnsiParser parser, string bytes, int chunkSize)
    {
        for (int offset = 0; offset < bytes.Length; offset += chunkSize)
            parser.Process(bytes.Substring(offset, Math.Min(chunkSize, bytes.Length - offset)));
    }

    private static int[] WrappedViewportRows(TerminalBuffer buffer) => Enumerable.Range(0, buffer.Rows)
        .Where(index => buffer.ViewportRows[index].IsWrapped).ToArray();

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string At(int row, int col = 0) => $"\x1b[{row + 1};{col + 1}H";
    private static string ShortText(int index) => $"RFs{index:D3} ATTR REST";

    private static string LongText(int index, int length) => new(Enumerable.Range(0, length)
        .Select(offset => offset % 10 == 9 ? (char)('0' + offset / 10 % 10) : (char)('A' + (offset + 7 * index) % 26)).ToArray());

    private static string WidthSeed()
    {
        var seed = new StringBuilder(Reset);
        int row = 0;
        for (int index = 0; index < 12; index++)
        {
            seed.Append(At(row));
            if (index % 2 == 0) { seed.Append(LongText(index / 2, 210)); row += 2; }
            else
            {
                string text = ShortText(index / 2);
                if (index == 1) seed.Append("\x1b[1;4m").Append(text.AsSpan(0, 5)).Append("\x1b[0m").Append(text.AsSpan(5));
                else seed.Append(text);
                row++;
            }
        }
        for (int index = 6; index < 22; index++) seed.Append(At(row++)).Append(ShortText(index));
        return seed.Append(At(1, 46)).ToString();
    }

    private static string HistorySeed()
    {
        var seed = new StringBuilder(Reset);
        for (int row = 0; row < 40; row++) seed.Append(At(row)).Append(LongText(row, 138));
        return seed.Append(At(39, 138)).ToString();
    }
}
