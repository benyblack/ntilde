namespace Ntilde.VT.Tests;

public sealed class NativeSixelMissingMetricsTests
{
    [Theory]
    [InlineData("x")]
    [InlineData("\x1bPq~\x1b\\")]
    public void Native_input_without_an_explicit_pair_is_refused_before_stream_or_buffer_effects(string input)
    {
        var budget = new NativeSixelBudget();
        var buffer = TerminalBuffer.CreateNativeSixelTestBuffer(10, 6, budget, null);
        var parser = new AnsiParser(buffer, false);
        Assert.Throws<InvalidOperationException>(() => parser.Process(input));
        Assert.Equal((0, 0), (buffer.CursorCol, buffer.CursorRow));
        Assert.Empty(buffer.Images);
        Assert.Equal((0, 0L), budget.Usage);
        Assert.Equal(0, buffer.SixelSources.Count);
        parser.SixelCellSizeOverride = new(10, 20);
        parser.Process("X");
        buffer.Lock.EnterReadLock();
        try { Assert.Equal('X', buffer.GetCell(0, 0).Character); }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Fact]
    public void Native_buffer_export_without_an_explicit_pair_is_unavailable()
    {
        var buffer = TerminalBuffer.CreateNativeSixelTestBuffer(10, 6, new NativeSixelBudget(), null);
        Assert.Throws<InvalidOperationException>(() => buffer.ExportState(100));
        Assert.Null(buffer.NativeSixelCellMetrics);
    }

    [Fact]
    public void Native_parser_export_without_an_explicit_pair_is_unavailable()
    {
        var buffer = TerminalBuffer.CreateNativeSixelTestBuffer(10, 6, new NativeSixelBudget(), null);
        Assert.Throws<InvalidOperationException>(() => new AnsiParser(buffer, false).ExportState());
        Assert.Null(buffer.NativeSixelCellMetrics);
    }

    [Fact]
    public void Missing_incoming_native_parser_pair_is_refused_before_parser_mutation()
    {
        var buffer = TerminalBuffer.CreateNativeSixelTestBuffer(10, 6, new NativeSixelBudget(), null);
        var parser = new AnsiParser(buffer, false) { SixelCellSizeOverride = new(10, 20) };
        string before = System.Text.Json.JsonSerializer.Serialize(parser.ExportState());
        var state = parser.ExportState();
        state.State = 1;
        state.SixelCellWidthPx = state.SixelCellHeightPx = null;
        Assert.Throws<InvalidOperationException>(() => parser.ImportState(state));
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(parser.ExportState()));
        Assert.Equal(new SixelCellSize(10, 20), buffer.NativeSixelCellMetrics);
    }

    [Fact]
    public void Valid_incoming_native_state_initializes_the_empty_targets_explicit_pair()
    {
        var source = TerminalBuffer.CreateNativeSixelTestBuffer(10, 6, new NativeSixelBudget(), null);
        var producer = new AnsiParser(source, false) { SixelCellSizeOverride = new(13, 27) };
        producer.Process("\x1b[?80h\x1bPq~\x1b\\");
        var state = source.ExportState(0);
        state.Parser = producer.ExportState();
        var target = TerminalBuffer.CreateNativeSixelTestBuffer(10, 6, new NativeSixelBudget(), null);
        Assert.Null(target.NativeSixelCellMetrics);
        var parser = new AnsiParser(target, false);
        TerminalStateTransfer.Restore(target, parser, state);
        Assert.Equal(new SixelCellSize(13, 27), target.NativeSixelCellMetrics);
        parser.Process("X");
        target.Lock.EnterReadLock();
        try { Assert.Equal('X', target.GetCell(0, 0).Character); }
        finally { target.Lock.ExitReadLock(); }
        target.ClearImages();
        source.ClearImages();
    }

    [Fact]
    public void Legacy_null_metrics_keep_the_existing_parser_and_state_behavior()
    {
        var buffer = new TerminalBuffer(10, 6);
        var parser = new AnsiParser(buffer, false);
        parser.Process("legacy");
        Assert.Null(parser.ExportState().SixelCellWidthPx);
        Assert.Null(buffer.ExportState(0).NativeSixelCellMetrics);
        buffer.Lock.EnterReadLock();
        try { Assert.Equal('l', buffer.GetCell(0, 0).Character); }
        finally { buffer.Lock.ExitReadLock(); }
    }
}
