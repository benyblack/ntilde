using System;
using Avalonia.Headless.XUnit;
using Ntilde.Rendering;
using Ntilde.Shell;
using Ntilde.VT;
using SkiaSharp;

namespace Ntilde.Tests.RenderTests;

public sealed class ControlledSnapshotTests
{
    private static readonly CellMetrics Metrics = new()
    {
        CellWidth = 8,
        CellHeight = 18,
        Baseline = 14,
        Ascent = 14,
        Descent = 4,
    };

    private static TerminalBuffer Buffer(string input)
    {
        var buffer = new TerminalBuffer(20, 3)
        {
            Theme = new TerminalTheme { Foreground = TermColor.White, Background = TermColor.Black },
        };
        new AnsiParser(buffer).Process(input);
        return buffer;
    }

    private static SKBitmap Capture(TerminalBuffer buffer, long? time, out SnapshotRenderContext context,
        bool preferredCursorBlink = true)
    {
        var bitmap = TerminalSnapshotRenderer.CaptureWithContext(buffer, Metrics, 168, 54,
            new TerminalSnapshotOptions
            {
                FontResolution = SnapshotFontResolution.LiveParity,
                TypefaceFamily = BundledFontCatalog.DefaultTerminalFontFamily,
                FillBackground = true,
                RenderScaling = 2,
                PresentationTimeMs = time,
                PreferredCursorStyle = CursorStyle.Beam,
                PreferredCursorBlink = preferredCursorBlink,
            }, true, out var details);
        context = Assert.IsType<SnapshotRenderContext>(details);
        return bitmap;
    }

    [AvaloniaTheory]
    [InlineData(0, true)]
    [InlineData(529, true)]
    [InlineData(530, false)]
    [InlineData(1059, false)]
    [InlineData(1060, true)]
    public void SyntheticClock_ControlsInk_AndReportsTheExactFrame(long time, bool visible)
    {
        var buffer = Buffer("\x1b[?25l \x1b[5;41mMMMM");
        using var picture = Capture(buffer, time, out var context);
        using var reference = Capture(Buffer(visible ? "\x1b[?25l \x1b[41mMMMM" : "\x1b[?25l \x1b[41m    "), null, out _);
        Assert.Equal(reference.Bytes, picture.Bytes);
        Assert.Equal(time, context.PresentationTimeMs);
        Assert.Equal(visible, context.TextBlinkVisible);
        Assert.False(context.Frame.CursorVisible);
        Assert.Equal(20, context.Frame.Cols);
        Assert.Equal(3, context.Frame.Rows);
        Assert.Equal(8, context.Frame.PixelGrid.OriginXPx);
        Assert.Equal(16, context.Frame.PixelGrid.CellWidthPx);
        Assert.Equal(36, context.Frame.PixelGrid.CellHeightPx);
        Assert.Equal(336, picture.Width);
        Assert.Equal(108, picture.Height);
    }

    [AvaloniaTheory]
    [InlineData(1, true, CursorStyle.Block)]
    [InlineData(2, false, CursorStyle.Block)]
    [InlineData(3, true, CursorStyle.Underline)]
    [InlineData(4, false, CursorStyle.Underline)]
    [InlineData(5, true, CursorStyle.Beam)]
    [InlineData(6, false, CursorStyle.Beam)]
    public void SyntheticClock_RespectsExplicitCursorBlinkAndShape(int mode, bool blinking, CursorStyle style)
    {
        var buffer = Buffer($"\x1b[{mode} q");
        using var on = Capture(buffer, 0, out var first, preferredCursorBlink: false);
        using var off = Capture(buffer, 530, out var second, preferredCursorBlink: false);
        Assert.True(first.Frame.CursorVisible);
        Assert.Equal(blinking, first.Frame.CursorBlinkEnabled);
        Assert.Equal(style, first.Frame.CursorStyle);
        Assert.Equal(!blinking, second.Frame.CursorVisible);
        if (blinking) Assert.NotEqual(on.Bytes, off.Bytes);
        else Assert.Equal(on.Bytes, off.Bytes);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefaultCursor_UsesThePresentingHostsBlinkPreference(bool blink)
    {
        using var bitmap = Capture(Buffer(""), 530, out var context, blink);
        Assert.Equal(blink, context.Frame.CursorBlinkEnabled);
        Assert.Equal(!blink, context.Frame.CursorVisible);
        Assert.Equal(CursorStyle.Beam, context.Frame.CursorStyle);
    }

    [AvaloniaFact]
    public void DefaultCapture_RemainsVisible_AndContextOwnsDetachedFontAndThemeFacts()
    {
        var buffer = Buffer("\x1b[?25l\x1b[5mMMMM");
        using var initial = Capture(buffer, null, out var context);
        using var fixedVisible = Capture(buffer, 0, out var other);
        Assert.Equal(initial.Bytes, fixedVisible.Bytes);
        Assert.Null(context.PresentationTimeMs);
        Assert.True(context.TextBlinkVisible);
        Assert.False(string.IsNullOrWhiteSpace(context.PrimaryFontFamily));
        Assert.Matches("^[a-f0-9]{64}$", context.PrimaryFontSha256!);
        Assert.Equal(context.PrimaryFontSha256, other.PrimaryFontSha256);
        Assert.Null(context.Frame.Theme.Source);
        Assert.Equal(256, context.Frame.Theme.AnsiPalette.Length);
        var palette = (TermColor[])context.Frame.Theme.AnsiPalette.Clone();
        new AnsiParser(buffer).Process("\x1b]4;1;rgb:12/34/56\a\x1b]11;rgb:11/22/33\a");
        Assert.Equal(TermColor.Black, context.Frame.Theme.Background);
        Assert.Equal(palette, context.Frame.Theme.AnsiPalette);
    }

    [AvaloniaFact]
    public void NegativeSyntheticTime_IsRefusedBeforeRendering()
        => Assert.Throws<ArgumentOutOfRangeException>(() => Capture(Buffer(""), -1, out _));
}
