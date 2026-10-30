using Avalonia.Headless.XUnit;
using Ntilde.Tests.Infra;
using Ntilde.Tests.RenderTests;
using Ntilde.VT;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;

namespace Ntilde.Tests.Performance;

/// <summary>
/// #127: frame times of the draw path and how parsing holds up under rendering, run nightly (Category=Stress, the
/// Nightly Tab Stress job) because time, unlike allocation, varies with the runner. Each writes its numbers to the
/// metrics artifacts, so a trend shows before a budget fails.
/// </summary>
/// <remarks>
/// The budgets are deliberately loose: a frame must fit one 60 Hz interval (16 ms) at the 95th percentile, where the
/// draw path takes 1 to 5 ms on a desktop. Tighten them once the nightly numbers have settled.
/// </remarks>
[Trait("Category", "Stress")]
[Collection("RenderBenchmarks")]
public class RenderFrameTimeBenchmarks
{
    private const int Cols = 120;
    private const int Rows = 40;
    private const double VsyncIntervalMs = 1000.0 / 60;

    private static double[] Time(string scenario, bool withRowCache, Func<DrawPathHarness, Action> change, int frames = 300)
    {
        using var harness = new DrawPathHarness(Cols, Rows, withRowCache);
        harness.FillWithTypicalOutput();
        Action beforeFrame = change(harness);
        for (int i = 0; i < 20; i++)
        {
            beforeFrame();
            harness.DrawFrame();
        }

        double[] times = harness.FrameTimesMs(beforeFrame, frames);
        MetricsArtifactWriter.WriteReport(
            $"frame-time-{scenario}",
            string.Create(
                CultureInfo.InvariantCulture,
                $"{scenario} ({Cols}x{Rows}, {frames} frames): p50 {DrawPathHarness.Percentile(times, 50):F2} ms, p95 {DrawPathHarness.Percentile(times, 95):F2} ms, max {times[^1]:F2} ms"));
        return times;
    }

    private static void AssertP95Within(double[] times, double budgetMs, string what)
    {
        double p95 = DrawPathHarness.Percentile(times, 95);
        Assert.True(p95 <= budgetMs, string.Create(CultureInfo.InvariantCulture, $"{what}: p95 {p95:F2} ms, over the {budgetMs:F1} ms budget (#127)"));
    }

    [AvaloniaFact]
    public void FullRedraws_FitAVsyncInterval()
    {
        AssertP95Within(Time("full-redraw", withRowCache: false, _ => () => { }), VsyncIntervalMs, "a full redraw");
    }

    [AvaloniaFact]
    public void ScrollingFrames_FitAVsyncInterval()
    {
        int line = 0;
        double[] times = Time("scrolling", withRowCache: true, h => () => h.Parser.Process("\r\n" + DrawPathHarness.TypicalLine(line++)));

        AssertP95Within(times, VsyncIntervalMs, "output scrolling a line per frame");
    }

    [AvaloniaFact]
    public void CachedFrames_TakeAFractionOfAVsyncInterval()
    {
        AssertP95Within(Time("cached", withRowCache: true, _ => () => { }), VsyncIntervalMs / 2, "an unchanged screen");
    }

    [AvaloniaFact]
    public void Parsing_KeepsMostOfItsThroughput_UnderA60HzRenderLoop()
    {
        // Rendering takes the buffer's read lock per frame and parsing its write lock per chunk: neither may starve the
        // other. Output arrives in PTY-sized chunks while a render thread draws at 60 Hz, as the view's timer does.
        //
        // Parsing loses nothing to rendering. Rendering does lose to parsing: the parser re-takes the write lock
        // back to back, and the loop draws about 9 frames a second during the flood where it could draw about 48
        // (#658). The frame-rate floor catches rendering stopping altogether; raise it once #658 is fixed.
        byte[][] chunks = OutputChunks(megabytes: 8, chunkBytes: 16 * 1024);

        TimeSpan alone = ParseAll(chunks, renderer: null, out _);
        using var harness = new DrawPathHarness(Cols, Rows);
        TimeSpan underRender = ParseAll(chunks, harness, out int frames);

        double throughputRatio = alone.TotalMilliseconds / underRender.TotalMilliseconds;
        double framesPerSecond = frames / underRender.TotalSeconds;
        MetricsArtifactWriter.WriteReport(
            "parse-under-render",
            string.Create(
                CultureInfo.InvariantCulture,
                $"8 MB in 16 KB chunks: alone {alone.TotalMilliseconds:F0} ms, under a 60 Hz render loop {underRender.TotalMilliseconds:F0} ms ({throughputRatio:P0} of the throughput), {frames} frames ({framesPerSecond:F1} fps)"));

        Assert.True(throughputRatio >= 0.5, string.Create(CultureInfo.InvariantCulture, $"parsing kept {throughputRatio:P0} of its throughput under rendering (#127)"));
        Assert.True(framesPerSecond >= 4, string.Create(CultureInfo.InvariantCulture, $"the render loop drew {framesPerSecond:F1} frames a second during the flood: parsing starved it (#127, #658)"));
    }

    private static byte[][] OutputChunks(int megabytes, int chunkBytes)
    {
        var text = new StringBuilder();
        for (int i = 0; text.Length < megabytes * 1024 * 1024; i++)
        {
            text.Append(DrawPathHarness.TypicalLine(i)).Append("\r\n");
        }

        byte[] bytes = Encoding.UTF8.GetBytes(text.ToString());
        int count = (bytes.Length + chunkBytes - 1) / chunkBytes;
        var chunks = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            chunks[i] = bytes.AsSpan(i * chunkBytes, Math.Min(chunkBytes, bytes.Length - (i * chunkBytes))).ToArray();
        }

        return chunks;
    }

    private static TimeSpan ParseAll(byte[][] chunks, DrawPathHarness? renderer, out int frames)
    {
        TerminalBuffer buffer = renderer?.Buffer ?? new TerminalBuffer(Cols, Rows);
        AnsiParser parser = renderer?.Parser ?? new AnsiParser(buffer);
        using var stop = new CancellationTokenSource();
        int drawn = 0;
        Thread? renderThread = null;
        if (renderer is not null)
        {
            renderThread = new Thread(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    renderer.DrawFrame();
                    Interlocked.Increment(ref drawn);
                    Thread.Sleep(TimeSpan.FromMilliseconds(VsyncIntervalMs));
                }
            })
            { IsBackground = true, Name = "BenchmarkRender" };
        }

        // The window opens before the render thread starts, and the count is read when it closes: only frames drawn
        // inside it count, neither one done before parsing began nor one finished after it ended (Codex review of #659).
        var clock = Stopwatch.StartNew();
        renderThread?.Start();
        var decoder = Encoding.UTF8.GetDecoder();
        var chars = new char[Encoding.UTF8.GetMaxCharCount(chunks[0].Length)];
        foreach (byte[] chunk in chunks)
        {
            int count = decoder.GetChars(chunk, chars, flush: false);
            parser.Process(new string(chars, 0, count));
        }

        clock.Stop();
        // A frame blocked through the whole flood finishes after the window; counted, it would let a starved loop pass.
        frames = Volatile.Read(ref drawn);
        stop.Cancel();
        renderThread?.Join();
        return clock.Elapsed;
    }
}
