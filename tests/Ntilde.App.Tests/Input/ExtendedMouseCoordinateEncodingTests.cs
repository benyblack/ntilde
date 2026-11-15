using Avalonia.Input;
using Ntilde.Shell;
using Ntilde.VT;
using Xunit;

namespace Ntilde.Tests.Input
{
    /// <summary>
    /// Exact reports for the two extended coordinate encodings, written from the xterm control sequence
    /// definitions (ctlseqs "Extended coordinates"; button.c EmitMousePosition/EditorButton), not derived from the encoder.
    /// </summary>
    public sealed class ExtendedMouseCoordinateEncodingTests
    {
        private static TerminalMouseEvent Press(int column, int row, KeyModifiers modifiers = KeyModifiers.None, TerminalMouseButton button = TerminalMouseButton.Left)
            => new(TerminalMouseEventKind.Press, button, column, row, modifiers);

        private static ModeState Utf8(Action<ModeState>? more = null)
        {
            var modes = new ModeState { MouseModeX10 = true, MouseModeUtf8 = true };
            more?.Invoke(modes);
            return modes;
        }

        private static ModeState Urxvt(Action<ModeState>? more = null)
        {
            var modes = new ModeState { MouseModeX10 = true, MouseModeUrxvt = true };
            more?.Invoke(modes);
            return modes;
        }

        // Column and row use the same position so every boundary is exercised for both coordinates.
        // value = position + 32 code point: one byte through position 95 (127), two bytes from 96 (U+0080) up to 2015 (U+07FF).
        [Theory]
        [InlineData(1, "!")]
        [InlineData(94, "~")]
        [InlineData(95, "\u007f")]
        [InlineData(96, "\u0080")]
        [InlineData(97, "\u0081")]
        [InlineData(223, "ÿ")]
        [InlineData(224, "Ā")]
        [InlineData(2015, "߿")]
        [InlineData(2016, "߿")]   // policy: clamped to the largest encodable position
        [InlineData(5000, "߿")]
        public void Utf8_press_carries_each_position_as_a_code_point(int position, string expectedCoordinate)
        {
            string? sequence = TerminalInputModeEncoder.EncodeMouseEvent(Utf8(), Press(position, position), out bool legacyBytes);

            Assert.Equal("\u001b[M " + expectedCoordinate + expectedCoordinate, sequence);
            Assert.False(legacyBytes);
        }

        [Fact]
        public void Utf8_positions_up_to_95_are_identical_to_the_legacy_encoding()
        {
            var legacy = new ModeState { MouseModeX10 = true };
            foreach (int position in new[] { 1, 2, 47, 94, 95 })
            {
                Assert.Equal(
                    TerminalInputModeEncoder.EncodeMouseEvent(legacy, Press(position, 3)),
                    TerminalInputModeEncoder.EncodeMouseEvent(Utf8(), Press(position, 3)));
            }
        }

        [Fact]
        public void Utf8_wire_bytes_are_the_two_byte_utf8_forms()
        {
            string sequence = TerminalInputModeEncoder.EncodeMouseEvent(Utf8(), Press(96, 2015))!;

            Assert.Equal(new byte[] { 0x1b, 0x5b, 0x4d, 0x20, 0xc2, 0x80, 0xdf, 0xbf }, System.Text.Encoding.UTF8.GetBytes(sequence));
        }

        [Fact]
        public void Utf8_buttons_use_the_x10_event_codes_with_the_32_offset()
        {
            ModeState modes = Utf8(m => { m.MouseModeButtonEvent = true; m.MouseModeAnyEvent = true; });

            Assert.Equal("\u001b[M \u0080#", TerminalInputModeEncoder.EncodeMouseEvent(modes, Press(96, 3)));
            Assert.Equal("\u001b[M!\u0080#", TerminalInputModeEncoder.EncodeMouseEvent(modes, Press(96, 3, button: TerminalMouseButton.Middle)));
            Assert.Equal("\u001b[M\"\u0080#", TerminalInputModeEncoder.EncodeMouseEvent(modes, Press(96, 3, button: TerminalMouseButton.Right)));
            // Shift 4, Alt 8, Control 16 on top of code 0: 28 + 32 = 60.
            Assert.Equal("\u001b[M<\u0080#", TerminalInputModeEncoder.EncodeMouseEvent(modes, Press(96, 3, KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Control)));
            // Release is button 3 with the modifier bits, whichever button was released: 3 + 16 + 32 = 51.
            Assert.Equal("\u001b[M#\u0080#", TerminalInputModeEncoder.EncodeMouseEvent(modes,
                new TerminalMouseEvent(TerminalMouseEventKind.Release, TerminalMouseButton.Right, 96, 3, KeyModifiers.None)));
            Assert.Equal("\u001b[M3\u0080#", TerminalInputModeEncoder.EncodeMouseEvent(modes,
                new TerminalMouseEvent(TerminalMouseEventKind.Release, TerminalMouseButton.Left, 96, 3, KeyModifiers.Control)));
            Assert.Equal("\u001b[M`\u0080#", TerminalInputModeEncoder.EncodeMouseEvent(modes,
                new TerminalMouseEvent(TerminalMouseEventKind.Wheel, TerminalMouseButton.WheelUp, 96, 3, KeyModifiers.None)));
            Assert.Equal("\u001b[Ma\u0080#", TerminalInputModeEncoder.EncodeMouseEvent(modes,
                new TerminalMouseEvent(TerminalMouseEventKind.Wheel, TerminalMouseButton.WheelDown, 96, 3, KeyModifiers.None)));
            Assert.Equal("\u001b[M@\u0080#", TerminalInputModeEncoder.EncodeMouseEvent(modes,
                new TerminalMouseEvent(TerminalMouseEventKind.Move, TerminalMouseButton.Left, 96, 3, KeyModifiers.None)));
            Assert.Equal("\u001b[MC\u0080#", TerminalInputModeEncoder.EncodeMouseEvent(modes,
                new TerminalMouseEvent(TerminalMouseEventKind.Move, TerminalMouseButton.None, 96, 3, KeyModifiers.None)));
        }

