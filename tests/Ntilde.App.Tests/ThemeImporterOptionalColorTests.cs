using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Ntilde.Shell.ThemeImporters;
using Ntilde.VT;
using Xunit;

namespace Ntilde.Tests
{
    /// <summary>
    /// The importers carry the colors they used to drop (spec §6): selection, selected text, cursor
    /// text and search. Fixtures follow each format's upstream layout (Dracula's published ports).
    /// </summary>
    public class ThemeImporterOptionalColorTests
    {
        private static readonly TermColor DraculaBackground = TermColor.FromRgb(0x28, 0x2A, 0x36);
        private static readonly TermColor DraculaForeground = TermColor.FromRgb(0xF8, 0xF8, 0xF2);
        private static readonly TermColor DraculaSelection = TermColor.FromRgb(0x44, 0x47, 0x5A);

        [Fact]
        public void Windows_Terminal_carries_selectionBackground()
        {
            var theme = Assert.Single(new WindowsTerminalImporter().Import(Fixture("dracula-wt.json")));

            Assert.Equal(DraculaSelection, theme.SelectionBackground);
            Assert.Null(theme.SelectionForeground);
        }

        [Fact]
        public void Windows_Terminal_without_selectionBackground_leaves_it_derived()
        {
            string path = Path.Combine(Path.GetTempPath(), $"ntilde_wt_{Guid.NewGuid():N}.json");
            File.WriteAllText(path, """{ "name": "Plain", "background": "#101010", "foreground": "#E0E0E0" }""");
            try
            {
                Assert.Null(Assert.Single(new WindowsTerminalImporter().Import(path)).SelectionBackground);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void ITerm2_carries_selection_selected_text_and_cursor_text()
        {
            var theme = Assert.Single(new ITerm2Importer().Import(Fixture("dracula.itermcolors")));

            Assert.Equal(DraculaSelection, theme.SelectionBackground);
            Assert.Equal(TermColor.FromRgb(0xFF, 0xFF, 0xFF), theme.SelectionForeground);
            Assert.Equal(DraculaBackground, theme.CursorText);
            Assert.Equal(DraculaBackground, theme.Background);
            Assert.Equal(TermColor.FromRgb(0xFF, 0x55, 0x55), theme.Red);
        }

        /// <summary>
        /// Components exported from 8-bit values come back a hair under (241.99999...); truncating
        /// lost a unit per channel. The fixture's foreground blue is exactly such a value.
        /// </summary>
        [Fact]
        public void ITerm2_components_round_rather_than_truncate()
        {
            var theme = Assert.Single(new ITerm2Importer().Import(Fixture("dracula.itermcolors")));

            Assert.Equal(DraculaForeground, theme.Foreground);
        }

        /// <summary>Plist reals are always "0.5"; a comma-decimal locale used to misread every one.</summary>
        [Fact]
        public void ITerm2_reads_the_same_under_a_comma_decimal_locale()
        {
            var previous = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            try
            {
                var theme = Assert.Single(new ITerm2Importer().Import(Fixture("dracula.itermcolors")));

                Assert.Equal(DraculaBackground, theme.Background);
                Assert.Equal(DraculaSelection, theme.SelectionBackground);
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        [Fact]
        public void Alacritty_reads_quoted_hex_colors()
        {
            // `#` starts a TOML comment only outside quotes; the old parser cut "#282a36" at the
            // `#`, so every color in a real Alacritty theme was dropped.
            var theme = Assert.Single(new AlacrittyImporter().Import(Fixture("dracula.toml")));

            Assert.Equal(DraculaBackground, theme.Background);
            Assert.Equal(DraculaForeground, theme.Foreground);
            Assert.Equal(TermColor.FromRgb(0xBD, 0x93, 0xF9), theme.Blue);
            Assert.Equal(TermColor.FromRgb(0xFF, 0xFF, 0xFF), theme.BrightWhite);
        }

        [Fact]
        public void Alacritty_carries_selection_and_search_and_maps_cell_keywords_to_derived()
        {
            var theme = Assert.Single(new AlacrittyImporter().Import(Fixture("dracula.toml")));

            Assert.Equal(DraculaSelection, theme.SelectionBackground);
            Assert.Equal(TermColor.FromRgb(0x50, 0xFA, 0x7B), theme.SearchMatch);
            Assert.Equal(TermColor.FromRgb(0xFF, 0xB8, 0x6C), theme.SearchMatchActive);
            // text = "CellForeground" / "CellBackground": the cell's own color, i.e. derived.
            Assert.Null(theme.SelectionForeground);
            Assert.Null(theme.CursorText);
            // cursor = "CellForeground" names no color either; the cursor keeps the theme default.
            Assert.Equal(new TerminalTheme().CursorColor, theme.CursorColor);
        }

        [Fact]
        public void Alacritty_reads_inline_tables_and_0x_hex()
        {
            var theme = Assert.Single(new AlacrittyImporter().Import(Fixture("inline-0x.toml")));

            Assert.Equal(TermColor.FromRgb(0x10, 0x10, 0x10), theme.Background);
            Assert.Equal(TermColor.FromRgb(0xE0, 0xE0, 0xE0), theme.CursorColor);
            Assert.Equal(TermColor.FromRgb(0x10, 0x10, 0x10), theme.CursorText);
            Assert.Equal(TermColor.FromRgb(0x33, 0x55, 0x77), theme.SelectionBackground);
            Assert.Equal(TermColor.FromRgb(0xFF, 0xFF, 0xFF), theme.SelectionForeground);
            Assert.Equal(TermColor.FromRgb(0xAA, 0xAA, 0x00), theme.SearchMatch);
            Assert.Equal(TermColor.FromRgb(0xFF, 0x88, 0x00), theme.SearchMatchActive);
        }

        private static string Fixture(string name)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "../../../Fixtures/Themes", name);
            Assert.True(File.Exists(path), $"Missing fixture {path}");
            return path;
        }
    }
}
