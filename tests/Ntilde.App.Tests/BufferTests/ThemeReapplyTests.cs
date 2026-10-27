using Avalonia.Headless.XUnit;
using Ntilde.Shell;
using Ntilde.VT;
using Xunit;

namespace Ntilde.Tests.BufferTests
{
    /// <summary>
    /// Every settings save re-applies the settings to every pane. Re-applying an unchanged theme
    /// used to remap every cell and repaint every row of every pane - a whole-window flicker on a
    /// save that touched, say, the bell. An unchanged theme is now left alone; a changed one,
    /// including one edited in place, is still applied.
    /// </summary>
    public sealed class ThemeReapplyTests
    {
        private static TerminalTheme Theme(string name, TermColor foreground) => new()
        {
            Name = name,
            Foreground = foreground,
            Background = TermColor.FromRgb(0xfd, 0xf6, 0xe3),
        };

        // ThemeName must match, or the ActiveTheme getter re-resolves through ThemeManager.
        private static TerminalSettings SettingsFor(TerminalTheme theme)
            => new() { ThemeName = theme.Name, ActiveTheme = theme };

        private static TerminalView ViewWithText(TerminalTheme theme)
        {
            var buffer = new TerminalBuffer(10, 2) { Theme = theme };
            new AnsiParser(buffer).Process("hi");
            var view = new TerminalView();
            view.SetBuffer(buffer);
            view.ApplySettings(SettingsFor(theme));
            return view;
        }

        private static uint FirstCellForeground(TerminalBuffer buffer)
        {
            buffer.Lock.EnterReadLock();
            try { return buffer.GetCell(0, 0).Fg; }
            finally { buffer.Lock.ExitReadLock(); }
        }

        [AvaloniaFact]
        public void Reapplying_an_equal_theme_leaves_the_buffer_alone()
        {
            var theme = Theme("Probe", TermColor.FromRgb(0x65, 0x7b, 0x83));
            var view = ViewWithText(theme);

            view.ApplySettings(SettingsFor(theme.Clone()));

            Assert.Same(theme, view.Buffer!.Theme);
        }

        [AvaloniaFact]
        public void A_changed_theme_is_applied()
        {
            var view = ViewWithText(Theme("Probe", TermColor.FromRgb(0x65, 0x7b, 0x83)));
            var other = Theme("Other", TermColor.FromRgb(0x10, 0x20, 0x30));

            view.ApplySettings(SettingsFor(other));

            Assert.Same(other, view.Buffer!.Theme);
            Assert.Equal(other.Foreground.ToUint(), FirstCellForeground(view.Buffer));
        }

        /// <summary>The buffer holds the very instance that changed, so only the applied copy shows the edit.</summary>
        [AvaloniaFact]
        public void A_theme_edited_in_place_is_applied()
        {
            var theme = Theme("Probe", TermColor.FromRgb(0x65, 0x7b, 0x83));
            var view = ViewWithText(theme);

            theme.Foreground = TermColor.FromRgb(0x10, 0x20, 0x30);
            view.ApplySettings(SettingsFor(theme));

            Assert.Equal(theme.Foreground.ToUint(), FirstCellForeground(view.Buffer!));
        }

        [AvaloniaFact]
        public void Same_colors_compares_every_color_and_the_name()
        {
            var theme = Theme("Probe", TermColor.FromRgb(0x65, 0x7b, 0x83));
            Assert.True(theme.HasSameColors(theme.Clone()));

            var renamed = theme.Clone();
            renamed.Name = "Renamed";
            Assert.False(theme.HasSameColors(renamed));

            var paletteEdit = theme.Clone();
            paletteEdit.SetAnsiColor(6, bright: true, TermColor.FromRgb(1, 2, 3));
            Assert.False(theme.HasSameColors(paletteEdit));

            var optionalEdit = theme.Clone();
            optionalEdit.SearchMatch = TermColor.FromRgb(1, 2, 3);
            Assert.False(theme.HasSameColors(optionalEdit));
        }
    }
}
