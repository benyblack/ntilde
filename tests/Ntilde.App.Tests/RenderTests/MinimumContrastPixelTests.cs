using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Ntilde.Platform;
using Ntilde.Rendering;
using Ntilde.Shell;
using Ntilde.Shell.Theming;
using Ntilde.Tests.Infra;
using Ntilde.VT;
using SkiaSharp;
using System;
using Xunit;

namespace Ntilde.Tests.RenderTests
{
    /// <summary>
    /// The Minimum contrast setting. The case it exists for is GitHub Light's: the theme's ANSI
    /// black is its own foreground, so a prompt segment painted black-on-default draws text the color
    /// of its background. Full blocks (U+2588) on the primitive path fill their cell, so a cell's
    /// centre pixel is the text color on any machine.
    /// </summary>
    /// <remarks>Capturing boots the headless platform, so this runs in the PlatformBoot lane.</remarks>
    [Collection("GoldenPng")]
    [Trait("Lane", "PlatformBoot")]
    public sealed class MinimumContrastPixelTests
    {
        private static readonly CellMetrics Metrics = new()
        {
            CellWidth = 8.4f,
            CellHeight = 18.0f,
            Baseline = 14.0f,
            Ascent = 14.0f,
            Descent = 4.0f
        };

        private static readonly SKColor Ink = new(0x24, 0x29, 0x2E); // GitHub Light's foreground and ANSI black

        [AvaloniaFact]
        public void Off_by_default_text_the_color_of_its_background_stays_invisible()
        {
            using var bitmap = Capture(minimumContrast: 1.0);

            Assert.Equal(Ink, CellCentre(bitmap, col: 0));
        }

        [AvaloniaFact]
        public void On_text_the_color_of_its_background_is_moved_until_it_reads()
        {
            using var bitmap = Capture(minimumContrast: 4.5);

            var text = CellCentre(bitmap, col: 0);
            double ratio = ThemeColorMath.Contrast(Color.FromRgb(text.Red, text.Green, text.Blue), Color.FromRgb(Ink.Red, Ink.Green, Ink.Blue));
            Assert.True(ratio >= 4.5, $"text {text} on {Ink}: {ratio:F2}:1");
        }

        [AvaloniaFact]
        public void Text_that_already_reads_is_left_alone()
        {
            using var bitmap = Capture(minimumContrast: 4.5);

            // Column 2: the theme's foreground on its white background, about 15:1.
            Assert.Equal(Ink, CellCentre(bitmap, col: 2));
        }

        private static SKBitmap Capture(double minimumContrast)
        {
            var buffer = new TerminalBuffer(6, 1)
            {
                Theme = new TerminalTheme
                {
                    Foreground = TermColor.FromRgb(Ink.Red, Ink.Green, Ink.Blue),
                    Background = TermColor.FromRgb(0xFF, 0xFF, 0xFF),
                    Black = TermColor.FromRgb(Ink.Red, Ink.Green, Ink.Blue),
                },
            };
            // A prompt segment on ANSI black in the default foreground, then plain text.
            new AnsiParser(buffer).Process("\x1b[40m██\x1b[0m██");

            using var glyphCache = new GlyphCache();
            byte[] png = SnapshotService.CapturePng(buffer, Metrics, (int)Math.Ceiling(6 * Metrics.CellWidth + 8), (int)Math.Ceiling(Metrics.CellHeight), new SnapshotCaptureOptions
            {
                ForceBlockElementPrimitives = true,
                GlyphCache = glyphCache,
                HideCursor = true,
                MinimumContrast = minimumContrast,
            });
            return SKBitmap.Decode(png);
        }

        private static SKColor CellCentre(SKBitmap bitmap, int col)
            => bitmap.GetPixel((int)(4 + (col + 0.5) * Metrics.CellWidth), (int)(Metrics.CellHeight / 2)).WithAlpha(0xFF);
    }
}
