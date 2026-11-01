using System.Runtime.InteropServices;

namespace Ntilde.VT.Tests;

public sealed class Osc66BudgetReviewTests
{
    private const string ScrollOff = "\x1b[7;1H\n\n\n\n\n\n\n\x1b[1;1H";
    private static readonly string Payload = new('x', 4096);
    private static readonly TextSizingOptions Options = new(7, 7, 0, 0, 0, 0);

    private static TerminalBuffer Retained(int blocks)
    {
        var buffer = new TerminalBuffer(49, 7);
        var parser = new AnsiParser(buffer);
        for (int i = 0; i < blocks; i++)
        {
            buffer.WriteSizedText(Payload, Options);
            parser.Process(ScrollOff);
        }
        return buffer;
    }

    [Fact]
    public void Rejected_reservations_do_not_allocate_a_history_sized_prune_on_every_attempt()
    {
        var buffer = Retained(600);
        new AnsiParser(buffer).Process("\x1b[1;49H");
        for (int i = 0; i < 3; i++) buffer.WriteSizedText(Payload, Options);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20; i++) buffer.WriteSizedText(Payload, Options);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 128 * 1024, $"rejected reservations allocated {allocated} bytes");
        Assert.Equal(48, buffer.CursorCol);
    }

    [Fact]
    public void Ordinary_output_does_not_allocate_an_erase_dictionary_for_a_retained_history_block()
    {
        static long Measure(TerminalBuffer buffer)
        {
            new AnsiParser(buffer).Process("\x1b[?7l");
            for (int i = 0; i < 100; i++) buffer.Write("a");
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) buffer.Write("a");
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
        long baseline = Measure(new TerminalBuffer(49, 7));
        long retained = Measure(Retained(1));
        Assert.True(retained < baseline + 16 * 1024, $"ordinary={baseline}, retained={retained}");
    }

    [Fact]
    public void Valid_non_overlapping_import_reaches_the_summed_storage_budget_check()
    {
        var source = Retained(600);
        var state = source.ExportState(int.MaxValue);
        Assert.InRange(state.TextSizing!.Count, 1, 1000);
        state.TextSizing.Add(new TerminalTextSizingState
        {
            Text = Payload,
            Options = Options,
            BaseWidth = 7,
            X = 0,
            Y = state.ScrollbackRowCount
        });
        byte[] bytes = Convert.FromBase64String(state.Main.CellsBase64!);
        var cells = MemoryMarshal.Cast<byte, TerminalCell>(bytes.AsSpan());
        cells[0] = TerminalCell.Default;
        cells[0].Character = 'x';
        cells[0].HasExtendedText = true;
        state.Main.CellsBase64 = Convert.ToBase64String(bytes);
        state.Main.ExtendedText = new() { [0] = Payload };
        state.Main.RowWrittenColumns = Enumerable.Repeat(49, 7).ToArray();
        var target = new TerminalBuffer(49, 7);
        var error = Assert.Throws<InvalidOperationException>(() => target.ImportState(state));
        Assert.Contains("storage budget exceeded", error.Message);
    }

    [Fact]
    public void A_width_increase_rejected_by_the_budget_leaves_cells_cursor_and_metadata_unchanged()
    {
        var target = Retained(600);
        var source = new TerminalBuffer(49, 7);
        source.WriteSizedText("❤", new(7, 0, 0, 0, 0, 0));
        var state = source.ExportState(0);
        target.ApplySnapshot(new ReplaySnapshot
        {
            Cols = 49,
            Rows = 7,
            ScrollBottom = 6,
            IsAutoWrapMode = true,
            CursorCol = state.CursorCol,
            CursorRow = state.CursorRow,
            CellsBase64 = state.Main.CellsBase64,
            ExtendedText = state.Main.ExtendedText,
            RowWrittenColumns = state.Main.RowWrittenColumns,
            TextSizing = state.TextSizing
        });
        var parser = new AnsiParser(target);
        bool rejected = false;
        for (int row = 0; row < 7 && !rejected; row++)
            for (int col = 7; col < 49; col++)
            {
                parser.Process($"\x1b[{row + 1};{col + 1}H");
                target.WriteSizedText("x", new(1, 1, 0, 0, 0, 0));
                target.Lock.EnterReadLock();
                try { rejected = target.GetTextSizingAbsolute(col, target.Scrollback.Count + row) == null; }
                finally { target.Lock.ExitReadLock(); }
                if (rejected) break;
            }
        Assert.True(rejected, "the available viewport space must reach the retained-metadata budget");
        parser.Process("\x1b[1;8H");
        byte[] before = TerminalStateSerializer.ToBytes(target.ExportState(int.MaxValue));
        parser.Process("\uFE0F");
        Assert.Equal(before, TerminalStateSerializer.ToBytes(target.ExportState(int.MaxValue)));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(600, false)]
    [InlineData(600, true)]
    public void Legacy_checkpoint_restores_the_same_visible_block_with_and_without_budget_pressure(int retainedBlocks, bool alternate)
    {
        var target = Retained(retainedBlocks);
        int previousBlocks = target.ExportState(int.MaxValue).TextSizing!.Count;
        if (alternate) new AnsiParser(target).Process("\x1b[?47h");
        var source = new TerminalBuffer(49, 7);
        source.WriteSizedText(Payload, Options);
        var state = source.ExportState(0);
        var checkpoint = new ReplaySnapshot
        {
            Cols = 49,
            Rows = 7,
            IsAltScreen = alternate,
            ScrollBottom = 6,
            IsAutoWrapMode = true,
            CursorCol = state.CursorCol,
            CursorRow = state.CursorRow,
            IsPendingWrap = state.IsPendingWrap,
            CellsBase64 = state.Main.CellsBase64,
            ExtendedText = state.Main.ExtendedText,
            RowWraps = state.Main.RowWraps,
            RowWrittenColumns = state.Main.RowWrittenColumns,
            TextSizing = state.TextSizing
        };
        target.ApplySnapshot(checkpoint);
        target.Lock.EnterReadLock();
        try
        {
            var piece = Assert.IsType<CellTextSizing>(target.GetTextSizingAbsolute(48, (alternate ? 0 : target.Scrollback.Count) + 6));
            Assert.Equal(Payload, piece.Block.Text);
            Assert.Equal(Options, piece.Block.Options);
        }
        finally { target.Lock.ExitReadLock(); }
        var exported = target.ExportState(int.MaxValue);
        int restoredBlocks = exported.TextSizing!.Count;
        // The actual retained cells must remain within budget, not just the accounting field.
        var roundTrip = new TerminalBuffer(49, 7);
        roundTrip.ImportState(exported);
        Assert.Equal(alternate, roundTrip.IsAltScreenActive);
        Assert.True(restoredBlocks >= previousBlocks, "reclaim only enough old history to restore the checkpoint");
        if (retainedBlocks == 1) Assert.Equal(2, restoredBlocks);
    }
}
