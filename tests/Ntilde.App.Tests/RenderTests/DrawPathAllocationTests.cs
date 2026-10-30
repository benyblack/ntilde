using Avalonia.Headless.XUnit;
using Ntilde.Tests.Infra;
using System;
using System.Globalization;

namespace Ntilde.Tests.RenderTests;

/// <summary>
/// #127: what the draw path allocates per frame, gated on every PR. Allocation, unlike time, is the same on every
/// run, so a budget here cannot flake; frame times are gated nightly (RenderFrameTimeBenchmarks).
/// </summary>
/// <remarks>
/// <para>
/// A 120x40 screen of typical shell output, drawn as a view draws it (<see cref="DrawPathHarness"/>). Each budget is
/// about one and a half to two times what the path allocates today, and each sits well below what the path allocates
/// when the cache that keeps it there is defeated, so a regression of that kind fails here rather than passing CI.
/// </para>
/// <para>
/// Measured on Windows when written (bytes per frame): unchanged 23,040, typing 46,029, scrolling 497,228; without the
/// row cache (the alternate screen, where vim and less draw) unchanged 164,096.
/// </para>
/// </remarks>
[Trait("Category", "RenderMetrics")]
public class DrawPathAllocationTests
{
    private const int Cols = 120;
    private const int Rows = 40;
    private const int WarmupFrames = 10;
    private const int MeasuredFrames = 100;

    private static double Measure(string scenario, bool withRowCache, Func<DrawPathHarness, Action> change)
    {
        using var harness = new DrawPathHarness(Cols, Rows, withRowCache);
        harness.FillWithTypicalOutput();
        Action beforeFrame = change(harness);
        for (int i = 0; i < WarmupFrames; i++)
        {
            beforeFrame();
            harness.DrawFrame();
        }

        double bytes = harness.AllocatedBytesPerFrame(beforeFrame, MeasuredFrames);
        MetricsArtifactWriter.WriteReport(
            $"draw-path-allocation-{scenario}",
            string.Create(CultureInfo.InvariantCulture, $"{scenario}: {bytes:F0} bytes per frame ({Cols}x{Rows}, {MeasuredFrames} frames)"));
        return bytes;
    }

    private static void AssertWithin(double bytes, int budget, string what)
        => Assert.True(bytes <= budget, string.Create(CultureInfo.InvariantCulture, $"{what}: {bytes:F0} bytes per frame, over the {budget} budget (#127)"));

    [AvaloniaFact]
    public void UnchangedFrames_AreServedFromTheRowCache_WithinBudget()
    {
        // Without the row cache the same frame allocates about 164 KB: a cache that stops hitting fails here.
        double bytes = Measure("unchanged", withRowCache: true, _ => () => { });

        AssertWithin(bytes, 48 * 1024, "an unchanged screen");
    }

    [AvaloniaFact]
    public void TypingFrames_RedrawOneRow_WithinBudget()
    {
        double bytes = Measure("typing", withRowCache: true, h => () => h.Parser.Process("x"));

        AssertWithin(bytes, 96 * 1024, "one row changing per frame");
    }

    [AvaloniaFact]
    public void ScrollingFrames_StayWithinBudget()
    {
        // Every row misses the row cache while output scrolls (ScrollUp touches every row's revision, #657), so this
        // is a full redraw plus the recording of 40 row pictures; the budget holds that from growing further. Tighten
        // it once #657 lands.
        int line = 0;
        double bytes = Measure("scrolling", withRowCache: true, h => () => h.Parser.Process("\r\n" + DrawPathHarness.TypicalLine(line++)));

        AssertWithin(bytes, 768 * 1024, "output scrolling a line per frame");
    }

    [AvaloniaFact]
    public void FramesWithoutTheRowCache_StayWithinBudget()
    {
        // The alternate screen draws without the row cache, every row every frame.
        double bytes = Measure("no-row-cache", withRowCache: false, _ => () => { });

        AssertWithin(bytes, 256 * 1024, "a full redraw");
    }
}
