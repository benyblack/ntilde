using Ntilde.Platform;
using Ntilde.Rendering;
using Ntilde.Shell;
using Ntilde.Tests.Infra;
using Ntilde.VT;
using SkiaSharp;

namespace Ntilde.Tests.RenderTests;

[Trait("Lane", "PlatformBoot")]
[Collection("GoldenPng")]
public sealed class KittyAspectRenderTests
{
    private static readonly CellMetrics Metrics = new() { CellWidth = 11.333f, CellHeight = 24, Baseline = 18, Ascent = 18, Descent = 6 };
    private static string Command(string controls, byte[] raw) => $"\x1b_G{controls};{Convert.ToBase64String(raw)}\x1b\\";

    [Theory]
    [InlineData(4, 2, 1.0)]
    [InlineData(2, 4, 1.0)]
    [InlineData(4, 2, 1.5)]
    [InlineData(2, 4, 1.5)]
    [InlineData(4, 2, 2.0)]
    [InlineData(2, 4, 2.0)]
    [InlineData(3, 2, 1.5)]
    public void Explicit_cell_rectangle_fits_real_Kitty_pixels_without_distortion_or_non_target_changes(int width, int height, double scale)
    {
        var buffer = NewBuffer();
        var parser = NewParser(buffer);
        var retired = new List<object>();
        using var before = Capture(buffer, scale, out PixelGrid grid);
        try
        {
            parser.Process(Command($"a=T,f=32,s={width},v={height},i=23,c=4,r=2,C=1", Source(width, height)));
            var placement = Assert.Single(buffer.Images);
            Assert.Equal((3, 3, 4, 2), (placement.CellX, placement.CellY, placement.CellWidth, placement.CellHeight));
            Assert.Equal((3, 3), (buffer.CursorCol, buffer.CursorRow));
            using var after = Capture(buffer, scale, out PixelGrid afterGrid);
            Assert.Equal(grid, afterGrid);
            var envelope = new SKRectI(grid.XForCol(3), grid.YForRowTop(3), grid.XForCol(7), grid.YForRowTop(5));
            AssertUnchangedOutside(before, after, envelope, grid);
            var actual = InkBounds(after, envelope);
            double fit = Math.Min(envelope.Width / (double)width, envelope.Height / (double)height);
            // A fractional destination edge can cover either neighboring device pixel.
            Assert.InRange(actual.Width, (int)Math.Floor(width * fit), (int)Math.Ceiling(width * fit));
            Assert.InRange(actual.Height, (int)Math.Floor(height * fit), (int)Math.Ceiling(height * fit));
            Assert.True(actual.Left >= envelope.Left && actual.Top >= envelope.Top && actual.Right <= envelope.Right && actual.Bottom <= envelope.Bottom);
            AssertInteriorColors(after, actual, width, height);
        }
        finally { DisposeImages(buffer, retired); }
    }

    [Theory]
    [InlineData("anonymous-kitty")]
    [InlineData("iterm-stretch")]
    [InlineData("sixel")]
    [InlineData("legacy")]
    public void Protocol_identity_selects_Kitty_fit_while_existing_non_Kitty_envelopes_remain_filled(string protocol)
    {
        var buffer = NewBuffer(); var parser = NewParser(buffer); var retired = new List<object>();
        using var before = Capture(buffer, 1, out PixelGrid grid);
        try
        {
            byte[] rgba = Source(4, 2);
            if (protocol == "anonymous-kitty") parser.Process(Command("a=T,f=32,s=4,v=2,c=4,r=2,C=1", rgba));
            else if (protocol == "iterm-stretch")
            {
                using var source = new SKBitmap(4, 2);
                for (int y = 0; y < 2; y++) for (int x = 0; x < 4; x++) source.SetPixel(x, y, Color(x, y, 4, 2));
                using var image = SKImage.FromBitmap(source); using var png = image.Encode(SKEncodedImageFormat.Png, 100);
                parser.Process("\x1b]1337;File=inline=1;width=4;height=2;preserveAspectRatio=0:" + Convert.ToBase64String(png.ToArray()) + "\x1b\\");
            }
            else if (protocol == "sixel") parser.Process("\x1bP0;1q\"1;1;4;6#0;2;100;0;0#0!4~\x1b\\");
            else
            {
                object handle = new SkiaImageDecoder().DecodeRawImage(rgba, 4, 4, 2, out _, out _)!;
                buffer.AddImage(new TerminalImage(handle, 3, 3, 4, 2));
            }
            var placement = Assert.Single(buffer.Images);
            using var after = Capture(buffer, 1, out _);
            var envelope = new SKRectI(grid.XForCol(placement.CellX), grid.YForRowTop(placement.CellY),
                grid.XForCol(placement.CellX + placement.CellWidth), grid.YForRowTop(placement.CellY + placement.CellHeight));
            AssertUnchangedOutside(before, after, envelope, grid);
            var actual = InkBounds(after, envelope);
            Assert.Equal(envelope.Width, actual.Width);
            Assert.Equal(protocol == "anonymous-kitty" ? envelope.Width / 2 : envelope.Height, actual.Height);
            Assert.Equal(protocol == "anonymous-kitty" ? 0u : null, placement.KittyImageId);
        }
        finally { DisposeImages(buffer, retired); }
    }

