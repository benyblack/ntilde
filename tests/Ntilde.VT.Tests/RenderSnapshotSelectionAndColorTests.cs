using Ntilde.VT;

namespace Ntilde.VT.Tests;

public sealed class RenderSnapshotSelectionAndColorTests
{
    private static string Osc(string value) => "\x1b]" + value + "\x1b\\";

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Relative_selection_uses_the_captured_viewport_and_does_not_mutate_its_input(int scrollOffset)
    {
        var buffer = new TerminalBuffer(12, 3);
        new AnsiParser(buffer).Process("zero\r\none\r\ntwo\r\nthree\r\nfour\r\nfive");
        var selection = new SelectionState { IsActive = true, Start = (0, 1), End = (1, 2) };
        var request = new RenderSnapshotRequest
        {
            ViewportRows = 3,
            ViewportCols = 12,
            ScrollOffset = scrollOffset,
            Isolated = true,
            Selection = selection,
            SelectionRelativeToViewport = true,
        };
        using var snapshot = buffer.CaptureRenderSnapshot(request, out _);
        Assert.True(snapshot.AbsDisplayStart > 0);
        Assert.Equal(2, snapshot.SelectionRows.Length);
        Assert.Equal(snapshot.AbsDisplayStart, snapshot.SelectionRows.Array[0].AbsRow);
        Assert.Equal(1, snapshot.SelectionRows.Array[0].ColStart);
        Assert.Equal(11, snapshot.SelectionRows.Array[0].ColEnd);
        Assert.Equal(snapshot.AbsDisplayStart + 1, snapshot.SelectionRows.Array[1].AbsRow);
        Assert.Equal(0, snapshot.SelectionRows.Array[1].ColStart);
        Assert.Equal(2, snapshot.SelectionRows.Array[1].ColEnd);
        Assert.Equal((0, 1), selection.Start);
        Assert.Equal((1, 2), selection.End);
        using var absolute = buffer.CaptureRenderSnapshot(request with { SelectionRelativeToViewport = false }, out _);
        Assert.Equal(0, absolute.SelectionRows.Length);
    }

    [Fact]
    public void Theme_snapshot_preserves_nullable_values_and_flags_even_for_equal_color_overrides()
    {
        var host = new TerminalTheme { SelectionBackground = TermColor.FromRgb(23, 91, 173) };
        var buffer = new TerminalBuffer(12, 3) { Theme = host };
        var request = new RenderSnapshotRequest { ViewportRows = 3, ViewportCols = 12, Isolated = true };
        using var before = buffer.CaptureRenderSnapshot(request, out _);
        Assert.Equal(host.SelectionBackground, before.Theme.SelectionBackground);
        Assert.Null(before.Theme.SelectionForeground);
        Assert.False(before.Theme.HasSelectionBackgroundOverride);
        new AnsiParser(buffer).Process(Osc("17;#175bad") + Osc("19;#f0e0d0") + Osc("12;#123456"));
        using var after = buffer.CaptureRenderSnapshot(request, out _);
        Assert.Equal(before.Theme.SelectionBackground, after.Theme.SelectionBackground);
        Assert.True(after.Theme.HasSelectionBackgroundOverride);
        Assert.True(after.Theme.HasSelectionForegroundOverride);
        Assert.True(after.Theme.HasCursorOverride);
        Assert.Equal(0xfff0e0d0U, after.Theme.SelectionForeground!.Value.ToUint());
        Assert.Same(host, buffer.Theme);
        Assert.Null(host.SelectionForeground);
    }

    [Fact]
    public void Runtime_resets_restore_host_values_and_clear_all_snapshot_override_evidence()
    {
        var host = new TerminalTheme { SelectionBackground = TermColor.FromRgb(1, 2, 3) };
        var buffer = new TerminalBuffer(12, 3) { Theme = host };
        var parser = new AnsiParser(buffer);
        var request = new RenderSnapshotRequest { ViewportRows = 3, ViewportCols = 12, Isolated = true };
        using var before = buffer.CaptureRenderSnapshot(request, out _);
        parser.Process(Osc("10;#102030;#405060;#708090") + Osc("17;#a0b0c0") + Osc("19;#d0e0f0") + Osc("4;1;#123456;200;#abcdef"));
        using var changed = buffer.CaptureRenderSnapshot(request, out _);
        Assert.True(changed.Theme.HasForegroundOverride);
        Assert.True(changed.Theme.HasBackgroundOverride);
        Assert.True(changed.Theme.HasCursorOverride);
        Assert.True(changed.Theme.PaletteOverrides.Contains(1));
        Assert.True(changed.Theme.PaletteOverrides.Contains(200));
        parser.Process(Osc("104") + Osc("110") + Osc("111") + Osc("112") + Osc("117") + Osc("119"));
        using var restored = buffer.CaptureRenderSnapshot(request, out _);
        Assert.Equal(before.Theme.Foreground, restored.Theme.Foreground);
        Assert.Equal(before.Theme.Background, restored.Theme.Background);
        Assert.Equal(before.Theme.CursorColor, restored.Theme.CursorColor);
        Assert.Equal(before.Theme.SelectionBackground, restored.Theme.SelectionBackground);
        Assert.Equal(before.Theme.SelectionForeground, restored.Theme.SelectionForeground);
        Assert.Equal(before.Theme.AnsiPalette, restored.Theme.AnsiPalette);
        Assert.False(restored.Theme.HasForegroundOverride);
        Assert.False(restored.Theme.HasBackgroundOverride);
        Assert.False(restored.Theme.HasCursorOverride);
        Assert.False(restored.Theme.HasSelectionBackgroundOverride);
        Assert.False(restored.Theme.HasSelectionForegroundOverride);
        Assert.Equal(default, restored.Theme.PaletteOverrides);
        Assert.Same(host, buffer.Theme);
        // Earlier snapshots retain their value evidence after subsequent resets.
        Assert.True(changed.Theme.HasSelectionForegroundOverride);
        Assert.Equal(0xffd0e0f0U, changed.Theme.SelectionForeground!.Value.ToUint());
    }
}
