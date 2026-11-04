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
    /// DECSCNM (?5, #831) as drawn. xterm semantics: the default foreground and background trade
    /// places for the whole screen; explicit RGB and indexed colors keep their values; an SGR 7 cell
    /// swaps its resolved pair as usual, so a default-colored inverse cell appears normal. Full blocks
    /// (U+2588) on the primitive path fill their cell exactly, so a cell's centre pixel is the glyph's
    /// color and a blank's is its background, with no font involved.
    /// </summary>
    /// <remarks>Capturing boots the headless platform, so this runs in the PlatformBoot lane.</remarks>
    [Collection("GoldenPng")]
    [Trait("Lane", "PlatformBoot")]
    public sealed class ReverseVideoRenderTests
    {
        private static readonly CellMetrics Metrics = new()
        {
            CellWidth = 8.4f,
            CellHeight = 18.0f,
            Baseline = 14.0f,
            Ascent = 14.0f,
            Descent = 4.0f
        };

        private const int Cols = 12;
        private const int Rows = 2;

        private static readonly TermColor Green = TermColor.FromRgb(0x00, 0xFF, 0x00);
        private static readonly TermColor Navy = TermColor.FromRgb(0x10, 0x20, 0x80);

        // One cell per case, in column order.
        private const string Cases =
            "█" +                                   // 0: default glyph
            " " +                                   // 1: default blank
            "\u001b[7m█ \u001b[0m" +                // 2, 3: SGR 7 on defaults
            "\u001b[38;2;0;255;0m█\u001b[0m" +      // 4: explicit RGB foreground
            "\u001b[48;2;16;32;128m \u001b[0m" +    // 5: explicit RGB background
            "\u001b[31m█\u001b[0m" +                // 6: indexed foreground
            "\u001b[44m \u001b[0m" +                // 7: indexed background
            "\u001b[38;2;0;255;0;7m \u001b[0m" +    // 8: SGR 7 over an explicit foreground
            "\u001b[48;2;16;32;128;7m \u001b[0m";   // 9: SGR 7 over an explicit background

        [AvaloniaFact]
        public void Default_explicit_and_inverse_cells_follow_xterm_in_both_mode_states()
        {
            var buffer = Buffer();
            var parser = new AnsiParser(buffer);
            parser.Process(Cases);
            SKColor white = SKColors.White, black = SKColors.Black, green = ToSk(Green), navy = ToSk(Navy);
            SKColor red = ToSk(buffer.Theme.Red), blue = ToSk(buffer.Theme.Blue);

            SKColor[] normal = [white, black, black, white, green, navy, red, blue, green, white];
            SKColor[] reversed = [black, white, white, black, green, navy, red, blue, green, black];

            AssertCells(buffer, normal, "normal video");
            parser.Process("\u001b[?5h");
            AssertCells(buffer, reversed, "DECSCNM set");
            parser.Process("\u001b[?5l");
            AssertCells(buffer, normal, "DECSCNM reset");
        }

        [AvaloniaFact]
        public void The_unwritten_screen_and_padding_take_the_presented_background()
        {
            var buffer = Buffer();
            new AnsiParser(buffer).Process("\u001b[?5h");

            using var bitmap = Capture(buffer, selection: null);

            Assert.Equal(SKColors.White, CellCentre(bitmap, col: Cols - 1, row: Rows - 1));
            Assert.Equal(SKColors.White, bitmap.GetPixel(1, 1).WithAlpha(0xFF)); // left padding
        }

        /// <summary>
        /// A cursor that matched the default foreground would vanish on the swapped background; it
        /// follows the swap, and the glyph under a block cursor keeps its contrast with it.
        /// </summary>
        [AvaloniaTheory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_block_cursor_that_tracked_the_foreground_keeps_its_contrast(bool reverse)
        {
            var buffer = Buffer(theme => theme.CursorColor = TermColor.White);
            var parser = new AnsiParser(buffer);
            parser.Process("█ " + (reverse ? "\u001b[?5h" : ""));
            buffer.Modes.CursorStyle = CursorStyle.Block;
            SKColor cursor = reverse ? SKColors.Black : SKColors.White;
            SKColor background = reverse ? SKColors.White : SKColors.Black;

            buffer.CursorRow = 0;
            buffer.CursorCol = 1;
            using (var onBlank = Capture(buffer, selection: null, hideCursor: false))
            {
                Assert.Equal(cursor, CellCentre(onBlank, col: 1, row: 0));
                Assert.Equal(background, CellCentre(onBlank, col: 2, row: 0));
            }

            buffer.CursorCol = 0;
            using var onGlyph = Capture(buffer, selection: null, hideCursor: false);
            // The cursor text color derives from the presented background.
            Assert.Equal(background, CellCentre(onGlyph, col: 0, row: 0));
        }

        [AvaloniaFact]
        public void An_explicit_selection_keeps_its_fill_and_selected_text_stays_readable()
        {
            var buffer = Buffer(theme => theme.SelectionBackground = Navy);
            new AnsiParser(buffer).Process("██  \u001b[?5h");

            using var bitmap = Capture(buffer, new SelectionState { IsActive = true, Start = (0, 0), End = (0, 3) });

            Assert.Equal(ToSk(Navy), CellCentre(bitmap, col: 2, row: 0));
            // The presented text color (black) on navy would not read; the selection foreground is
            // derived against the presented colors, so the selected glyph clears the text contrast.
            SKColor selectedText = CellCentre(bitmap, col: 0, row: 0);
            Assert.True(Contrast(selectedText, ToSk(Navy)) >= 4.5, $"selected text {selectedText} on navy");
        }

        /// <summary>
        /// The live view's path: the row-picture cache and the span diff. Toggling the mode must
        /// repaint every row, and toggling it back must restore identical pixels.
        /// </summary>
        [AvaloniaFact]
        public void Live_row_cache_repaints_on_toggle_and_restores_identical_pixels()
        {
            using var harness = new DrawPathHarness(Cols, 3);
            harness.Parser.Process("\u001b[?25l" + Cases + "\r\n\u001b[1;32mok\u001b[0m");
            harness.DrawFrame();
            byte[] before = harness.Pixels();

            harness.Parser.Process("\u001b[?5h");
            harness.DrawFrame();
            byte[] reversed = harness.Pixels();
            Assert.NotEqual(before, reversed);
            harness.DrawFrame();
            Assert.Equal(reversed, harness.Pixels());

            harness.Parser.Process("\u001b[?5l");
            harness.DrawFrame();
            Assert.Equal(before, harness.Pixels());
        }

        private static void AssertCells(TerminalBuffer buffer, SKColor[] expected, string state)
        {
            using var bitmap = Capture(buffer, selection: null);
            for (int col = 0; col < expected.Length; col++)
            {
                SKColor actual = CellCentre(bitmap, col, row: 0);
                Assert.True(expected[col] == actual, $"{state}: cell {col} was {actual}, expected {expected[col]}");
            }
        }

        private static TerminalBuffer Buffer(Action<TerminalTheme>? configure = null)
        {
            var theme = new TerminalTheme
            {
                Foreground = TermColor.White,
                Background = TermColor.Black,
                CursorColor = TermColor.FromRgb(0xFF, 0x00, 0x00),
            };
            configure?.Invoke(theme);
            return new TerminalBuffer(Cols, Rows) { Theme = theme };
        }

        private static SKBitmap Capture(TerminalBuffer buffer, SelectionState? selection, bool hideCursor = true)
        {
            // The glyph cache is what puts U+2588 on the primitive path (see GoldenSharedPngTests).
            using var glyphCache = new GlyphCache();
            byte[] png = SnapshotService.CapturePng(buffer, Metrics, (int)Math.Ceiling(Cols * Metrics.CellWidth + 8), (int)Math.Ceiling(Rows * Metrics.CellHeight), new SnapshotCaptureOptions
            {
                ForceBlockElementPrimitives = true,
                GlyphCache = glyphCache,
                Selection = selection,
                HideCursor = hideCursor,
            });
            return SKBitmap.Decode(png);
        }

        private static SKColor CellCentre(SKBitmap bitmap, int col, int row)
        {
            var pixel = bitmap.GetPixel((int)(4 + (col + 0.5) * Metrics.CellWidth), (int)((row + 0.5) * Metrics.CellHeight));
            return pixel.WithAlpha(0xFF);
        }

        private static SKColor ToSk(TermColor color) => new((byte)color.R, (byte)color.G, (byte)color.B);

        private static double Contrast(SKColor a, SKColor b)
        {
            static double Channel(byte c)
            {
                double s = c / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }

            static double Luminance(SKColor c) => 0.2126 * Channel(c.Red) + 0.7152 * Channel(c.Green) + 0.0722 * Channel(c.Blue);
            double la = Luminance(a), lb = Luminance(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }
    }
}