    [Theory]
    [InlineData("top")]
    [InlineData("bottom")]
    [InlineData("right")]
    public void Snapshot_clipping_keeps_sampling_the_original_fitted_image_instead_of_refitting_the_visible_piece(string edge)
    {
        var buffer = NewBuffer(); var parser = NewParser(buffer); var retired = new List<object>();
        using var baseline = Capture(buffer, 1, out PixelGrid grid);
        try
        {
            parser.Process(Command("a=T,f=32,s=4,v=4,i=23,c=4,r=4,C=1", Source(4, 4)));
            var placement = Assert.Single(buffer.Images);
            using var full = Capture(buffer, 1, out _);
            buffer.Lock.EnterWriteLock();
            try
            {
                if (edge == "top") placement.ClipTopRows = 1;
                else if (edge == "bottom") placement.ClipBottomRows = 3;
                else placement.ClipRightColumns = 3;
            }
            finally { buffer.Lock.ExitWriteLock(); }
            using var clipped = Capture(buffer, 1, out _);
            var envelope = new SKRectI(grid.XForCol(3), grid.YForRowTop(3), grid.XForCol(7), grid.YForRowTop(7));
            var visible = new SKRectI(envelope.Left, grid.YForRowTop(3 + placement.ClipTopRows),
                grid.XForCol(7 - placement.ClipRightColumns), grid.YForRowTop(7 - placement.ClipBottomRows));
            for (int y = 0; y < clipped.Height; y++) for (int x = 0; x < clipped.Width; x++)
                Assert.Equal(visible.Contains(x, y) ? full.GetPixel(x, y) : baseline.GetPixel(x, y), clipped.GetPixel(x, y));
        }
        finally { DisposeImages(buffer, retired); }
    }

    [Fact]
    public void Main_history_translation_preserves_the_fitted_ratio_and_all_non_image_pixels()
    {
        var buffer = NewBuffer(); var parser = NewParser(buffer);
        var reference = NewBuffer(); var referenceParser = NewParser(reference); var retired = new List<object>();
        try
        {
            parser.Process(Command("a=T,f=32,s=4,v=2,i=23,c=4,r=2,C=1", Source(4, 2)));
            const string scroll = "\x1b[10;1H\r\n\r\n\r\n";
            parser.Process(scroll); referenceParser.Process(scroll);
            Assert.Equal(3, buffer.Scrollback.Count); Assert.Equal(reference.Scrollback.Count, buffer.Scrollback.Count);
            Assert.False(Assert.Single(buffer.Images).IsAltScreenImage);
            using var baseline = Capture(reference, 1, out PixelGrid grid);
            using var rendered = Capture(buffer, 1, out _);
            var envelope = new SKRectI(grid.XForCol(3), 0, grid.XForCol(7), grid.YForRowTop(2));
            AssertUnchangedOutside(baseline, rendered, envelope, grid, controlRow: 5);
            var bounds = InkBounds(rendered, envelope);
            Assert.Equal(envelope.Width, bounds.Width); Assert.Equal(envelope.Width / 2, bounds.Height);
            AssertInteriorColors(rendered, bounds, 4, 2);
        }
        finally { DisposeImages(buffer, retired); }
    }

