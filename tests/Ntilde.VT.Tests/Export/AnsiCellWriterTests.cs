using System.Text;
using Ntilde.VT;
using Ntilde.VT.Export;

namespace Ntilde.VT.Tests.Export;

public sealed class AnsiCellWriterTests
{
    private static TerminalBuffer Parse(string text, int cols = 20, int rows = 3)
    {
        var buffer = new TerminalBuffer(cols, rows);
        new AnsiParser(buffer, forceConPtyFiltering: false) { ImageDecoder = null }.Process(text);
        return buffer;
    }

    private static string WriteRow(TerminalBuffer buffer, int row, int maxCols)
    {
        using TerminalRenderSnapshot snap = buffer.CaptureRenderSnapshot(new RenderSnapshotRequest { ViewportRows = buffer.Rows, ViewportCols = buffer.Cols }, out _);
        var sb = new StringBuilder();
        AnsiCellWriter.AppendRow(sb, snap.RowsData.Array![row].Cells, maxCols);
        return sb.ToString();
    }

    [Fact]
    public void A_written_row_reproduces_text_and_attributes_when_replayed()
    {
        TerminalBuffer source = Parse("\x1b[1;31mAB\x1b[0m \x1b[38;2;10;20;30;48;5;200mC\x1b[0m \x1b[3;4;7mD\x1b[0m 界X");

        TerminalBuffer replay = Parse(WriteRow(source, 0, source.Cols));

        // GetCell/GetGrapheme assert the buffer's read lock is held (TerminalBuffer.AssertLockHeld);
        // the brief's test omitted this, so this deviates from it with the smallest fix (see report).
        source.Lock.EnterReadLock();
        replay.Lock.EnterReadLock();
        try
        {
            for (int c = 0; c < 12; c++)
            {
                TerminalCell a = source.GetCell(c, 0), b = replay.GetCell(c, 0);
                Assert.True(source.GetGrapheme(c, 0) == replay.GetGrapheme(c, 0), $"grapheme at {c}");
                Assert.True((a.IsBold, a.IsItalic, a.IsUnderline, a.IsInverse, a.IsWide, a.IsWideContinuation)
                         == (b.IsBold, b.IsItalic, b.IsUnderline, b.IsInverse, b.IsWide, b.IsWideContinuation), $"attributes at {c}");
                Assert.True((a.IsDefaultForeground, a.IsPaletteForeground, a.Fg, a.IsDefaultBackground, a.IsPaletteBackground, a.Bg)
                         == (b.IsDefaultForeground, b.IsPaletteForeground, b.Fg, b.IsDefaultBackground, b.IsPaletteBackground, b.Bg), $"colours at {c}");
            }
        }
        finally
        {
            replay.Lock.ExitReadLock();
            source.Lock.ExitReadLock();
        }
    }

    [Fact]
    public void A_style_change_is_a_full_reset_form_and_the_row_ends_plain()
    {
        string row = WriteRow(Parse("\x1b[1mA\x1b[0mB"), 0, 2);

        Assert.StartsWith("\x1b[0;1mA\x1b[0mB", row, StringComparison.Ordinal);
        Assert.EndsWith(AnsiCellWriter.Reset, row, StringComparison.Ordinal);
    }

    [Fact]
    public void A_wide_cell_cut_by_the_clip_edge_is_a_space_and_the_column_count_is_exact()
    {
        TerminalBuffer source = Parse("abcd界", cols: 10, rows: 1);   // 界 occupies columns 4 and 5
        using TerminalRenderSnapshot snap = source.CaptureRenderSnapshot(new RenderSnapshotRequest { ViewportRows = 1, ViewportCols = 10 }, out _);
        var sb = new StringBuilder();

        int written = AnsiCellWriter.AppendRow(sb, snap.RowsData.Array![0].Cells, maxCols: 5);

        Assert.Equal(5, written);
        Assert.DoesNotContain("界", sb.ToString(), StringComparison.Ordinal);
        TerminalBuffer replay = Parse(sb.ToString(), cols: 10, rows: 1);
        Assert.Equal("abcd", TerminalExporter.GetVisibleRowTexts(replay)[0]);
        replay.Lock.EnterReadLock();
        try
        {
            Assert.Equal(" ", replay.GetGrapheme(4, 0));
        }
        finally
        {
            replay.Lock.ExitReadLock();
        }
    }

    [Theory]
    [InlineData("\x1b[32mx", "\x1b[0;32m")]
    [InlineData("\x1b[92mx", "\x1b[0;92m")]
    [InlineData("\x1b[38;5;123mx", "\x1b[0;38;5;123m")]
    [InlineData("\x1b[44mx", "\x1b[0;44m")]
    [InlineData("\x1b[2;9mx", "\x1b[0;2;9m")]
    [InlineData("\x1b[5mx", "\x1b[0;5m")]
    [InlineData("\x1b[8mx", "\x1b[0;8m")]
    public void Sgr_forms(string input, string expectedPrefix) =>
        Assert.StartsWith(expectedPrefix, WriteRow(Parse(input), 0, 1), StringComparison.Ordinal);

    [Fact]
    public void Multi_codepoint_graphemes_are_emitted_in_full_and_the_column_count_matches_their_display_width()
    {
        // Man+ZWJ+Woman: a ZWJ join sequence, one grapheme, 2 columns wide.
        const string Family = "\U0001F468‍\U0001F469";
        // 'e' + combining acute accent: one grapheme, 2 UTF-16 code units, but only 1 column wide.
        const string EAcute = "é";
        // Heavy black heart + VS16 (emoji presentation selector): one grapheme, 2 columns wide.
        const string Heart = "❤️";

        TerminalBuffer source = Parse(Family + EAcute + Heart, cols: 20, rows: 1);
        using TerminalRenderSnapshot snap = source.CaptureRenderSnapshot(new RenderSnapshotRequest { ViewportRows = 1, ViewportCols = source.Cols }, out _);
        var sb = new StringBuilder();

        int written = AnsiCellWriter.AppendRow(sb, snap.RowsData.Array![0].Cells, maxCols: 5);

        Assert.Equal(5, written);
        Assert.Equal(AnsiCellWriter.Reset + Family + EAcute + Heart + AnsiCellWriter.Reset, sb.ToString());
    }
}
