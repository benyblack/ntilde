using Ntilde.VT;
using Ntilde.VT.Export;
using System.Runtime.InteropServices;

namespace Ntilde.VT.Tests;

public class OverlineTests
{
    [Fact]
    public void Sgr53_WritesSparseDecoration_WithoutEnablingUnderline()
    {
        var buffer = new TerminalBuffer(20, 3);
        new AnsiParser(buffer).Process("\x1b[53mA\x1b[55mB");

        Assert.NotNull(buffer.GetUnderlineAbsolute(0, 0));
        Assert.Null(buffer.GetUnderlineAbsolute(1, 0));
        buffer.Lock.EnterReadLock();
        try { Assert.False(buffer.GetCell(0, 0).IsUnderline); }
        finally { buffer.Lock.ExitReadLock(); }
    }

    private static CellUnderline? At(TerminalBuffer buffer, int col, int row = 0)
        => buffer.GetUnderlineAbsolute(col, buffer.Scrollback.Count + row);

    [Fact]
    public void Overline_DoesNotWidenThePackedCell()
        => Assert.Equal(12, Marshal.SizeOf<TerminalCell>());

    [Theory]
    [InlineData("0")]
    [InlineData("")]
    [InlineData("55")]
    public void Reset_ClearsOverline(string reset)
    {
        var buffer = new TerminalBuffer(20, 3);
        new AnsiParser(buffer).Process($"\x1b[53mA\x1b[{reset}mB");
        Assert.True(At(buffer, 0)?.Overline);
        Assert.Null(At(buffer, 1));
        Assert.False(buffer.IsOverline);
    }

    [Fact]
    public void SelectiveResets_KeepUnderlineAndOverlineIndependent()
    {
        var buffer = new TerminalBuffer(20, 3);
        new AnsiParser(buffer).Process("\x1b[4:3;58;5;9;53mA\x1b[24mB\x1b[4:2mC\x1b[55mD");
        Assert.Equal(new CellUnderline(UnderlineStyle.Curly, null, 9, true), At(buffer, 0));
        Assert.Equal(CellUnderline.OverlineOnly, At(buffer, 1)); // no latent underline color/style
        Assert.Equal(new CellUnderline(UnderlineStyle.Double, null, 9, true), At(buffer, 2));
        Assert.Equal(new CellUnderline(UnderlineStyle.Double, null, 9), At(buffer, 3));
    }

    [Theory]
    [InlineData("\x1b[H\x1b[K")]
    [InlineData("\x1b[H\x1b[3X")]
    [InlineData("\x1b[2J")]
    [InlineData("\r   ")]
    public void EraseAndOverwrite_ClearSideEntries(string operation)
    {
        var buffer = new TerminalBuffer(20, 3);
        new AnsiParser(buffer).Process("\x1b[53mABC\x1b[0m" + operation);
        for (int col = 0; col < 3; col++) Assert.Null(At(buffer, col));
    }