        [Theory]
        [InlineData(1, 1, "\u001b[32;1;1M")]
        [InlineData(10, 5, "\u001b[32;10;5M")]
        [InlineData(95, 96, "\u001b[32;95;96M")]
        [InlineData(223, 224, "\u001b[32;223;224M")]
        [InlineData(2016, 4000, "\u001b[32;2016;4000M")]   // no limit: decimal numbers
        public void Urxvt_press_prints_the_x10_code_and_both_positions_as_decimal(int column, int row, string expected)
        {
            string? sequence = TerminalInputModeEncoder.EncodeMouseEvent(Urxvt(), Press(column, row), out bool legacyBytes);

            Assert.Equal(expected, sequence);
            Assert.False(legacyBytes);
        }

        [Fact]
        public void Urxvt_buttons_keep_the_32_offset_and_release_stays_button_3_with_final_M()
        {
            ModeState modes = Urxvt(m => { m.MouseModeButtonEvent = true; m.MouseModeAnyEvent = true; });

            Assert.Equal("\u001b[33;10;5M", TerminalInputModeEncoder.EncodeMouseEvent(modes, Press(10, 5, button: TerminalMouseButton.Middle)));
            Assert.Equal("\u001b[34;10;5M", TerminalInputModeEncoder.EncodeMouseEvent(modes, Press(10, 5, button: TerminalMouseButton.Right)));
            Assert.Equal("\u001b[60;10;5M", TerminalInputModeEncoder.EncodeMouseEvent(modes, Press(10, 5, KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Control)));
            Assert.Equal("\u001b[35;10;5M", TerminalInputModeEncoder.EncodeMouseEvent(modes,
                new TerminalMouseEvent(TerminalMouseEventKind.Release, TerminalMouseButton.Left, 10, 5, KeyModifiers.None)));
            Assert.Equal("\u001b[51;10;5M", TerminalInputModeEncoder.EncodeMouseEvent(modes,
                new TerminalMouseEvent(TerminalMouseEventKind.Release, TerminalMouseButton.Right, 10, 5, KeyModifiers.Control)));
            Assert.Equal("\u001b[96;10;5M", TerminalInputModeEncoder.EncodeMouseEvent(modes,
                new TerminalMouseEvent(TerminalMouseEventKind.Wheel, TerminalMouseButton.WheelUp, 10, 5, KeyModifiers.None)));
            Assert.Equal("\u001b[97;10;5M", TerminalInputModeEncoder.EncodeMouseEvent(modes,
                new TerminalMouseEvent(TerminalMouseEventKind.Wheel, TerminalMouseButton.WheelDown, 10, 5, KeyModifiers.None)));
            Assert.Equal("\u001b[64;10;5M", TerminalInputModeEncoder.EncodeMouseEvent(modes,
                new TerminalMouseEvent(TerminalMouseEventKind.Move, TerminalMouseButton.Left, 10, 5, KeyModifiers.None)));
            Assert.Equal("\u001b[67;10;5M", TerminalInputModeEncoder.EncodeMouseEvent(modes,
                new TerminalMouseEvent(TerminalMouseEventKind.Move, TerminalMouseButton.None, 10, 5, KeyModifiers.None)));
        }

        [Fact]
        public void Tracking_selectors_still_decide_which_events_are_reported_under_both_encodings()
        {
            foreach (ModeState modes in new[] { Utf8(), Urxvt() })
            {
                modes.MouseModeX10 = false;
                modes.MouseModeX10PressOnly = true;
                Assert.NotNull(TerminalInputModeEncoder.EncodeMouseEvent(modes, Press(10, 5)));
                Assert.Null(TerminalInputModeEncoder.EncodeMouseEvent(modes,
                    new TerminalMouseEvent(TerminalMouseEventKind.Release, TerminalMouseButton.Left, 10, 5, KeyModifiers.None)));
                modes.MouseModeX10PressOnly = false;
                Assert.Null(TerminalInputModeEncoder.EncodeMouseEvent(modes, Press(10, 5)));
            }
        }

        [Fact]
        public void Only_the_plain_legacy_form_is_reported_as_octets_and_sgr_wins_over_urxvt_wins_over_utf8()
        {
            var legacy = new ModeState { MouseModeX10 = true };
            TerminalInputModeEncoder.EncodeMouseEvent(legacy, Press(96, 3), out bool legacyBytes);
            Assert.True(legacyBytes);

            var all = new ModeState { MouseModeX10 = true, MouseModeSGR = true, MouseModeUrxvt = true, MouseModeUtf8 = true };
            Assert.Equal("\u001b[<0;96;3M", TerminalInputModeEncoder.EncodeMouseEvent(all, Press(96, 3), out legacyBytes));
            Assert.False(legacyBytes);

            all.MouseModeSGR = false;
            Assert.Equal("\u001b[32;96;3M", TerminalInputModeEncoder.EncodeMouseEvent(all, Press(96, 3)));

            all.MouseModeUrxvt = false;
            Assert.Equal("\u001b[M \u0080#", TerminalInputModeEncoder.EncodeMouseEvent(all, Press(96, 3), out legacyBytes));
            Assert.False(legacyBytes);
        }
    }
}
