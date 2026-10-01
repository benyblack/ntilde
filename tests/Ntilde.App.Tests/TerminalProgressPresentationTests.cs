using Ntilde.Shell;
using Xunit;

namespace Ntilde.Tests
{
    /// <summary>
    /// OSC 9;4 progress (issue #271): the parser's raw (state, percent) mapping
    /// (<see cref="TerminalProgressReport.FromOsc"/>) and the pure presentation decisions
    /// (<see cref="TabProgressPresentation"/>). Plain facts, no window (same split as
    /// <see cref="TabStatusPresentationTests"/>).
    /// </summary>
    public sealed class TerminalProgressPresentationTests
    {
        // ---- FromOsc: state mapping ----

        [Theory]
        [InlineData(0)] // off / withdraw
        [InlineData(5)] // unknown: ignored, not guessed at
        [InlineData(-1)]
        public void FromOsc_off_and_unknown_states_map_to_null(int state)
            => Assert.Null(TerminalProgressReport.FromOsc(state, 50));

        [Fact]
        public void FromOsc_normal_carries_percent()
            => Assert.Equal(new TerminalProgressReport(TerminalProgressKind.Normal, 42),
                TerminalProgressReport.FromOsc(1, 42));

        [Fact]
        public void FromOsc_error_carries_percent()
            => Assert.Equal(new TerminalProgressReport(TerminalProgressKind.Error, 7),
                TerminalProgressReport.FromOsc(2, 7));

        [Fact]
        public void FromOsc_indeterminate_drops_percent()
            => Assert.Equal(new TerminalProgressReport(TerminalProgressKind.Indeterminate, null),
                TerminalProgressReport.FromOsc(3, 90));

        [Fact]
        public void FromOsc_paused_carries_percent()
            => Assert.Equal(new TerminalProgressReport(TerminalProgressKind.Paused, 30),
                TerminalProgressReport.FromOsc(4, 30));

        [Fact]
        public void FromOsc_normal_without_percent_keeps_null()
            => Assert.Equal(new TerminalProgressReport(TerminalProgressKind.Normal, null),
                TerminalProgressReport.FromOsc(1, null));

        // ---- FormatMarkerSuffix: horizontal tab title ----

        [Fact]
        public void MarkerSuffix_is_empty_for_no_progress()
        {
            Assert.Equal(string.Empty, TabProgressPresentation.FormatMarkerSuffix(null));
            Assert.Equal(string.Empty, TabProgressPresentation.FormatMarkerSuffix(
                new TerminalProgressReport(TerminalProgressKind.None, null)));
        }

        [Fact]
        public void MarkerSuffix_normal_shows_bare_percent()
            => Assert.Equal(" 42%", TabProgressPresentation.FormatMarkerSuffix(
                new TerminalProgressReport(TerminalProgressKind.Normal, 42)));

        [Fact]
        public void MarkerSuffix_error_prefixes_cross()
            => Assert.Equal(" ✖ 42%", TabProgressPresentation.FormatMarkerSuffix(
                new TerminalProgressReport(TerminalProgressKind.Error, 42)));

        [Fact]
        public void MarkerSuffix_paused_without_percent_shows_glyph_only()
            => Assert.Equal(" ⏸", TabProgressPresentation.FormatMarkerSuffix(
                new TerminalProgressReport(TerminalProgressKind.Paused, null)));

        [Fact]
        public void MarkerSuffix_indeterminate_and_percentless_normal_show_ellipsis()
        {
            Assert.Equal(" ⋯", TabProgressPresentation.FormatMarkerSuffix(
                new TerminalProgressReport(TerminalProgressKind.Indeterminate, null)));
            Assert.Equal(" ⋯", TabProgressPresentation.FormatMarkerSuffix(
                new TerminalProgressReport(TerminalProgressKind.Normal, null)));
        }

        // ---- ResolveBar: vertical header ----

        [Fact]
        public void Bar_is_hidden_for_no_progress()
        {
            Assert.Equal((false, false, 0.0), TabProgressPresentation.ResolveBar(null));
            Assert.Equal((false, false, 0.0), TabProgressPresentation.ResolveBar(
                new TerminalProgressReport(TerminalProgressKind.None, null)));
        }

        [Fact]
        public void Bar_normal_shows_percent_value()
            => Assert.Equal((true, false, 42.0), TabProgressPresentation.ResolveBar(
                new TerminalProgressReport(TerminalProgressKind.Normal, 42)));

        [Fact]
        public void Bar_indeterminate_and_percentless_normal_go_indeterminate()
        {
            Assert.Equal((true, true, 0.0), TabProgressPresentation.ResolveBar(
                new TerminalProgressReport(TerminalProgressKind.Indeterminate, null)));
            Assert.Equal((true, true, 0.0), TabProgressPresentation.ResolveBar(
                new TerminalProgressReport(TerminalProgressKind.Normal, null)));
        }

        [Fact]
        public void Bar_error_and_paused_still_show_their_value()
        {
            Assert.Equal((true, false, 7.0), TabProgressPresentation.ResolveBar(
                new TerminalProgressReport(TerminalProgressKind.Error, 7)));
            Assert.Equal((true, false, 30.0), TabProgressPresentation.ResolveBar(
                new TerminalProgressReport(TerminalProgressKind.Paused, 30)));
        }

        // ---- ResolveTaskbar: ITaskbarList3 mapping ----

        [Fact]
        public void Taskbar_is_cleared_for_no_progress()
        {
            Assert.Equal((TabProgressPresentation.TaskbarFlagNoProgress, (int?)null),
                TabProgressPresentation.ResolveTaskbar(null));
        }

        [Fact]
        public void Taskbar_normal_maps_value_over_100()
            => Assert.Equal((TabProgressPresentation.TaskbarFlagNormal, (int?)42),
                TabProgressPresentation.ResolveTaskbar(new TerminalProgressReport(TerminalProgressKind.Normal, 42)));

        [Fact]
        public void Taskbar_error_and_paused_map_their_flags()
        {
            Assert.Equal((TabProgressPresentation.TaskbarFlagError, (int?)7),
                TabProgressPresentation.ResolveTaskbar(new TerminalProgressReport(TerminalProgressKind.Error, 7)));
            Assert.Equal((TabProgressPresentation.TaskbarFlagPaused, (int?)30),
                TabProgressPresentation.ResolveTaskbar(new TerminalProgressReport(TerminalProgressKind.Paused, 30)));
        }

        [Fact]
        public void Taskbar_indeterminate_maps_flag_without_value()
            => Assert.Equal((TabProgressPresentation.TaskbarFlagIndeterminate, (int?)null),
                TabProgressPresentation.ResolveTaskbar(new TerminalProgressReport(TerminalProgressKind.Indeterminate, null)));

        [Fact]
        public void Taskbar_percentless_states_report_zero_value()
        {
            // A flag without a value is legal for indeterminate only; the percentless
            // error/paused/normal shapes still call SetProgressValue so the colour the
            // taskbar paints has a width to paint.
            Assert.Equal((TabProgressPresentation.TaskbarFlagNormal, (int?)0),
                TabProgressPresentation.ResolveTaskbar(new TerminalProgressReport(TerminalProgressKind.Normal, null)));
            Assert.Equal((TabProgressPresentation.TaskbarFlagError, (int?)0),
                TabProgressPresentation.ResolveTaskbar(new TerminalProgressReport(TerminalProgressKind.Error, null)));
        }
    }
}
