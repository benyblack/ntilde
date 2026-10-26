using Avalonia.Headless.XUnit;
using Ntilde.Platform;
using Ntilde.Rendering;
using Ntilde.Shell;
using Ntilde.Tests.Infra;
using Ntilde.VT;
using SkiaSharp;
using System;
using Xunit;

namespace Ntilde.Tests.RenderTests
{
    /// <summary>
    /// The renderer draws selection, search and the block cursor from the theme (spec §3
    /// "Renderer"). Full blocks (U+2588) on the primitive path fill their cell exactly, so the
    /// centre pixel of a cell is the glyph's color on any machine, with no font involved.
    /// </summary>
    /// <remarks>Capturing boots the headless platform, so this runs in the PlatformBoot lane.</remarks>
    [Collection("GoldenPng")]
    [Trait("Lane", "PlatformBoot")]
    public sealed class ThemeRenderColorPixelTests
    {
        private static readonly CellMetrics Metrics = new()
        {
            CellWidth = 8.4f,
            CellHeight = 18.0f,
            Baseline = 14.0f,
            Ascent = 14.0f,
            Descent = 4.0f
        };

        private static readonly TermColor Green = TermColor.FromRgb(0x00, 0xFF, 0x00);
        private static readonly TermColor Orange = TermColor.FromRgb(0xFF, 0x66, 0x00);
        private static readonly TermColor Navy = TermColor.FromRgb(0x10, 0x20, 0x80);

        /// <summary>The block cursor used to be an opaque rect that hid the glyph under it.</summary>
        [AvaloniaFact]
        public void A_block_cursor_draws_the_glyph_under_it_in_the_cursor_text_color()
        {
            var buffer = Buffer(theme => theme.CursorText = Green);
            new AnsiParser(buffer).Process("████");
            buffer.Modes.CursorStyle = CursorStyle.Block;
            buffer.CursorRow = 0;
            buffer.CursorCol = 1;

            using var bitmap = Capture(buffer, selection: null);

            Assert.Equal(ToSk(Green), CellCentre(bitmap, col: 1, row: 0));
            Assert.Equal(SKColors.White, CellCentre(bitmap, col: 2, row: 0));
        }

        [AvaloniaFact]
        public void Selected_text_takes_an_explicit_selection_foreground()
        {
            var buffer = Buffer(theme =>
            {
                theme.SelectionBackground = Navy;
                theme.SelectionForeground = Green;
            });
            new AnsiParser(buffer).Process("████   ");

            using var bitmap = Capture(buffer, new SelectionState { IsActive = true, Start = (0, 1), End = (0, 5) });

            Assert.Equal(SKColors.White, CellCentre(bitmap, col: 0, row: 0)); // unselected text
            Assert.Equal(ToSk(Green), CellCentre(bitmap, col: 2, row: 0));    // selected text
            Assert.Equal(ToSk(Navy), CellCentre(bitmap, col: 5, row: 0));     // selected blank: the fill
        }

        /// <summary>
        /// An opaque theme selection must not hide the text it covers: the text is drawn over it,
        /// in its own color when the theme names no selection foreground.
        /// </summary>
        [AvaloniaFact]
        public void An_opaque_selection_is_drawn_under_the_text()
        {
            var buffer = Buffer(theme => theme.SelectionBackground = Navy);
            new AnsiParser(buffer).Process("██ ");

            using var bitmap = Capture(buffer, new SelectionState { IsActive = true, Start = (0, 0), End = (0, 2) });

            Assert.Equal(SKColors.White, CellCentre(bitmap, col: 1, row: 0));
            Assert.Equal(ToSk(Navy), CellCentre(bitmap, col: 2, row: 0));
        }

        [AvaloniaFact]
        public void The_selection_fill_comes_from_the_theme()
        {
            var buffer = Buffer(theme => theme.SelectionBackground = Orange);
            new AnsiParser(buffer).Process("   ");

            using var bitmap = Capture(buffer, new SelectionState { IsActive = true, Start = (0, 0), End = (0, 1) });

            Assert.Equal(ToSk(Orange), CellCentre(bitmap, col: 0, row: 0));
            Assert.Equal(SKColors.Black, CellCentre(bitmap, col: 2, row: 0));
        }

        private static TerminalBuffer Buffer(Action<TerminalTheme> configure)
        {
            var theme = new TerminalTheme
            {
                Foreground = TermColor.White,
                Background = TermColor.Black,
                CursorColor = TermColor.FromRgb(0xFF, 0x00, 0x00),
            };
            configure(theme);
            return new TerminalBuffer(8, 2) { Theme = theme };
        }

        private static SKBitmap Capture(TerminalBuffer buffer, SelectionState? selection)
        {
            // The glyph cache is what puts U+2588 on the primitive path (see GoldenSharedPngTests).
            using var glyphCache = new GlyphCache();
            byte[] png = SnapshotService.CapturePng(buffer, Metrics, (int)Math.Ceiling(8 * Metrics.CellWidth + 8), (int)Math.Ceiling(2 * Metrics.CellHeight), new SnapshotCaptureOptions
            {
                ForceBlockElementPrimitives = true,
                GlyphCache = glyphCache,
                Selection = selection,
                HideCursor = selection != null,
            });
            return SKBitmap.Decode(png);
        }

        private static SKColor CellCentre(SKBitmap bitmap, int col, int row)
        {
            var pixel = bitmap.GetPixel((int)(4 + (col + 0.5) * Metrics.CellWidth), (int)((row + 0.5) * Metrics.CellHeight));
            return pixel.WithAlpha(0xFF);
        }

        private static SKColor ToSk(TermColor color) => new((byte)color.R, (byte)color.G, (byte)color.B);
    }
}
