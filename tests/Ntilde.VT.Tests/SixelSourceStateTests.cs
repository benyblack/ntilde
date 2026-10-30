using Ntilde.VT;

namespace Ntilde.VT.Tests;

public sealed class SixelSourceStateTests
{
    [Fact]
    public void Repeated_fixed_cursor_images_keep_headless_placements_bounded_and_latest_restorable()
    {
        var buffer = new TerminalBuffer(10, 6) { SixelSources = new SixelSourceBudget(1024, 4) };
        var parser = new AnsiParser(buffer) { RetainSixelSources = true };
        for (int i = 0; i < 1000; i++) parser.Process($"\x1b[1;1H\x1bPq!{i % 9 + 1}~\x1b\\");
        Assert.Equal(6, buffer.TotalLines); // no scrolling to hide a placement leak
        Assert.InRange(buffer.Images.Count, 1, 4);
        Assert.Equal("q!1~", buffer.Images[^1].SixelSource?.Data);
        var restored = new TerminalBuffer(10, 6);
        restored.ImportState(buffer.ExportState(0));
        Assert.Equal(buffer.Images.Count, restored.Images.Count);
        Assert.Equal("q!1~", restored.Images[^1].SixelSource?.Data);
        var retired = new List<object>();
        Assert.False(buffer.DrainRetiredImageHandles(retired, long.MinValue)); // headless sources release immediately
    }

    [Fact]
    public void Source_budget_evicts_oldest_and_releases_disposed_tokens_without_retaining_oversized_input()
    {
        var budget = new SixelSourceBudget(7, 2);
        var first = budget.Retain("abc");
        var second = budget.Retain("de");
        var last = budget.Retain("fgh");
        Assert.Null(first.Data);
        Assert.Equal(5, budget.RetainedChars);
        Assert.Equal(2, budget.Count);
        Assert.Null(budget.Retain("oversized").Data);
        Assert.Equal("fgh", last.Data);
        second.Dispose();
        second.Dispose();
        Assert.Equal(3, budget.RetainedChars);
        last.Dispose();
        Assert.Equal(0, budget.Count);
        Assert.Equal(0, budget.RetainedChars);
    }

    [Fact]
    public void Source_count_and_concurrent_releases_remain_bounded()
    {
        var budget = new SixelSourceBudget(1000, 2);
        var first = budget.Retain("q~");
        budget.Retain("q~");
        budget.Retain("q~");
        Assert.Null(first.Data);
        Parallel.For(0, 1000, _ => budget.Retain("q~").Dispose());
        Assert.InRange(budget.Count, 0, 2);
        Assert.InRange(budget.RetainedChars, 0, 4);
    }

    [Fact]
    public void Retained_headless_sources_roundtrip_both_screens_without_replaying_cursor_movement()
    {
        var buffer = new TerminalBuffer(10, 6);
        var parser = new AnsiParser(buffer) { RetainSixelSources = true };
        parser.Process("\x1b[?80h\x1b[3;5H\x1bPq#1;2;100;0;0#1!30~\x1b\\\x1b[?47h\x1bPq#1;2;0;0;100#1!10~\x1b\\");
        var snapshot = buffer.ExportState(0);
        snapshot.Parser = parser.ExportState();
        byte[] bytes = TerminalStateSerializer.ToBytes(snapshot);
        var restored = new TerminalBuffer(20, 3);
        var restoredParser = new AnsiParser(restored) { RetainSixelSources = true };
        TerminalStateTransfer.Restore(restored, restoredParser, TerminalStateSerializer.FromBytes(bytes));
        Assert.Equal(2, restored.Images.Count);
        Assert.All(restored.Images, image => Assert.IsType<SixelSource>(image.ImageHandle));
        Assert.Equal((buffer.CursorCol, buffer.CursorRow), (restored.CursorCol, restored.CursorRow));
        var again = restored.ExportState(0);
        again.Parser = restoredParser.ExportState();
        Assert.Equal(bytes, TerminalStateSerializer.ToBytes(again));
    }

    [Fact]
    public void Snapshot_scrollback_truncation_shifts_and_clips_retained_image_anchors()
    {
        var buffer = new TerminalBuffer(10, 3);
        var parser = new AnsiParser(buffer) { RetainSixelSources = true, SixelCellSizeOverride = new SixelCellSize(10, 6) };
        parser.Process("\x1b[3;1H\x1bPq!30~-~\x1b\\x\r\n");
        var snapshot = buffer.ExportState(0);
        var image = Assert.Single(snapshot.Sixels!);
        Assert.Equal(-1, image.Y);
        Assert.Equal(1, image.ClipTop);
        var restored = new TerminalBuffer(10, 3);
        TerminalStateTransfer.Restore(restored, new AnsiParser(restored), snapshot);
        var imported = Assert.Single(restored.Images);
        Assert.Equal((-1, 1), (imported.CellY, imported.ClipTopRows));
        Assert.Equal((buffer.CursorCol, buffer.CursorRow), (restored.CursorCol, restored.CursorRow));
    }

    [Fact]
    public void Oversized_snapshot_sources_and_excess_image_counts_are_clamped_before_retention()
    {
        var snapshot = new TerminalBuffer(10, 3).ExportState(0);
        snapshot.Sixels = Enumerable.Range(0, 300).Select(_ => new TerminalSixelImageState
        {
            Source = "q~",
            Width = 1,
            Height = 1,
        }).ToList();
        snapshot.Sixels.Add(new TerminalSixelImageState { Source = new string('?', SixelSourceBudget.DefaultMaxChars + 1), Width = 1, Height = 1 });
        var buffer = new TerminalBuffer(10, 3);
        TerminalStateTransfer.Restore(buffer, new AnsiParser(buffer), snapshot);
        Assert.Equal(SixelSourceBudget.DefaultMaxSources - 1, buffer.Images.Count);
        Assert.InRange(buffer.SixelSources.RetainedChars, 1, SixelSourceBudget.DefaultMaxChars);
        Assert.All(buffer.Images, image => Assert.Equal("q~", image.SixelSource?.Data));
        var smaller = new TerminalBuffer(10, 3) { SixelSources = new SixelSourceBudget(100, 4) };
        TerminalStateTransfer.Restore(smaller, new AnsiParser(smaller), snapshot);
        Assert.Equal(4, smaller.Images.Count);
    }

    [Theory]
    [InlineData("$q~")]
    [InlineData("")]
    [InlineData("q")]
    public void Malformed_snapshot_sources_are_rejected_before_any_buffer_mutation(string source)
    {
        var snapshot = new TerminalBuffer(30, 10).ExportState(0);
        snapshot.Sixels = [new TerminalSixelImageState { Source = source, Width = 1, Height = 1 }];
        var buffer = new TerminalBuffer(10, 3);
        var parser = new AnsiParser(buffer);
        parser.Process("preserved");
        byte[] before = TerminalStateSerializer.ToBytes(buffer.ExportState(0));
        Assert.Throws<InvalidOperationException>(() => TerminalStateTransfer.Restore(buffer, parser, snapshot));
        Assert.Equal(before, TerminalStateSerializer.ToBytes(buffer.ExportState(0)));
    }
}
