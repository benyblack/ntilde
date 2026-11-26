using System.Text;
using Ntilde.Replay;
using Ntilde.VT;

namespace Ntilde.VT.Tests;

public sealed class SixelSessionPolicyTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(int.MaxValue)]
    public void Unsupported_creation_policy_cannot_allocate_a_buffer(int policy)
        => Assert.Throws<InvalidOperationException>(() => new TerminalBuffer(10, 3, policy));

    [Theory]
    [InlineData("\u001bc", true)]
    [InlineData("\x1b[!p", false)]
    [InlineData("\x1b[?1049h\u001bc", true)]
    public void Legacy_creation_policy_is_immutable_across_resets_and_restore(string reset, bool clearsScreen)
    {
        var buffer = new TerminalBuffer(10, 3, 0);
        var parser = new AnsiParser(buffer);
        parser.Process("before\u001b[?25l" + reset);
        Assert.Equal(clearsScreen ? "" : "before", BufferSnapshot.Capture(buffer).Lines[0]);
        Assert.True(buffer.Modes.IsCursorVisible);
        Assert.Equal(0, buffer.SixelRasterPolicyVersion);
        Assert.False(typeof(TerminalBuffer).GetProperty(nameof(TerminalBuffer.SixelRasterPolicyVersion))!.CanWrite);
        var state = buffer.ExportState(0);
        string json = Encoding.UTF8.GetString(TerminalStateSerializer.ToBytes(state));
        Assert.DoesNotContain("required_sixel_raster", json);
        var explicitZero = TerminalStateSerializer.FromBytes(Encoding.UTF8.GetBytes("{\"required_sixel_raster\":0," + json[1..]));
        Assert.Equal(TerminalStateSerializer.ToBytes(state), TerminalStateSerializer.ToBytes(explicitZero));
        TerminalStateTransfer.Restore(buffer, parser, explicitZero);
        Assert.Equal(0, buffer.SixelRasterPolicyVersion);
    }

    [Theory]
    [InlineData("1,\"required_sixel_raster\":0")]
    [InlineData("0,\"required_sixel_raster\":1")]
    [InlineData("0,\"required_sixel_raster\":0")]
    public void Duplicate_snapshot_policy_cannot_be_hidden_by_last_key_wins(string values)
    {
        string json = Encoding.UTF8.GetString(TerminalStateSerializer.ToBytes(new TerminalBuffer(10, 3).ExportState(0)));
        Assert.Throws<System.Text.Json.JsonException>(() => TerminalStateSerializer.FromBytes(
            Encoding.UTF8.GetBytes("{\"required_sixel_raster\":" + values + "," + json[1..])));
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(1, false)]
    [InlineData(int.MaxValue, false)]
    [InlineData(-1, true)]
    [InlineData(1, true)]
    [InlineData(int.MaxValue, true)]
    public void Unsupported_snapshot_policy_fails_before_buffer_or_parser_mutation(int policy, bool complete)
    {
        var source = new TerminalBuffer(30, 8).ExportState(0);
        string json = Encoding.UTF8.GetString(TerminalStateSerializer.ToBytes(source));
        var snapshot = TerminalStateSerializer.FromBytes(Encoding.UTF8.GetBytes("{\"required_sixel_raster\":" + policy + "," + json[1..]));
        var target = new TerminalBuffer(10, 3);
        var parser = new AnsiParser(target);
        parser.Process("keep\x1b[?25l\x1b[31;");
        byte[] before = Pair(target, parser);
        Assert.Throws<InvalidOperationException>(() =>
        {
            if (complete) TerminalStateTransfer.Restore(target, parser, snapshot);
            else target.ImportState(snapshot);
        });
        Assert.Equal(before, Pair(target, parser));
    }

    private static byte[] Pair(TerminalBuffer buffer, AnsiParser parser)
    {
        var state = buffer.ExportState(0);
        state.Parser = parser.ExportState();
        return TerminalStateSerializer.ToBytes(state);
    }

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
