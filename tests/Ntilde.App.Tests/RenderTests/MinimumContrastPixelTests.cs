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

        /// <summary>
        /// A luma rule calls pure red dark, so lightening alone would stop at 4:1 against it; black
        /// reaches 5.25:1. The search must try both directions.
        /// </summary>
        [AvaloniaFact]
        public void A_saturated_background_is_met_by_whichever_direction_reaches_the_ratio()
        {
            var theme = new TerminalTheme { Foreground = TermColor.White, Background = TermColor.Black };
            using var bitmap = Render(theme, "\x1b[38;2;255;0;0;48;2;255;0;0m██", 4.5, selection: null);

            var text = CellCentre(bitmap, col: 0);
            double ratio = ThemeColorMath.Contrast(Color.FromRgb(text.Red, text.Green, text.Blue), Color.FromRgb(0xFF, 0x00, 0x00));
            Assert.True(ratio >= 4.5, $"text {text} on red: {ratio:F2}:1");
        }

        /// <summary>
        /// Text redrawn over a selection is measured against the selection, not the cell under it.
        /// Black text on a black cell under a #757575 selection already reads at 4.6:1; measured
        /// against the cell it was "fixed" to grey, the selection's own color.
        /// </summary>
        [AvaloniaFact]
        public void Selected_text_is_measured_against_the_selection()
        {
            var theme = new TerminalTheme
            {
                Foreground = TermColor.White,
                Background = TermColor.Black,
                Black = TermColor.Black,
                SelectionBackground = TermColor.FromRgb(0x75, 0x75, 0x75),
            };
            using var bitmap = Render(theme, "\x1b[30m██", 4.5, new SelectionState { IsActive = true, Start = (0, 0), End = (0, 1) });

            Assert.Equal(SKColors.Black, CellCentre(bitmap, col: 0));
        }

        private static SKBitmap Capture(double minimumContrast)
        {
            var theme = new TerminalTheme
            {
                Foreground = TermColor.FromRgb(Ink.Red, Ink.Green, Ink.Blue),
                Background = TermColor.FromRgb(0xFF, 0xFF, 0xFF),
                Black = TermColor.FromRgb(Ink.Red, Ink.Green, Ink.Blue),
            };
            // A prompt segment on ANSI black in the default foreground, then plain text.
            return Render(theme, "\x1b[40m██\x1b[0m██", minimumContrast, selection: null);
        }

        private static SKBitmap Render(TerminalTheme theme, string content, double minimumContrast, SelectionState? selection)
        {
            var buffer = new TerminalBuffer(6, 1) { Theme = theme };
            new AnsiParser(buffer).Process(content);

            using var glyphCache = new GlyphCache();
            byte[] png = SnapshotService.CapturePng(buffer, Metrics, (int)Math.Ceiling(6 * Metrics.CellWidth + 8), (int)Math.Ceiling(Metrics.CellHeight), new SnapshotCaptureOptions
            {
                ForceBlockElementPrimitives = true,
                GlyphCache = glyphCache,
                HideCursor = true,
                Selection = selection,
                MinimumContrast = minimumContrast,
            });
            return SKBitmap.Decode(png);
        }

        private static SKColor CellCentre(SKBitmap bitmap, int col)
            => bitmap.GetPixel((int)(4 + (col + 0.5) * Metrics.CellWidth), (int)(Metrics.CellHeight / 2)).WithAlpha(0xFF);
    }
}
