using System.Text;
using Ntilde.VT.Export;

namespace Ntilde.VT.Tests;

public sealed class Osc66ExportTests
{
    [Fact]
    public void Ansi_snapshot_restores_the_block_without_overwriting_its_lower_row()
    {
        var source = new TerminalBuffer(12, 4);
        new AnsiParser(source).Process("\x1b]66;s=2:w=2;nested text\a!\x1b[2;5Hbelow");
        string ansi = TerminalExporter.ExportToAnsi(source);
        Assert.Contains("\x1b]66;", ansi);
        var restored = new TerminalBuffer(12, 4);
        new AnsiParser(restored).Process("\x1b[20h" + ansi);
        restored.Lock.EnterReadLock();
        try
        {
            Assert.Equal("nested text", restored.GetTextSizingAbsolute(3, 1)!.Block.Text);
            Assert.Equal("b", restored.GetGraphemeAbsolute(4, 1));
            Assert.Equal("!", restored.GetGraphemeAbsolute(4, 0));
        }
        finally { restored.Lock.ExitReadLock(); }
    }

    [Theory]
    [InlineData("long unbounded payload", 2)]
    [InlineData("界界界", 3)]
    public void Unnegotiated_text_client_fallback_keeps_the_exact_physical_width(string text, int width)
    {
        var source = new TerminalBuffer(12, 4);
        new AnsiParser(source).Process($"\x1b]66;w={width};{text}\a!");
        using var snapshot = source.CaptureRenderSnapshot(new RenderSnapshotRequest
        { ViewportRows = 4, ViewportCols = 12, Isolated = true }, out _);
        var sb = new StringBuilder();
        Assert.Equal(12, AnsiCellWriter.AppendRow(sb, snapshot.RowsData.Array[0].Cells, 12));
        Assert.DoesNotContain("\x1b]66;", sb.ToString());
        var outer = new TerminalBuffer(12, 4);
        new AnsiParser(outer).Process(sb.ToString());
        outer.Lock.EnterReadLock();
        try { Assert.Equal("!", outer.GetGraphemeAbsolute(width, 0)); }
        finally { outer.Lock.ExitReadLock(); }
    }
}
