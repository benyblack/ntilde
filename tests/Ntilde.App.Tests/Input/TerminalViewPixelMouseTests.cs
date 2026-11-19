using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Moq;
using Ntilde.Platform;
using Ntilde.Pty;
using Ntilde.Shell;
using Ntilde.VT;
using Xunit;

namespace Ntilde.Tests.Input
{
    /// <summary>
    /// SGR-Pixels (?1016) positions as the view derives them from a logical pointer position: device pixels (logical times the
    /// effective render scaling, the unit of CSI 14 t / CSI 16 t), zero-based inside the text area and sent plus one, clamped to
    /// the grid. The static conversion is the production path's pure core, so scales a headless window cannot reach are covered too.
    /// </summary>
    public class TerminalViewPixelMouseTests
    {
        private const float CellWidth = 8f;
        private const float CellHeight = 16f;

        private static (TerminalView View, Mock<ITerminalSession> Session, TerminalBuffer Buffer) CreateView(double scale = 1)
        {
            var session = new Mock<ITerminalSession>();
            var view = new TerminalView();
            var buffer = new TerminalBuffer(80, 24);
            view.SetBuffer(buffer);
            view.SetSession(session.Object);
            view.SetMetricsForTest(CellWidth, CellHeight);
            view.SetRenderScalingForTest(scale);
            return (view, session, buffer);
        }

        [Theory]
        [InlineData(1.0, 0.0, 0.0, 1, 1)]
        [InlineData(1.0, 3.9, 15.9, 4, 16)]
        [InlineData(1.0, 36.0, 376.0, 37, 377)]
        [InlineData(2.0, 10.25, 5.5, 21, 12)]
        [InlineData(2.0, 0.24, 0.49, 1, 1)]
        [InlineData(1.5, 10.0, 10.0, 16, 16)]
        public void The_position_is_the_floored_device_pixel_offset_plus_one(double scale, double x, double y, int expectedX, int expectedY)
        {
            Assert.Equal((expectedX, expectedY), TerminalView.ToMouseReportPixel(new Point(x, y), scale, CellWidth, CellHeight, 80, 24));
        }

        [Theory]
        [InlineData(1.0, 640, 384)]
        [InlineData(2.0, 1280, 768)]
        [InlineData(1.5, 960, 576)]
        public void The_position_is_clamped_to_the_text_area(double scale, int width, int height)
        {
            Assert.Equal((width, height), TerminalView.ToMouseReportPixel(new Point(100000, 100000), scale, CellWidth, CellHeight, 80, 24));
            Assert.Equal((1, 1), TerminalView.ToMouseReportPixel(new Point(-30, -0.5), scale, CellWidth, CellHeight, 80, 24));
            Assert.Equal((width, height), TerminalView.ToMouseReportPixel(new Point(80 * CellWidth, 24 * CellHeight), scale, CellWidth, CellHeight, 80, 24));
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(0.0)]
        [InlineData(-2.0)]
        [InlineData(double.PositiveInfinity)]
        public void An_unusable_scale_reads_as_one(double scale)
        {
            Assert.Equal((37, 377), TerminalView.ToMouseReportPixel(new Point(36, 376), scale, CellWidth, CellHeight, 80, 24));
        }

        [Fact]
        public void A_grid_without_a_measurable_size_reports_the_first_pixel()
        {
            Assert.Equal((1, 1), TerminalView.ToMouseReportPixel(new Point(36, 376), 1, 0, CellHeight, 80, 24));
            Assert.Equal((1, 1), TerminalView.ToMouseReportPixel(new Point(36, 376), 1, CellWidth, CellHeight, 0, 24));
        }

