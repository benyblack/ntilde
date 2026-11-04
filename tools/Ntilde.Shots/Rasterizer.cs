using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SkiaSharp;

namespace Ntilde.Shots;

public static class Rasterizer
{
    /// <summary>
    /// Rasterizes the whole window, chrome included. CaptureRenderedFrame is used rather than
    /// RenderTargetBitmap because the window's own title bar, tab strip, and every in-window
    /// overlay must appear; MainWindow sets ExtendClientAreaToDecorationsHint, so this frame
    /// contains essentially the entire visual identity.
    /// </summary>
    public static SKBitmap CaptureWindow(Window window, double scale)
    {
        window.SetRenderScaling(scale);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        using WriteableBitmap frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException(
                "CaptureRenderedFrame returned null. The window is not rendering — check that " +
                "ShotsAppBuilder still sets UseHeadlessDrawing = false.");

        using var stream = new MemoryStream();
        frame.Save(stream, PngBitmapEncoderOptions.Default);
        stream.Position = 0;

        SKBitmap bitmap = SKBitmap.Decode(stream)
            ?? throw new InvalidOperationException("Could not decode the captured frame.");

        if (window is MainWindow mainWindow && OperatingSystem.IsMacOS())
        {
            // Focus mode's decorations theme shrinks the title band to the strip, and AppKit centres
            // the buttons in whatever band it is given.
            double band = mainWindow.IsFocusMode ? MainWindow.FocusStripHeight : 36;
            DrawTrafficLights(bitmap, window.RenderScaling, band / 2);
        }

        return bitmap;
    }

    /// <summary>
    /// On macOS MainWindow leaves <see cref="MainWindow.MacOsTrafficLightReserve"/> empty at the
    /// left of the tab strip for the system's close/minimise/zoom buttons, which AppKit draws and
    /// the headless renderer never does - so a Mac capture showed a blank gap where every real Mac
    /// window has them. Painted at the standard size (12pt) and spacing (20pt between centres),
    /// centred at <paramref name="centreY"/> (the middle of the 36pt tab band, or of focus mode's
    /// strip), in the system's own fill and rim colours.
    /// </summary>
    private static void DrawTrafficLights(SKBitmap bitmap, double scale, double centreY)
    {
        (SKColor Fill, SKColor Rim)[] lights =
        [
            (new SKColor(0xFF, 0x5F, 0x57), new SKColor(0xE0, 0x44, 0x3E)),
            (new SKColor(0xFE, 0xBC, 0x2E), new SKColor(0xDE, 0xA1, 0x23)),
            (new SKColor(0x28, 0xC8, 0x40), new SKColor(0x1A, 0xAB, 0x29))
        ];

        using var canvas = new SKCanvas(bitmap);
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var rim = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = (float)(0.5 * scale) };

        float radius = (float)(6 * scale);
        float y = (float)(centreY * scale);
        for (int i = 0; i < lights.Length; i++)
        {
            float x = (float)((20 + 20 * i) * scale);
            fill.Color = lights[i].Fill;
            rim.Color = lights[i].Rim;
            canvas.DrawCircle(x, y, radius, fill);
            canvas.DrawCircle(x, y, radius - rim.StrokeWidth / 2, rim);
        }
    }

    public static void WritePng(SKBitmap bitmap, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using SKData data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using FileStream file = File.Create(path);
        data.SaveTo(file);
    }

    /// <summary>
    /// Writes a PNG tuned for the committed <c>docs/assets/shots/</c> tree: every filter Skia's
    /// PNG encoder offers plus zlib level 9, instead of <see cref="WritePng"/>'s zero-tuning
    /// <c>bitmap.Encode(Png, 100)</c> (Skia's own default filter/zlib settings). Deliberately not
    /// the default: this trades encode time for smaller output, which is only worth paying where
    /// the result is committed. Masters (Tier 1/2, gitignored <c>artifacts/</c>) and clip frame
    /// sequences (numbered in the hundreds, also gitignored) call <see cref="WritePng"/> instead —
    /// their size never matters, and re-encoding hundreds of frames at level 9 would slow every
    /// capture run for no benefit. Only <see cref="VariantBuilder"/>'s Tier 3 published variants
    /// call this.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="bitmap"/> exposes no readable pixel buffer (<c>PeekPixels</c> returned
    /// null) or the encoder itself failed.
    /// </exception>
    public static void WriteOptimizedPng(SKBitmap bitmap, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using SKPixmap pixmap = bitmap.PeekPixels()
            ?? throw new InvalidOperationException(
                $"Could not read pixels from the bitmap being written to '{path}' as an optimized PNG.");

        var options = new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, 9);
        using SKData data = pixmap.Encode(options)
            ?? throw new InvalidOperationException($"SKPixmap.Encode(PNG) failed writing '{path}'.");
        using FileStream file = File.Create(path);
        data.SaveTo(file);
    }

    /// <summary>
    /// Writes <paramref name="bitmap"/> as a WebP under <see cref="SKWebpEncoderCompression.Lossless"/>
    /// — the spec requires PNG plus WebP siblings for every published variant, and lossy WebP's
    /// block artifacts land directly on monospace glyph edges in these text-heavy terminal
    /// screenshots. Quality 100 selects libwebp's slowest/most thorough lossless compression
    /// effort, not a visual-quality knob.
    /// </summary>
    /// <remarks>
    /// Verified empirically (see <c>RasterizerTests</c> and the task report) to be bit-exact for
    /// every fully opaque pixel — the case that matters for glyphs, chrome, and every screenshot
    /// pixel that is not the drop shadow's own soft edge. A small residual (observed: ~0.2% of
    /// pixels, single-digit-of-255 per channel) shows up only on the blurred, semi-transparent
    /// shadow gradient <see cref="PostProcess.RoundedWithShadow"/> paints outside the window
    /// frame. <see cref="SKWebpEncoderOptions"/> in the installed SkiaSharp (3.119.4) exposes only
    /// <see cref="SKWebpEncoderCompression"/> and <c>Quality</c> — no control over libwebp's
    /// internal alpha handling — so this residual is not something a caller can configure away.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="bitmap"/> exposes no readable pixel buffer or the encoder itself failed.
    /// </exception>
    public static void WriteLosslessWebp(SKBitmap bitmap, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using SKPixmap pixmap = bitmap.PeekPixels()
            ?? throw new InvalidOperationException(
                $"Could not read pixels from the bitmap being written to '{path}' as a WebP.");

        var options = new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 100);
        using SKData data = pixmap.Encode(options)
            ?? throw new InvalidOperationException($"SKPixmap.Encode(WebP) failed writing '{path}'.");
        using FileStream file = File.Create(path);
        data.SaveTo(file);
    }

    /// <summary>
    /// Share of pixels differing from the image's most common colour. A capture that comes back
    /// near zero is a blank raster, which is the failure mode that looks like success.
    /// </summary>
    public static double InkFraction(SKBitmap bitmap)
    {
        if (bitmap.Width == 0 || bitmap.Height == 0)
        {
            throw new ArgumentException(
                $"Bitmap has no pixels ({bitmap.Width}x{bitmap.Height}). InkFraction cannot tell a " +
                "degenerate capture from a detected-blank one, so it refuses rather than returning 0.0.",
                nameof(bitmap));
        }

        var counts = new Dictionary<uint, int>();

        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                uint key = (uint)bitmap.GetPixel(x, y);
                counts[key] = counts.TryGetValue(key, out int existing) ? existing + 1 : 1;
            }
        }

        int total = bitmap.Width * bitmap.Height;
        int dominant = counts.Values.Max();

        return (double)(total - dominant) / total;
    }
}