    [Fact]
    public void Cached_replacement_clears_old_letterbox_pixels_and_keeps_snapshot_pinned_source_live()
    {
        var buffer = NewBuffer(); var parser = NewParser(buffer); var retired = new List<object>();
        using var cache = new RowImageCache();
        using var baseline = Capture(buffer, 1, out PixelGrid grid, cache);
        try
        {
            parser.Process(Command("a=T,f=32,s=4,v=2,i=23,c=4,r=2,C=1", Source(4, 2)));
            var old = Assert.IsType<SKBitmap>(Assert.Single(buffer.Images).ImageHandle);
            int session = buffer.BeginSnapshotSession();
            try
            {
                using var pinned = buffer.CaptureRenderSnapshot(new RenderSnapshotRequest
                { ViewportRows = 10, ViewportCols = 20, Isolated = true, SnapshotSessionId = session }, out _);
                using var warm = Capture(buffer, 1, out _, cache);
                using var repeated = Capture(buffer, 1, out _, cache);
                Assert.Equal(warm.Bytes, repeated.Bytes);
                parser.Process(Command("a=T,f=32,s=2,v=4,i=23,c=4,r=2,C=1", Source(2, 4)));
                buffer.DrainRetiredImageHandles(retired, long.MaxValue); Assert.Empty(retired);
                Assert.NotEqual(IntPtr.Zero, old.Handle); Assert.Equal(SKColors.Red, old.GetPixel(0, 0));
                using var cached = Capture(buffer, 1, out _, cache);
                using var uncached = Capture(buffer, 1, out _);
                Assert.Equal(uncached.Bytes, cached.Bytes);
                var envelope = new SKRectI(grid.XForCol(3), grid.YForRowTop(3), grid.XForCol(7), grid.YForRowTop(5));
                AssertUnchangedOutside(baseline, cached, envelope, grid);
                var bounds = InkBounds(cached, envelope);
                Assert.Equal(envelope.Height / 2, bounds.Width); Assert.Equal(envelope.Height, bounds.Height);
                Assert.Equal(SKColors.Black, cached.GetPixel(envelope.Right - 1, envelope.Top + 1));
                Assert.True(pinned.Images.Array[0].PreserveAspectRatio);
            }
            finally { buffer.EndSnapshotSession(session); }
            buffer.DrainRetiredImageHandles(retired, long.MaxValue);
            Assert.Same(old, Assert.Single(retired)); old.Dispose(); retired.Clear();
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
    private static SKBitmap Capture(TerminalBuffer buffer, double scale, out PixelGrid grid, RowImageCache? cache = null)
    {
        SnapshotService.EnsureAvaloniaInitialized();
        var result = TerminalSnapshotRenderer.CaptureWithContext(buffer, Metrics, 240, 240,
            new TerminalSnapshotOptions { HideCursor = true, FillBackground = true, RenderScaling = scale, RowCache = cache }, true, out var info);
        grid = Assert.IsType<SnapshotRenderContext>(info.Context).Frame.PixelGrid;
        return result;
    }
    private static SKRectI InkBounds(SKBitmap bitmap, SKRectI envelope)
    {
        int left = envelope.Right, right = envelope.Left, top = envelope.Bottom, bottom = envelope.Top;
        for (int y = envelope.Top; y < envelope.Bottom; y++) for (int x = envelope.Left; x < envelope.Right; x++)
        {
            if (bitmap.GetPixel(x, y) == SKColors.Black) continue;
            left = Math.Min(left, x); right = Math.Max(right, x + 1); top = Math.Min(top, y); bottom = Math.Max(bottom, y + 1);
        }
        Assert.True(right > left && bottom > top, "The independently blank target must contain actual decoded image pixels.");
        return new SKRectI(left, top, right, bottom);
    }
    private static void AssertUnchangedOutside(SKBitmap before, SKBitmap after, SKRectI envelope, PixelGrid grid, int controlRow = 0)
    {
        Assert.Equal(before.Info, after.Info);
        Assert.Equal(new SKColor(17, 41, 73), before.GetPixel(grid.OriginXPx + 1, grid.YForRowTop(controlRow) + 1));
        for (int y = 0; y < before.Height; y++) for (int x = 0; x < before.Width; x++)
        {
            if (envelope.Contains(x, y)) Assert.Equal(SKColors.Black, before.GetPixel(x, y));
            else Assert.Equal(before.GetPixel(x, y), after.GetPixel(x, y));
        }
    }
    private static void AssertInteriorColors(SKBitmap bitmap, SKRectI bounds, int width, int height)
    {
        int splitX = Math.Max(1, width / 3), splitY = Math.Max(1, height / 3);
        foreach (int sx in new[] { 0, width - 1 }) foreach (int sy in new[] { 0, height - 1 })
        {
            double x = sx == 0 ? splitX / 2.0 : (splitX + width) / 2.0;
            double y = sy == 0 ? splitY / 2.0 : (splitY + height) / 2.0;
            int px = bounds.Left + Math.Min(bounds.Width - 1, (int)(bounds.Width * x / width));
            int py = bounds.Top + Math.Min(bounds.Height - 1, (int)(bounds.Height * y / height));
            Assert.Equal(Color(sx, sy, width, height), bitmap.GetPixel(px, py));
        }
    }
    private static byte[] Source(int width, int height)
    {
        var raw = new byte[width * height * 4];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            SKColor color = Color(x, y, width, height); int offset = (y * width + x) * 4;
            raw[offset] = color.Red; raw[offset + 1] = color.Green; raw[offset + 2] = color.Blue; raw[offset + 3] = 255;
        }
        return raw;
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