        [AvaloniaTheory]
        [InlineData(1.0, 37, 377)]
        [InlineData(2.0, 73, 753)]
        public void The_press_report_is_the_sgr_frame_with_that_position(double scale, int expectedX, int expectedY)
        {
            var (view, session, buffer) = CreateView(scale);
            buffer.Modes.MouseModeX10 = true;
            buffer.Modes.MouseModePixel = true;

            view.HandleMousePressAt(new Point(36, 376), TerminalMouseButton.Left, KeyModifiers.None);

            session.Verify(x => x.SendInput($"\x1b[<0;{expectedX};{expectedY}M"), Times.Once);
            session.Verify(x => x.SendInput(It.IsAny<string>()), Times.Once);
        }

        [AvaloniaFact]
        public void Without_the_pixel_encoding_the_same_press_stays_in_cells()
        {
            var (view, session, buffer) = CreateView(2);
            buffer.Modes.MouseModeX10 = true;
            buffer.Modes.MouseModeSGR = true;

            view.HandleMousePressAt(new Point(36, 376), TerminalMouseButton.Left, KeyModifiers.None);

            session.Verify(x => x.SendInput("\x1b[<0;5;24M"), Times.Once);
        }

        [AvaloniaFact]
        public void Motion_is_reported_for_every_new_pixel_and_coalesced_for_the_same_pixel()
        {
            var (view, session, buffer) = CreateView();
            buffer.Modes.MouseModeAnyEvent = true;
            buffer.Modes.MouseModePixel = true;

            // All four positions lie in the same cell (column 5, row 10): cell coalescing would send one report.
            view.HandleMouseMoveAt(new Point(40.2, 160.9), TerminalMouseButton.None, KeyModifiers.None);
            view.HandleMouseMoveAt(new Point(40.7, 160.2), TerminalMouseButton.None, KeyModifiers.None);
            view.HandleMouseMoveAt(new Point(41.1, 160.2), TerminalMouseButton.None, KeyModifiers.None);
            view.HandleMouseMoveAt(new Point(41.9, 160.4), TerminalMouseButton.None, KeyModifiers.None);

            session.Verify(x => x.SendInput("\x1b[<35;41;161M"), Times.Once);
            session.Verify(x => x.SendInput("\x1b[<35;42;161M"), Times.Once);
            session.Verify(x => x.SendInput(It.IsAny<string>()), Times.Exactly(2));
        }

        [AvaloniaFact]
        public void Switching_between_pixels_and_cells_restarts_the_coalescing()
        {
            var (view, session, buffer) = CreateView();
            buffer.Modes.MouseModeAnyEvent = true;
            buffer.Modes.MouseModeSGR = true;
            var position = new Point(40.2, 160.9);

            view.HandleMouseMoveAt(position, TerminalMouseButton.None, KeyModifiers.None);
            view.HandleMouseMoveAt(position, TerminalMouseButton.None, KeyModifiers.None);
            buffer.Modes.SetMouseCoordinateEncoding(1016, true);
            view.HandleMouseMoveAt(position, TerminalMouseButton.None, KeyModifiers.None);

            session.Verify(x => x.SendInput("\x1b[<35;6;11M"), Times.Once);
            session.Verify(x => x.SendInput("\x1b[<35;41;161M"), Times.Once);
            session.Verify(x => x.SendInput(It.IsAny<string>()), Times.Exactly(2));
        }

        [AvaloniaTheory]
        [InlineData(1.0)]
        [InlineData(2.0)]
        [InlineData(1.5)]
        public void A_declared_pixel_is_routed_at_its_centre_and_reports_back_as_itself(double scale)
        {
            var (view, _, _) = CreateView(scale);
            int width = (int)(80 * CellWidth * scale), height = (int)(24 * CellHeight * scale);
            foreach (var (pixelX, pixelY) in new[] { (0, 0), (1, 1), (7, 15), (123, 45), (width - 1, height - 1), (width / 2, height / 3) })
            {
                Assert.True(view.TryGetPixelPosition(pixelX, pixelY, out var position));
                Assert.Equal((pixelX + 1, pixelY + 1), view.ToMouseReportPixel(position));
            }
        }