    [Fact]
    public void InsertAndDelete_MoveTheDecorationWithItsCell()
    {
        var buffer = new TerminalBuffer(20, 3);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b[53mA\x1b[0mB\r\x1b[2@");
        Assert.Null(At(buffer, 0));
        Assert.True(At(buffer, 2)?.Overline);
        parser.Process("\x1b[2P");
        Assert.True(At(buffer, 0)?.Overline);
        Assert.Null(At(buffer, 2));
    }

    [Fact]
    public void WideAndCombiningText_CarryTheDecoration()
    {
        var buffer = new TerminalBuffer(20, 3);
        new AnsiParser(buffer).Process("\x1b[53m中e\u0301");
        for (int col = 0; col < 3; col++) Assert.True(At(buffer, col)?.Overline);
    }

    [Fact]
    public void Reflow_PreservesOverlinedTrailingBlanks()
    {
        var buffer = new TerminalBuffer(6, 3);
        new AnsiParser(buffer).Process("x\x1b[53m     \x1b[0mY");
        buffer.Resize(3, 5);
        for (int col = 1; col < 6; col++) Assert.True(At(buffer, col % 3, col / 3)?.Overline);
        Assert.Null(At(buffer, 0, 2));
    }

    [Fact]
    public void Transfer_PreservesHistoryAltScreenLiveAndSavedPens()
    {
        var buffer = new TerminalBuffer(20, 3);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b[53mhistory\x1b[0m\r\nx\r\nx\r\nx\r\n");
        parser.Process("\x1b[53mM\x1b" + "7\x1b[?1049h\x1b[H\x1b[4:3;53mA");
        var snapshot = TerminalStateSerializer.FromBytes(TerminalStateSerializer.ToBytes(buffer.ExportState(int.MaxValue)));
        var restored = new TerminalBuffer(20, 3);
        restored.ImportState(snapshot);
        var restoredParser = new AnsiParser(restored);
        Assert.True(restored.GetUnderlineAbsolute(0, 0)?.Overline); // active alt
        restoredParser.Process("B");
        Assert.Equal(new CellUnderline(UnderlineStyle.Curly, Overline: true), restored.GetUnderlineAbsolute(1, 0));
        restoredParser.Process("\x1b[?1049l");
        Assert.True(restored.GetUnderlineAbsolute(0, 0)?.Overline); // history
        restoredParser.Process("N");
        Assert.Equal(CellUnderline.OverlineOnly, At(restored, 1, restored.CursorRow));
    }

    [Theory]
    [InlineData("\x1b[!p")]
    [InlineData("\x1b" + "c")]
    public void TerminalReset_ClearsTheLivePen(string reset)
    {
        var buffer = new TerminalBuffer(20, 3);
        new AnsiParser(buffer).Process("\x1b[53m" + reset + "A");
        Assert.False(buffer.IsOverline);
        Assert.Null(At(buffer, 0));
    }

    [Fact]
    public void RenderSnapshot_CarriesOverlineAndInvalidatesDecorationOnlyChanges()
    {
        var buffer = new TerminalBuffer(20, 3);
        var parser = new AnsiParser(buffer);
        var request = new RenderSnapshotRequest { ViewportRows = 3, ViewportCols = 20 };
        parser.Process("A");
        using (buffer.CaptureRenderSnapshot(request, out _)) { }
        parser.Process("\r\x1b[53mA\x1b[0m\x1b[6GZ");
        using var snapshot = buffer.CaptureRenderSnapshot(request, out _);
        Assert.Equal(CellUnderline.OverlineOnly, snapshot.RowsData.Array![0].Cells![0].Underline);
        Assert.Contains(snapshot.DirtySpans.Array.Take(snapshot.DirtySpans.Length), span => span.Row == 0 && span.ColStart == 0);
    }

    [Fact]
    public void AnsiExport_RoundTripsMixedDecorationsAndTrailingBlanks()
    {
        var buffer = new TerminalBuffer(20, 3);
        new AnsiParser(buffer).Process("\x1b[53mA\x1b[4:3;58;5;9mB\x1b[24mC\x1b[55mD\x1b[53m   \x1b[0m");
        var restored = new TerminalBuffer(20, 3);
        new AnsiParser(restored).Process(TerminalExporter.ExportToAnsi(buffer).Split('\n')[0]);
        for (int col = 0; col < 8; col++) Assert.Equal(At(buffer, col), At(restored, col));
        Assert.False(restored.IsOverline);
    }

    [Fact]
    public void SplitInputAndSavedCursor_PreserveOverline()
    {
        const string sequence = "\x1b[53m\x1b" + "7\x1b[55mA\x1b" + "8B";
        for (int split = 0; split <= sequence.Length; split++)
        {
            var buffer = new TerminalBuffer(20, 3);
            var parser = new AnsiParser(buffer);
            parser.Process(sequence[..split]);
            parser.Process(sequence[split..]);
            Assert.True(buffer.IsOverline);
            Assert.True(At(buffer, 0)?.Overline);
        }
    }

    [Fact]
    public void PagedHistoryRenderSnapshot_PreservesOverlineWithoutUnderlineFlag()
    {
        var buffer = new TerminalBuffer(20, 3);
        new AnsiParser(buffer).Process("\x1b[53mA\x1b[0m\r\nx\r\nx\r\nx\r\n");
        Assert.True(buffer.Scrollback.Count > 0);
        using var snapshot = buffer.CaptureRenderSnapshot(new RenderSnapshotRequest
        {
            ViewportRows = 3,
            ViewportCols = 20,
            ScrollOffset = buffer.Scrollback.Count,
            Isolated = true,
        }, out _);
        var cell = snapshot.RowsData.Array![0].Cells![0];
        Assert.Equal('A', cell.Character);
        Assert.Equal(CellUnderline.OverlineOnly, cell.Underline);
        Assert.False(cell.IsUnderline);
    }

    [Fact]
    public void MuxAnsiCellWriter_PreservesOverlineAndResetsIt()
    {
        var source = new TerminalBuffer(20, 3);
        new AnsiParser(source).Process("\x1b[53mA\x1b[55mB");
        using var snapshot = source.CaptureRenderSnapshot(new RenderSnapshotRequest { ViewportRows = 3, ViewportCols = 20 }, out _);
        var text = new System.Text.StringBuilder();
        AnsiCellWriter.AppendRow(text, snapshot.RowsData.Array![0].Cells!, 2);
        var target = new TerminalBuffer(20, 3);
        new AnsiParser(target).Process(text.ToString());
        Assert.True(At(target, 0)?.Overline);
        Assert.Null(At(target, 1));
        Assert.False(target.IsOverline);
    }

    [Fact]
    public async Task LegacyReplayCheckpoint_PreservesOverlineCellsAndLivePen()
    {
        string path = Path.GetTempFileName();
        try
        {
            var buffer = new TerminalBuffer(20, 3);
            var parser = new AnsiParser(buffer);
            parser.Process("\x1b[53mA\x1b[4:3mB");
            using (var recorder = new Ntilde.Replay.PtyRecorder(path, 20, 3)) recorder.RecordSnapshot(buffer, parser);
            var target = new TerminalBuffer(20, 3);
            var targetParser = new AnsiParser(target);
            bool applied = false;
            await new Ntilde.Replay.ReplayRunner(path).RunAsync(_ => Task.CompletedTask,
                onSnapshotCallback: snapshot =>
                {
                    snapshot.State = null; // exercise the retained legacy checkpoint projection
                    Ntilde.Replay.ReplayRunner.ApplySnapshot(target, targetParser, snapshot);
                    applied = true;
                    return Task.CompletedTask;
                }, ct: TestContext.Current.CancellationToken);
            Assert.True(applied);
            Assert.Equal(CellUnderline.OverlineOnly, At(target, 0));
            Assert.Equal(new CellUnderline(UnderlineStyle.Curly, Overline: true), At(target, 1));
            targetParser.Process("C");
            Assert.Equal(new CellUnderline(UnderlineStyle.Curly, Overline: true), At(target, 2));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(3, false, "\x1b[53m")]
    [InlineData(1, true, "\x1b[53m")]
    [InlineData(3, false, "\x1b[4:3m")]
    [InlineData(1, true, "\x1b[4:3m")]
    public void Reflow_PreservesDecoratedBlankRowsBelowTheCursor(int row, bool laterText, string style)
    {
        var buffer = new TerminalBuffer(20, 6);
        var parser = new AnsiParser(buffer);
        parser.Process($"\x1b[{row + 1};1H{style}   \x1b[0m");
        if (laterText) parser.Process($"\x1b[{row + 2};1HZ");
        parser.Process("\x1b[H");
        var expected = At(buffer, 0, row);
        Assert.NotNull(expected);
        buffer.Resize(10, 6);
        for (int col = 0; col < 3; col++) Assert.Equal(expected, At(buffer, col, row));
        Assert.Equal(0, buffer.CursorRow);
        Assert.Equal(0, buffer.CursorCol);
    }

    [Theory]
    [InlineData("55")]
    [InlineData("0")]
    public void SplitInput_PreservesEarlierOverlineAndTheResetCell(string reset)
    {
        string sequence = "\x1b[53mA\x1b[" + reset + "mB";
        for (int split = 1; split < sequence.Length; split++)
        {
            var buffer = new TerminalBuffer(20, 3);
            var parser = new AnsiParser(buffer);
            parser.Process(sequence[..split]);
            parser.Process(sequence[split..]);
            Assert.Equal(CellUnderline.OverlineOnly, At(buffer, 0));
            Assert.Null(At(buffer, 1));
        }
    }
}
