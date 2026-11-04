using Ntilde.Platform;
using Ntilde.Rendering;
using Ntilde.Shell;
using Ntilde.Tests.Infra;
using Ntilde.VT;
using SkiaSharp;

namespace Ntilde.Tests.RenderTests;

[Trait("Lane", "PlatformBoot")]
[Collection("GoldenPng")]
public sealed class ITermAspectRenderTests
{
    private static readonly CellMetrics Metrics = new() { CellWidth = 11.333f, CellHeight = 24, Baseline = 18, Ascent = 18, Descent = 6 };
    public static TheoryData<string, bool, int, int, double> Placements
    {
        get
        {
            var data = new TheoryData<string, bool, int, int, double>();
            foreach (string option in new[] { "", ";preserveAspectRatio=1", ";preserveAspectRatio=0" })
                foreach (var size in new[] { (6, 2), (2, 6) })
                    foreach (double scale in new[] { 1.0, 1.5, 2.0 })
                        data.Add(option, option != ";preserveAspectRatio=0", size.Item1, size.Item2, scale);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Placements))]
    public void Actual_inline_PNG_uses_default_or_explicit_aspect_policy_with_exact_non_target_controls(
        string option, bool preserve, int width, int height, double scale)
    {
        var buffer = NewBuffer(); var parser = NewParser(buffer); var retired = new List<object>();
        using var before = Capture(buffer, scale, out PixelGrid grid);
        try
        {
            parser.Process(Image(width, height, option));
            TerminalImage placement = Assert.Single(buffer.Images);
            Assert.Equal((3, 3, 4, 2), (placement.CellX, placement.CellY, placement.CellWidth, placement.CellHeight));
            using var after = Capture(buffer, scale, out PixelGrid afterGrid);
            Assert.Equal(grid, afterGrid);
            var envelope = Envelope(grid, placement);
            AssertNonTargetControls(before, after, envelope, grid);
            var bounds = InkBounds(after, envelope);
            double fit = Math.Min(envelope.Width / (double)width, envelope.Height / (double)height);
            double wantedWidth = preserve ? width * fit : envelope.Width;
            double wantedHeight = preserve ? height * fit : envelope.Height;
            Assert.InRange(bounds.Width, (int)Math.Floor(wantedWidth), (int)Math.Ceiling(wantedWidth));
            Assert.InRange(bounds.Height, (int)Math.Floor(wantedHeight), (int)Math.Ceiling(wantedHeight));
            Assert.True(bounds.Left >= envelope.Left && bounds.Top >= envelope.Top && bounds.Right <= envelope.Right && bounds.Bottom <= envelope.Bottom);
            AssertColors(after, bounds, width, height);
        }
        finally { DisposeImages(buffer, retired); }
    }

    [Theory]
    [InlineData("top")]
    [InlineData("bottom")]
    [InlineData("right")]
    public void Placement_intent_is_applied_before_original_snapshot_clipping(string edge)
    {
        var buffer = NewBuffer(); var parser = NewParser(buffer); var retired = new List<object>();
        using var baseline = Capture(buffer, 1, out PixelGrid grid);
        try
        {
            parser.Process(Image(2, 6, "", 4, 4));
            TerminalImage image = Assert.Single(buffer.Images);
            Assert.True(image.PreserveAspectRatio);
            using var full = Capture(buffer, 1, out _);
            var envelope = Envelope(grid, image);
            var fullBounds = InkBounds(full, envelope);
            Assert.Equal(envelope.Height / 3, fullBounds.Width); Assert.Equal(envelope.Height, fullBounds.Height);
            AssertColors(full, fullBounds, 2, 6);
            buffer.Lock.EnterWriteLock();
            try
            {
                if (edge == "top") image.ClipTopRows = 1;
                else if (edge == "bottom") image.ClipBottomRows = 3;
                else image.ClipRightColumns = 3;
            }
            finally { buffer.Lock.ExitWriteLock(); }
            using var clipped = Capture(buffer, 1, out _);
            var visible = new SKRectI(envelope.Left, grid.YForRowTop(image.CellY + image.ClipTopRows),
                grid.XForCol(image.CellX + image.CellWidth - image.ClipRightColumns), grid.YForRowTop(image.CellY + image.CellHeight - image.ClipBottomRows));
            for (int y = 0; y < clipped.Height; y++) for (int x = 0; x < clipped.Width; x++)
                Assert.Equal(visible.Contains(x, y) ? full.GetPixel(x, y) : baseline.GetPixel(x, y), clipped.GetPixel(x, y));
        }
        finally { DisposeImages(buffer, retired); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Default_and_explicit_stretch_intents_survive_main_history_translation(bool preserve)
    {
        var buffer = NewBuffer(); var parser = NewParser(buffer);
        var reference = NewBuffer(); var retired = new List<object>();
        try
        {
            parser.Process(Image(6, 2, preserve ? "" : ";preserveAspectRatio=0"));
            TerminalImage image = Assert.Single(buffer.Images);
            // Exercise history translation directly: image placement intentionally swallows a following newline.
            for (int i = 0; i < 3; i++) { buffer.ScrollUp(); reference.ScrollUp(); }
            Assert.Equal(3, buffer.Scrollback.Count); Assert.Equal(reference.Scrollback.Count, buffer.Scrollback.Count);
            Assert.Equal(preserve, image.PreserveAspectRatio); Assert.False(image.IsAltScreenImage);
            using var before = Capture(reference, 1, out PixelGrid grid);
            using var after = Capture(buffer, 1, out _);
            var envelope = Envelope(grid, image, buffer.Scrollback.Count);
            AssertNonTargetControls(before, after, envelope, grid, controlRow: 5);
            var bounds = InkBounds(after, envelope);
            Assert.Equal(envelope.Width, bounds.Width);
            double height = preserve ? envelope.Width / 3.0 : envelope.Height;
            Assert.InRange(bounds.Height, (int)Math.Floor(height), (int)Math.Ceiling(height));
            AssertColors(after, bounds, 6, 2);
        }
        finally { DisposeImages(buffer, retired); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Clear_removes_pixels_and_snapshot_pins_keep_the_same_source_and_intent_until_release(bool preserve)
    {
        var buffer = NewBuffer(); var parser = NewParser(buffer);
        var reference = NewBuffer(); var referenceParser = NewParser(reference); var retired = new List<object>();
        using var before = Capture(buffer, 1, out PixelGrid grid);
        try
        {
            parser.Process(Image(6, 2, preserve ? "" : ";preserveAspectRatio=0"));
            TerminalImage placement = Assert.Single(buffer.Images);
            SKBitmap source = Assert.IsType<SKBitmap>(placement.ImageHandle);
            using var visible = Capture(buffer, 1, out _);
            var envelope = Envelope(grid, placement);
            AssertNonTargetControls(before, visible, envelope, grid);
            AssertColors(visible, InkBounds(visible, envelope), 6, 2);
            int session = buffer.BeginSnapshotSession();
            try
            {
                using var pinned = buffer.CaptureRenderSnapshot(new RenderSnapshotRequest
                { ViewportRows = 10, ViewportCols = 20, Isolated = true, SnapshotSessionId = session }, out _);
                parser.Process("\x1b[2J"); referenceParser.Process("\x1b[2J");
                Assert.Empty(buffer.Images);
                buffer.DrainRetiredImageHandles(retired, long.MaxValue); Assert.Empty(retired);
                Assert.Equal(preserve, pinned.Images.Array[0].PreserveAspectRatio);
                Assert.Same(source, pinned.Images.Array[0].ImageHandle);
                Assert.NotEqual(IntPtr.Zero, source.Handle); Assert.Equal(SKColors.Red, source.GetPixel(0, 0));
                using var cleared = Capture(buffer, 1, out _);
                using var expected = Capture(reference, 1, out _);
                Assert.Equal(expected.Bytes, cleared.Bytes);
            }
            finally { buffer.EndSnapshotSession(session); }
            buffer.DrainRetiredImageHandles(retired, long.MaxValue);
            Assert.Same(source, Assert.Single(retired)); source.Dispose(); retired.Clear();
            Assert.Equal(IntPtr.Zero, source.Handle);
        }
        finally { DisposeImages(buffer, retired); }
    }

    private static TerminalBuffer NewBuffer()
    {
        var buffer = new TerminalBuffer(20, 10) { Theme = new TerminalTheme { Foreground = TermColor.White, Background = TermColor.Black } };
        new AnsiParser(buffer).Process("\x1b[1;1H\x1b[48;2;17;41;73m  \x1b[0m\x1b[9;1H\x1b[48;2;17;41;73m  \x1b[0m\x1b[4;4H");
        return buffer;
    }
    private static AnsiParser NewParser(TerminalBuffer buffer) => new(buffer, forceConPtyFiltering: false)
    { ImageDecoder = new SkiaImageDecoder(), CellWidth = Metrics.CellWidth, CellHeight = Metrics.CellHeight };
    private static string Image(int width, int height, string option, int cols = 4, int rows = 2)
    {
        using var source = new SKBitmap(width, height);
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) source.SetPixel(x, y, Color(x, y, width, height));
        using var image = SKImage.FromBitmap(source); using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return $"\x1b]1337;File=inline=1;width={cols};height={rows}{option}:" + Convert.ToBase64String(png.ToArray()) + "\x1b\\";
    }
    private static SKBitmap Capture(TerminalBuffer buffer, double scale, out PixelGrid grid)
    {
        SnapshotService.EnsureAvaloniaInitialized();
        var bitmap = TerminalSnapshotRenderer.CaptureWithContext(buffer, Metrics, 240, 240,
            new TerminalSnapshotOptions { HideCursor = true, FillBackground = true, RenderScaling = scale }, true, out var info);
        grid = Assert.IsType<SnapshotRenderContext>(info.Context).Frame.PixelGrid;
        return bitmap;
    }
    private static SKRectI Envelope(PixelGrid grid, TerminalImage image, int historyOffset = 0)
        => new(grid.XForCol(image.CellX), grid.YForRowTop(image.CellY - historyOffset),
            grid.XForCol(image.CellX + image.CellWidth), grid.YForRowTop(image.CellY - historyOffset + image.CellHeight));
    private static SKRectI InkBounds(SKBitmap bitmap, SKRectI envelope)
    {
        int left = envelope.Right, right = envelope.Left, top = envelope.Bottom, bottom = envelope.Top;
        for (int y = envelope.Top; y < envelope.Bottom; y++) for (int x = envelope.Left; x < envelope.Right; x++)
        {
            if (bitmap.GetPixel(x, y) == SKColors.Black) continue;
            left = Math.Min(left, x); right = Math.Max(right, x + 1); top = Math.Min(top, y); bottom = Math.Max(bottom, y + 1);
        }
        Assert.True(right > left && bottom > top, "The image-free reference target must contain actual decoded image pixels after placement.");
        return new SKRectI(left, top, right, bottom);
    }
    private static void AssertNonTargetControls(SKBitmap before, SKBitmap after, SKRectI envelope, PixelGrid grid, int controlRow = 0)
    {
        Assert.Equal(before.Info, after.Info);
        Assert.Equal(new SKColor(17, 41, 73), before.GetPixel(grid.OriginXPx + 1, grid.YForRowTop(controlRow) + 1));
        for (int y = 0; y < before.Height; y++) for (int x = 0; x < before.Width; x++)
        {
            if (envelope.Contains(x, y)) Assert.Equal(SKColors.Black, before.GetPixel(x, y));
            else Assert.Equal(before.GetPixel(x, y), after.GetPixel(x, y));
        }
    }
    private static void AssertColors(SKBitmap bitmap, SKRectI bounds, int width, int height)
    {
        int splitX = Math.Max(1, width / 3), splitY = Math.Max(1, height / 3);
        foreach (int sx in new[] { 0, width - 1 }) foreach (int sy in new[] { 0, height - 1 })
        {
            double x = sx == 0 ? splitX / 2.0 : (splitX + width) / 2.0;
            double y = sy == 0 ? splitY / 2.0 : (splitY + height) / 2.0;
            Assert.Equal(Color(sx, sy, width, height), bitmap.GetPixel(
                bounds.Left + Math.Min(bounds.Width - 1, (int)(bounds.Width * x / width)),
                bounds.Top + Math.Min(bounds.Height - 1, (int)(bounds.Height * y / height))));
        }
    }
    private static SKColor Color(int x, int y, int width, int height)
        => (x < Math.Max(1, width / 3), y < Math.Max(1, height / 3)) switch
        { (true, true) => SKColors.Red, (false, true) => SKColors.Lime, (true, false) => SKColors.Blue, _ => SKColors.Yellow };
    private static void DisposeImages(TerminalBuffer buffer, List<object> retired)
    {
        buffer.ClearImages(); buffer.DrainRetiredImageHandles(retired, long.MaxValue);
        foreach (IDisposable handle in retired.Distinct(ReferenceEqualityComparer.Instance)) handle.Dispose();
    }
}