        [Theory]
        [InlineData(0.0, 0.0, 1, 1)]
        [InlineData(36.0, 376.0, 59, 635)]
        [InlineData(7.99, 15.99, 13, 27)]
        [InlineData(8.0, 16.0, 14, 28)]
        public void A_session_cell_size_scales_each_axis_to_its_own_unit(double x, double y, int expectedX, int expectedY)
        {
            // Logical cell 8 x 16, the session reports 13 x 27 pixels: 1.625 and 1.6875 device pixels per logical pixel.
            Assert.Equal((expectedX, expectedY), TerminalView.ToMouseReportPixel(new Point(x, y), 13 / 8.0, 27 / 16.0, CellWidth, CellHeight, 80, 24));
        }

        [Fact]
        public void A_session_cell_size_bounds_the_text_area_it_reports()
        {
            Assert.Equal((80 * 13, 24 * 27), TerminalView.ToMouseReportPixel(new Point(100000, 100000), 13 / 8.0, 27 / 16.0, CellWidth, CellHeight, 80, 24));
            Assert.Equal((1, 1), TerminalView.ToMouseReportPixel(new Point(-5, -5), 13 / 8.0, 27 / 16.0, CellWidth, CellHeight, 80, 24));
        }

        [AvaloniaFact]
        public void A_mux_pane_reports_in_the_session_cell_size_whatever_its_own_scaling()
        {
            var (view, session, buffer) = CreateView(scale: 2);
            buffer.Modes.MouseModeX10 = true;
            buffer.Modes.MouseModePixel = true;
            SixelCellSize? reported = new SixelCellSize(13, 27);
            view.SessionPixelCellProvider = () => reported;

            view.HandleMousePressAt(new Point(36, 376), TerminalMouseButton.Left, KeyModifiers.None);
            session.Verify(x => x.SendInput("\x1b[<0;59;635M"), Times.Once);

            // Another client became the active one with other metrics: the next frame is in the new unit, with no change to this view.
            reported = new SixelCellSize(20, 40);
            view.HandleMousePressAt(new Point(36, 376), TerminalMouseButton.Left, KeyModifiers.None);
            session.Verify(x => x.SendInput("\x1b[<0;91;941M"), Times.Once);

            // No session size (a direct pane, or none reported yet): the view's own scaling, as before.
            reported = null;
            view.HandleMousePressAt(new Point(36, 376), TerminalMouseButton.Left, KeyModifiers.None);
            session.Verify(x => x.SendInput("\x1b[<0;73;753M"), Times.Once);
        }

        [AvaloniaTheory]
        [InlineData(0f, 27f)]
        [InlineData(13f, 0f)]
        [InlineData(float.NaN, 27f)]
        [InlineData(13f, float.PositiveInfinity)]
        [InlineData(-13f, 27f)]
        public void An_unusable_session_cell_size_falls_back_to_the_views_own_scaling(float width, float height)
        {
            var (view, _, _) = CreateView(scale: 2);
            view.SessionPixelCellProvider = () => new SixelCellSize(width, height);
            Assert.Equal((73, 753), view.ToMouseReportPixel(new Point(36, 376)));
        }

        [AvaloniaFact]
        public void A_mux_pane_reports_every_new_pixel_of_the_session_unit_and_coalesces_the_same_one()
        {
            var (view, session, buffer) = CreateView();
            buffer.Modes.MouseModeAnyEvent = true;
            buffer.Modes.MouseModePixel = true;
            view.SessionPixelCellProvider = () => new SixelCellSize(13, 27);

            // All three positions are in column 5 and in one device pixel of this view, but the session's unit tells the first two from the third.
            view.HandleMouseMoveAt(new Point(32.0, 100.0), TerminalMouseButton.None, KeyModifiers.None);
            view.HandleMouseMoveAt(new Point(32.4, 100.0), TerminalMouseButton.None, KeyModifiers.None);
            view.HandleMouseMoveAt(new Point(32.62, 100.0), TerminalMouseButton.None, KeyModifiers.None);

            session.Verify(x => x.SendInput("\x1b[<35;53;169M"), Times.Once);
            session.Verify(x => x.SendInput("\x1b[<35;54;169M"), Times.Once);
            session.Verify(x => x.SendInput(It.IsAny<string>()), Times.Exactly(2));
        }

