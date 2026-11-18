using Avalonia.Headless.XUnit;
using System;
using Xunit;

namespace Ntilde.Tests.RenderTests;

/// <summary>
/// The oldest history row must draw. Its snapshot id used to be 0 while nothing had been evicted, which is the id the
/// draw path reads as "no row here", so the top of a fully scrolled-back view stayed blank.
/// </summary>
public sealed class ScrollbackFirstRowRenderTests
{
    private const int Cols = 20;
    private const int Rows = 4;

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Oldest_history_row_draws_when_nothing_has_been_evicted(bool withRowCache)
    {
        using var harness = new DrawPathHarness(Cols, Rows, withRowCache);
        harness.Parser.Process("MMMM\r\n\r\n\r\n\r\n");
        Assert.Equal(1, harness.Buffer.Scrollback.Count);
        Assert.Equal(0, harness.Buffer.Scrollback.TotalRowsEvicted);

        harness.ScrollOffset = 1;
        harness.DrawFrame();
        AssertTopRowHasInk(harness.Pixels());

        // A second frame is the one a row picture cache serves.
        harness.DrawFrame();
        AssertTopRowHasInk(harness.Pixels());
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Second_history_row_draws_as_the_first_does(bool withRowCache)
    {
        using var harness = new DrawPathHarness(Cols, Rows, withRowCache);
        harness.Parser.Process("\r\nMMMM\r\n\r\n\r\n\r\n");
        Assert.Equal(2, harness.Buffer.Scrollback.Count);

        harness.ScrollOffset = 1;
        harness.DrawFrame();
        AssertTopRowHasInk(harness.Pixels());
    }

    /// <summary>The top row's band differs from the blank row's band under it.</summary>
    private static void AssertTopRowHasInk(byte[] pixels)
    {
        int cellHeight = (int)DrawPathHarness.Metrics.CellHeight;
        int stride = pixels.Length / (Rows * cellHeight);
        ReadOnlySpan<byte> top = pixels.AsSpan(0, stride * cellHeight);
        ReadOnlySpan<byte> blank = pixels.AsSpan(stride * cellHeight, stride * cellHeight);
        Assert.False(top.SequenceEqual(blank), "the first history row drew as a blank row");
    }
}
