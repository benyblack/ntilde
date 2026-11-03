using Avalonia.Headless.XUnit;
using Ntilde.Rendering;
using Ntilde.Shell;
using Ntilde.VT;
using SkiaSharp;

namespace Ntilde.Tests.RenderTests;

public sealed class TextBlinkRenderTests
{
    private static readonly CellMetrics Metrics = new()
    {
        CellWidth = 8.4f,
        CellHeight = 18,
        Baseline = 14,
        Ascent = 14,
        Descent = 4,
    };

    private static SKBitmap Render(string sequence, bool visible)
    {
        var buffer = new TerminalBuffer(20, 3)
        {
            Theme = new TerminalTheme { Foreground = TermColor.White, Background = TermColor.Black },
        };
        new AnsiParser(buffer).Process(sequence);
        return Render(buffer, visible);
    }

    private static SKBitmap Render(TerminalBuffer buffer, bool visible, RowImageCache? cache = null)
        => TerminalSnapshotRenderer.Capture(buffer, Metrics, 176, 54, new TerminalSnapshotOptions
        {
            HideCursor = true,
            FillBackground = true,
            TextBlinkVisible = visible,
            RowCache = cache,
            RenderScaling = 2,
        });

    private static byte[] Region(SKBitmap bitmap, int firstColumn, int endColumn)
    {
        var result = new System.Collections.Generic.List<byte>();
        for (int y = 0; y < 36; y++)
            for (int x = (int)((4 + firstColumn * Metrics.CellWidth) * 2); x < (int)((4 + endColumn * Metrics.CellWidth) * 2); x++)
            {
                var color = bitmap.GetPixel(x, y);
                result.Add(color.Red);
                result.Add(color.Green);
                result.Add(color.Blue);
                result.Add(color.Alpha);
            }
        return result.ToArray();
    }

    [AvaloniaFact]
    public void HiddenPhase_RemovesOnlyBlinkingForeground()
    {
        const string sequence = " \x1b[5mMMMM\x1b[25m    ZZZZ";
        using var visible = Render(sequence, true);
        using var hidden = Render(sequence, false);
        Assert.NotEqual(Region(visible, 1, 5), Region(hidden, 1, 5));
        Assert.Equal(Region(visible, 9, 13), Region(hidden, 9, 13));
    }

    [AvaloniaFact]
    public void HiddenPhase_KeepsTheExplicitBackground()
    {
        using var hidden = Render(" \x1b[5;41mMMMM", false);
        using var background = Render(" \x1b[41m    ", true);
        Assert.Equal(Region(background, 1, 5), Region(hidden, 1, 5));
    }

    [AvaloniaFact]
    public void CachedRows_RepaintBothPhasesWithoutChangingTheBuffer()
    {
        var buffer = new TerminalBuffer(20, 3)
        {
            Theme = new TerminalTheme { Foreground = TermColor.White, Background = TermColor.Black },
        };
        new AnsiParser(buffer).Process(" \x1b[5mMMMM\x1b[25m    ZZZZ");
        using var cache = new RowImageCache();
        using var first = Render(buffer, true, cache);
        using var hidden = Render(buffer, false, cache);
        using var again = Render(buffer, true, cache);
        Assert.NotEqual(Region(first, 1, 5), Region(hidden, 1, 5));
        Assert.Equal(first.Bytes, again.Bytes);
        Assert.Equal(Region(first, 9, 13), Region(hidden, 9, 13));
    }

    [AvaloniaTheory]
    [InlineData("normal", false)]
    [InlineData("\x1b[5m    ", false)]
    [InlineData("\x1b[5;8mhidden", false)]
    [InlineData("\x1b[5;4m    ", false)]
    [InlineData("\x1b[5mtext", true)]
    [InlineData("\x1b[5;53m    ", true)]
    [InlineData("\x1b[5;4:3m    ", true)]
    [InlineData("\x1b[5;9m    ", true)]
    public void AnimationFeedback_OnlyEnablesTheClockForBlinkingInk(string sequence, bool expected)
    {
        using var harness = new DrawPathHarness(20, 3);
        harness.Parser.Process(sequence);
        bool? observed = null;
        harness.DrawFrame(reportTextBlinkCells: value => observed = value);
        Assert.Equal(expected, observed);
    }

    [AvaloniaFact]
    public void LiveCachePhaseChanges_DoNotRequireVtDamage()
    {
        using var harness = new DrawPathHarness(20, 3);
        harness.Parser.Process("\x1b[5mMMMM\x1b[25m  ZZ");
        harness.DrawFrame();
        var first = harness.Pixels();
        harness.DrawFrame(textBlinkVisible: false);
        Assert.NotEqual(first, harness.Pixels());
        harness.DrawFrame(textBlinkVisible: true);
        Assert.Equal(first, harness.Pixels());
        harness.Parser.Process("\r\x1b[2K\x1b[5m   ");
        bool? observed = null;
        harness.DrawFrame(reportTextBlinkCells: value => observed = value);
        Assert.False(observed);
    }

    [AvaloniaTheory]
    [InlineData("\x1b[5;4:3;58;5;9m")]
    [InlineData("\x1b[5;53m")]
    [InlineData("\x1b[5;9m")]
    [InlineData("\x1b[5;1;3m")]
    public void HiddenPhase_SuppressesGlyphsAndDecorations(string style)
    {
        using var hidden = Render(" " + style + "MMMM", false);
        using var plain = Render("     ", true);
        Assert.Equal(Region(plain, 1, 5), Region(hidden, 1, 5));
    }

    [AvaloniaTheory]
    [InlineData("\x1b[5m", false)]
    [InlineData("\x1b[8m", true)]
    public void SuppressedInk_StillPaintsSizedBackgrounds(string style, bool phase)
    {
        using var hidden = Render(" " + style + "\x1b[41m\x1b]66;s=1:w=4;MMMM\a", phase);
        using var plain = Render(" \x1b[41m\x1b]66;s=1:w=4;    \a", true);
        Assert.Equal(Region(plain, 1, 5), Region(hidden, 1, 5));
    }

    [AvaloniaFact]
    public void RemovingBlink_DoesNotReuseTextFromBeforeTheAnimation()
    {
        using var cached = new DrawPathHarness(20, 3);
        using var uncached = new DrawPathHarness(20, 3, withRowCache: false);
        foreach (var harness in new[] { cached, uncached })
        {
            harness.Parser.Process("AAAAAAAAAAAA");
            harness.DrawFrame();
            harness.Parser.Process("\r\x1b[5mB\x1b[25m\x1b[8GZ");
            harness.DrawFrame();
            harness.Parser.Process("\rB");
            harness.DrawFrame();
        }
        Assert.Equal(uncached.Pixels(), cached.Pixels());
    }
}