        [AvaloniaFact]
        public void The_session_unit_is_computed_in_double_so_a_cell_boundary_and_the_extent_agree_with_the_reply()
        {
            var (view, _, buffer) = CreateView();
            view.SetMetricsForTest(9f, 13f);
            buffer.Resize(80, 30);
            view.SessionPixelCellProvider = () => new SixelCellSize(13f, 15.25f);

            // The 10th logical pixel column starts the 10th session pixel of a 13-pixel cell over a 9-pixel one: 9 x 13/9 is 13, not 12.99999976.
            Assert.Equal(14, view.ToMouseReportPixel(new Point(9, 0)).X);
            // 30 rows of 15.25 pixels are 457.5, which CSI 14 t reports as 458 (the text area bound).
            Assert.Equal((80 * 13, 458), view.ToMouseReportPixel(new Point(100000, 100000)));
        }

        [Theory]
        [InlineData(11f, 15f, 11, 16)]
        [InlineData(11f, 15f, 22, 31)]
        [InlineData(7f, 10f, 7, 11)]
        [InlineData(9f, 13f, 9, 14)]
        [InlineData(13f, 27f, 13, 28)]
        public void A_pointer_on_a_cell_boundary_lands_on_the_first_pixel_of_the_next_cell_of_the_session(float logicalCell, float sessionCell, int logicalX, int expectedX)
        {
            double scale = Math.Max(1.0, sessionCell) / (double)logicalCell;
            Assert.Equal(expectedX, TerminalView.ToMouseReportPixel(new Point(logicalX, 0), scale, scale, logicalCell, logicalCell, 80, 24).X);
        }

        [AvaloniaFact]
        public void A_session_cell_under_one_pixel_is_one_pixel_as_the_parser_reports_it()
        {
            var (view, _, _) = CreateView();
            view.SessionPixelCellProvider = () => new SixelCellSize(0.5f, 0.25f);
            // CSI 14 t reports 80 x 24 cells of 1 x 1 pixel: the text area is 80 x 24 pixels.
            Assert.Equal((80, 24), view.ToMouseReportPixel(new Point(100000, 100000)));
        }

        [AvaloniaFact]
        public void A_declared_pixel_in_the_session_unit_is_routed_at_its_centre_and_reports_back_as_itself()
        {
            var (view, _, _) = CreateView(scale: 2);
            view.SessionPixelCellProvider = () => new SixelCellSize(13, 27);
            foreach (var (pixelX, pixelY) in new[] { (0, 0), (12, 26), (13, 27), (500, 321), (80 * 13 - 1, 24 * 27 - 1) })
            {
                Assert.True(view.TryGetPixelPosition(pixelX, pixelY, out var position));
                Assert.Equal((pixelX + 1, pixelY + 1), view.ToMouseReportPixel(position));
            }

            Assert.False(view.TryGetPixelPosition(80 * 13, 0, out _));
            Assert.False(view.TryGetPixelPosition(0, 24 * 27, out _));
        }

        [Theory]
        [InlineData(1.25, 7.2, 80, 720)]
        [InlineData(1.0, 7.36, 80, 589)]
        public void The_text_area_extent_is_what_the_geometry_reply_rounds_to(double scale, double cellWidth, int cols, int expectedWidth)
        {
            Assert.True(TerminalView.TryGetTextAreaPixels(scale, cellWidth, 20, cols, 24, out _, out int width, out _));
            Assert.Equal(expectedWidth, width);
        }

        [AvaloniaTheory]
        [InlineData(-1, 0)]
        [InlineData(0, -1)]
        [InlineData(640, 0)]
        [InlineData(0, 384)]
        public void A_pixel_outside_the_text_area_is_refused(int pixelX, int pixelY)
        {
            var (view, _, _) = CreateView();
            Assert.False(view.TryGetPixelPosition(pixelX, pixelY, out _));
        }
    }
}
