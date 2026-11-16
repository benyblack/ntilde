using Avalonia.Input;
using Ntilde.Shell;
using Ntilde.VT;
using Xunit;

namespace Ntilde.Tests.Input
{
    /// <summary>
    /// Exact SGR-Pixels (?1016) reports, written from xterm's button.c (EditorButton / EmitButtonCode / EmitMousePosition) and the
    /// ctlseqs "SGR-Pixels (1016)" text: the ?1006 frame with the zero-based pixel offset plus one for both positions, no ceiling.
    /// The pixel fields of the event are the one-based wire positions; the cell fields never reach the wire in this mode.
    /// </summary>
    public sealed class PixelMouseCoordinateEncodingTests
    {
        private static ModeState Pixel(Action<ModeState>? more = null)
        {
            var modes = new ModeState { MouseModeX10 = true, MouseModePixel = true };
            more?.Invoke(modes);
            return modes;
        }

        private static TerminalMouseEvent At(TerminalMouseEventKind kind, TerminalMouseButton button, int pixelX, int pixelY,
            KeyModifiers modifiers = KeyModifiers.None)
            => new(kind, button, 3, 2, modifiers, pixelX, pixelY);

        [Theory]
        [InlineData(0, "\u001b[<0;123;45M")]
        [InlineData(1, "\u001b[<1;123;45M")]
        [InlineData(2, "\u001b[<2;123;45M")]
        public void Press_carries_the_pixel_position_in_the_sgr_frame(int button, string expected)
        {
            string? sequence = TerminalInputModeEncoder.EncodeMouseEvent(Pixel(), At(TerminalMouseEventKind.Press, (TerminalMouseButton)button, 123, 45), out bool legacyBytes);

            Assert.Equal(expected, sequence);
            Assert.False(legacyBytes);
        }

        [Theory]
        [InlineData(1, 1)]
        [InlineData(223, 224)]
        [InlineData(2015, 2016)]
        [InlineData(1920, 1081)]
        [InlineData(16384, 16384)]
        public void Positions_have_no_ceiling(int pixelX, int pixelY)
        {
            Assert.Equal($"\u001b[<0;{pixelX};{pixelY}M",
                TerminalInputModeEncoder.EncodeMouseEvent(Pixel(), At(TerminalMouseEventKind.Press, TerminalMouseButton.Left, pixelX, pixelY)));
        }

        [Fact]
        public void Release_keeps_the_button_and_ends_with_a_lowercase_m()
        {
            Assert.Equal("\u001b[<0;123;45m",
                TerminalInputModeEncoder.EncodeMouseEvent(Pixel(), At(TerminalMouseEventKind.Release, TerminalMouseButton.Left, 123, 45)));
            Assert.Equal("\u001b[<2;123;45m",
                TerminalInputModeEncoder.EncodeMouseEvent(Pixel(), At(TerminalMouseEventKind.Release, TerminalMouseButton.Right, 123, 45, KeyModifiers.None)));
        }

        [Theory]
        [InlineData(KeyModifiers.Shift, 4)]
        [InlineData(KeyModifiers.Alt, 8)]
        [InlineData(KeyModifiers.Control, 16)]
        [InlineData(KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Control, 28)]
        public void Modifier_bits_are_added_to_the_button_code(KeyModifiers modifiers, int bits)
        {
            Assert.Equal($"\u001b[<{bits};123;45M",
                TerminalInputModeEncoder.EncodeMouseEvent(Pixel(), At(TerminalMouseEventKind.Press, TerminalMouseButton.Left, 123, 45, modifiers)));
        }

        [Fact]
        public void Wheel_and_motion_report_the_same_pixel_position()
        {
            Assert.Equal("\u001b[<64;123;45M",
                TerminalInputModeEncoder.EncodeMouseEvent(Pixel(), At(TerminalMouseEventKind.Wheel, TerminalMouseButton.WheelUp, 123, 45)));
            Assert.Equal("\u001b[<65;123;45M",
                TerminalInputModeEncoder.EncodeMouseEvent(Pixel(), At(TerminalMouseEventKind.Wheel, TerminalMouseButton.WheelDown, 123, 45)));
            Assert.Equal("\u001b[<32;124;46M",
                TerminalInputModeEncoder.EncodeMouseEvent(Pixel(m => { m.MouseModeX10 = false; m.MouseModeButtonEvent = true; }),
                    At(TerminalMouseEventKind.Move, TerminalMouseButton.Left, 124, 46)));
            Assert.Equal("\u001b[<35;124;46M",
                TerminalInputModeEncoder.EncodeMouseEvent(Pixel(m => { m.MouseModeX10 = false; m.MouseModeAnyEvent = true; }),
                    At(TerminalMouseEventKind.Move, TerminalMouseButton.None, 124, 46)));
        }

        [Fact]
        public void X10_compatibility_tracking_sends_presses_only_without_modifier_bits()
        {
            var x10 = new ModeState { MouseModeX10PressOnly = true, MouseModePixel = true };
            Assert.Equal("\u001b[<0;123;45M", TerminalInputModeEncoder.EncodeMouseEvent(x10,
                At(TerminalMouseEventKind.Press, TerminalMouseButton.Left, 123, 45, KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Control)));
            Assert.Null(TerminalInputModeEncoder.EncodeMouseEvent(x10, At(TerminalMouseEventKind.Release, TerminalMouseButton.Left, 123, 45)));
        }

        [Fact]
        public void An_event_without_pixels_reads_as_the_first_pixel_and_never_as_cells()
        {
            Assert.Equal("\u001b[<0;1;1M",
                TerminalInputModeEncoder.EncodeMouseEvent(Pixel(), new TerminalMouseEvent(TerminalMouseEventKind.Press, TerminalMouseButton.Left, 96, 3, KeyModifiers.None)));
        }

        [Fact]
        public void Pixel_mode_is_not_used_when_another_encoding_is_the_only_active_one()
        {
            var sgr = new ModeState { MouseModeX10 = true, MouseModeSGR = true };
            Assert.Equal("\u001b[<0;3;2M", TerminalInputModeEncoder.EncodeMouseEvent(sgr, At(TerminalMouseEventKind.Press, TerminalMouseButton.Left, 123, 45)));
            var urxvt = new ModeState { MouseModeX10 = true, MouseModeUrxvt = true };
            Assert.Equal("\u001b[32;3;2M", TerminalInputModeEncoder.EncodeMouseEvent(urxvt, At(TerminalMouseEventKind.Press, TerminalMouseButton.Left, 123, 45)));
        }

        [Fact]
        public void A_snapshot_carrying_several_flags_prefers_pixels_then_sgr()
        {
            var all = new ModeState { MouseModeX10 = true, MouseModePixel = true, MouseModeSGR = true, MouseModeUrxvt = true, MouseModeUtf8 = true };
            Assert.Equal("\u001b[<0;123;45M", TerminalInputModeEncoder.EncodeMouseEvent(all, At(TerminalMouseEventKind.Press, TerminalMouseButton.Left, 123, 45)));
            all.MouseModePixel = false;
            Assert.Equal("\u001b[<0;3;2M", TerminalInputModeEncoder.EncodeMouseEvent(all, At(TerminalMouseEventKind.Press, TerminalMouseButton.Left, 123, 45)));
        }
    }
}
