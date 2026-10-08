using Ntilde.Shell;
using Avalonia.Input;
using Ntilde.Platform;
using Ntilde.VT;
using Xunit;

namespace Ntilde.Tests.Input
{
    public class TerminalInputModeEncoderTests
    {
        [Fact]
        public void EncodeMouseEvent_ButtonEventTrackingWithSgr_EncodesDragMotion()
        {
            var modes = new ModeState
            {
                MouseModeButtonEvent = true,
                MouseModeSGR = true
            };

            string? sequence = TerminalInputModeEncoder.EncodeMouseEvent(
                modes,
                new TerminalMouseEvent(TerminalMouseEventKind.Move, TerminalMouseButton.Left, 12, 7, KeyModifiers.None));

            Assert.Equal("\x1b[<32;12;7M", sequence);
        }

        [Fact]
        public void EncodeMouseEvent_AnyEventWithSgr_EncodesHoverMotion()
        {
            var modes = new ModeState
            {
                MouseModeAnyEvent = true,
                MouseModeSGR = true
            };

            string? sequence = TerminalInputModeEncoder.EncodeMouseEvent(
                modes,
                new TerminalMouseEvent(TerminalMouseEventKind.Move, TerminalMouseButton.None, 9, 4, KeyModifiers.None));

            Assert.Equal("\x1b[<35;9;4M", sequence);
        }

        [Fact]
        public void EncodeMouseEvent_SgrRelease_PreservesReleasedButtonAndModifiers()
        {
            var modes = new ModeState
            {
                MouseModeX10 = true,
                MouseModeSGR = true
            };

            string? sequence = TerminalInputModeEncoder.EncodeMouseEvent(
                modes,
                new TerminalMouseEvent(
                    TerminalMouseEventKind.Release,
                    TerminalMouseButton.Right,
                    3,
                    4,
                    KeyModifiers.Control | KeyModifiers.Shift));

            Assert.Equal("\x1b[<22;3;4m", sequence);
        }

        [Fact]
        public void EncodeMouseEvent_ButtonEventTracking_IgnoresHoverWithoutPressedButton()
        {
            var modes = new ModeState
            {
                MouseModeButtonEvent = true,
                MouseModeSGR = true
            };

            string? sequence = TerminalInputModeEncoder.EncodeMouseEvent(
                modes,
                new TerminalMouseEvent(TerminalMouseEventKind.Move, TerminalMouseButton.None, 9, 4, KeyModifiers.None));

            Assert.Null(sequence);
        }

        [Fact]
        public void EncodeMouseEvent_LegacyAtMaxCoordinate_EncodesInOneByteEach()
        {
            // 223 IS representable in the legacy X10 encoding: the byte sent is value+32 and
            // 32+223 = 255, the largest byte. Left press -> button code 0 -> char 32 (space).
            var modes = new ModeState { MouseModeX10 = true };

            string? sequence = TerminalInputModeEncoder.EncodeMouseEvent(
                modes,
                new TerminalMouseEvent(TerminalMouseEventKind.Press, TerminalMouseButton.Left, 223, 223, KeyModifiers.None));

            // Built from char codes rather than a literal so this file stays pure ASCII.
            string expected = "\x1b[M\x20" + (char)255 + (char)255;
            Assert.Equal(expected, sequence);
        }

        [Fact]
        public void EncodeMouseEvent_LegacyBeyondMaxCoordinate_ClampsAndNeverEmitsSgr()
        {
            // Regression: coordinates past 223 used to fall back to the SGR form even when the
            // application never enabled ?1006 - unparseable for a legacy-mode app, which would
            // render it as text or desync. xterm clamps out-of-range coordinates instead of
            // switching protocols.
            var modes = new ModeState { MouseModeX10 = true };

            string? sequence = TerminalInputModeEncoder.EncodeMouseEvent(
                modes,
                new TerminalMouseEvent(TerminalMouseEventKind.Press, TerminalMouseButton.Left, 400, 500, KeyModifiers.None));

            Assert.NotNull(sequence);
            Assert.DoesNotContain("\x1b[<", sequence);
            string expected = "\x1b[M\x20" + (char)255 + (char)255;
            Assert.Equal(expected, sequence);
        }

        [Fact]
        public void EncodeMouseEvent_SgrBeyondLegacyMaxCoordinate_IsNotClamped()
        {
            // The 223 ceiling is a property of the legacy one-byte-per-coordinate encoding only;
            // ?1006 sends decimal parameters and must carry the real coordinates.
            var modes = new ModeState { MouseModeX10 = true, MouseModeSGR = true };

            string? sequence = TerminalInputModeEncoder.EncodeMouseEvent(
                modes,
                new TerminalMouseEvent(TerminalMouseEventKind.Press, TerminalMouseButton.Left, 400, 500, KeyModifiers.None));

            Assert.Equal("\x1b[<0;400;500M", sequence);
        }

        [Fact]
        public void EncodeAltKey_AltLetter_EmitsEscapePrefixedLowercase()
        {
            // xterm "metaSendsEscape": Alt+<letter> sends ESC followed by the character.
            // Claude Code relies on Alt+V (ESC v) as its paste-image trigger.
            // Build ESC via concatenation: "\x1b" is a complete escape (the closing quote
            // ends it), avoiding \x greediness that would fold a trailing hex char into it.
            Assert.Equal("\x1b" + "v", TerminalInputModeEncoder.EncodeAltKey(Key.V, KeyModifiers.Alt));
            Assert.Equal("\x1b" + "b", TerminalInputModeEncoder.EncodeAltKey(Key.B, KeyModifiers.Alt));
        }

        [Fact]
        public void EncodeAltKey_AltShiftLetter_EmitsEscapePrefixedUppercase()
        {
            Assert.Equal("\x1b" + "V", TerminalInputModeEncoder.EncodeAltKey(Key.V, KeyModifiers.Alt | KeyModifiers.Shift));
        }

        [Fact]
        public void EncodeAltKey_AltDigit_EmitsEscapePrefixedDigit()
        {
            Assert.Equal("\x1b" + "5", TerminalInputModeEncoder.EncodeAltKey(Key.D5, KeyModifiers.Alt));
        }

        [Fact]
        public void EncodeAltKey_AltBackspace_EmitsEscapeThenDelete()
        {
            // readline backward-kill-word (M-DEL): ESC followed by DEL (0x7f).
            Assert.Equal("\x1b\x7f", TerminalInputModeEncoder.EncodeAltKey(Key.Back, KeyModifiers.Alt));
        }

        [Fact]
        public void EncodeAltKey_AltEnter_EmitsEscapeThenCarriageReturn()
        {
            Assert.Equal("\x1b\r", TerminalInputModeEncoder.EncodeAltKey(Key.Enter, KeyModifiers.Alt));
        }

        [Fact]
        public void EncodeAltKey_AltPeriod_EmitsEscapeThenPeriod()
        {
            // readline yank-last-arg (M-.)
            Assert.Equal("\x1b.", TerminalInputModeEncoder.EncodeAltKey(Key.OemPeriod, KeyModifiers.Alt));
        }

        [Fact]
        public void EncodeAltKey_AltShiftPeriod_ReturnsNull()
        {
            // Shifted OemPeriod is '>' on most layouts, not '.'; don't mis-encode it.
            Assert.Null(TerminalInputModeEncoder.EncodeAltKey(Key.OemPeriod, KeyModifiers.Alt | KeyModifiers.Shift));
        }

        [Fact]
        public void EncodeAltKey_WithoutAlt_ReturnsNull()
        {
            Assert.Null(TerminalInputModeEncoder.EncodeAltKey(Key.V, KeyModifiers.None));
        }

        [Fact]
        public void EncodeAltKey_CtrlAltCombo_ReturnsNull()
        {
            // Ctrl+Alt is AltGr on many layouts and produces real text input;
            // encoding it here would double-handle the key.
            Assert.Null(TerminalInputModeEncoder.EncodeAltKey(Key.V, KeyModifiers.Alt | KeyModifiers.Control));
        }

        [Fact]
        public void EncodeAltKey_NonPrintableKey_ReturnsNull()
        {
            Assert.Null(TerminalInputModeEncoder.EncodeAltKey(Key.Up, KeyModifiers.Alt));
            Assert.Null(TerminalInputModeEncoder.EncodeAltKey(Key.F5, KeyModifiers.Alt));
        }

        // --- EncodeSpecialKey --------------------------------------------------------------
        //
        // Cursor keys, Home/End, Insert/Delete, PageUp/PageDown and F1-F12 have to carry the
        // modifiers held with them, or Ctrl+Left (word jump in PSReadLine, bash and zsh), Shift+arrows
        // (editor selection) and Ctrl+Delete all reach the application as the plain key. The encoding
        // is xterm's "PC-style" one: CSI <param> ; <m> <final>, where
        // m = 1 + shift(1) + alt(2) + ctrl(4) + meta(8).

        // The modified form of each key, written out per key rather than derived from the encoder's
        // own table, so a mistake there cannot be baked into the expectation too.
        private static readonly (Key Key, string Parameter, char Final)[] ModifiedSpecialKeyForms =
        {
            (Key.Up, "1", 'A'),
            (Key.Down, "1", 'B'),
            (Key.Right, "1", 'C'),
            (Key.Left, "1", 'D'),
            (Key.Home, "1", 'H'),
            (Key.End, "1", 'F'),
            (Key.Insert, "2", '~'),
            (Key.Delete, "3", '~'),
            (Key.PageUp, "5", '~'),
            (Key.PageDown, "6", '~'),
            (Key.F1, "1", 'P'),
            (Key.F2, "1", 'Q'),
            (Key.F3, "1", 'R'),
            (Key.F4, "1", 'S'),
            (Key.F5, "15", '~'),
            (Key.F6, "17", '~'),
            (Key.F7, "18", '~'),
            (Key.F8, "19", '~'),
            (Key.F9, "20", '~'),
            (Key.F10, "21", '~'),
            (Key.F11, "23", '~'),
            (Key.F12, "24", '~'),
        };

        // Every single modifier, every Ctrl/Alt/Shift combination, and Meta alone and combined, with
        // the parameter as a literal.
        private static readonly (KeyModifiers Modifiers, string Parameter)[] ModifierParameters =
        {
            (KeyModifiers.Shift, "2"),
            (KeyModifiers.Alt, "3"),
            (KeyModifiers.Alt | KeyModifiers.Shift, "4"),
            (KeyModifiers.Control, "5"),
            (KeyModifiers.Control | KeyModifiers.Shift, "6"),
            (KeyModifiers.Control | KeyModifiers.Alt, "7"),
            (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift, "8"),
            (KeyModifiers.Meta, "9"),
            (KeyModifiers.Meta | KeyModifiers.Shift, "10"),
            (KeyModifiers.Meta | KeyModifiers.Control, "13"),
            (KeyModifiers.Meta | KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift, "16"),
        };

        public static TheoryData<Key, KeyModifiers, bool, string> ModifiedSpecialKeyCases()
        {
            var data = new TheoryData<Key, KeyModifiers, bool, string>();
            foreach (var form in ModifiedSpecialKeyForms)
            {
                foreach (var modifier in ModifierParameters)
                {
                    // Application cursor mode must not change the modified form: xterm always sends
                    // the CSI form once a modifier is held, never SS3 with a parameter.
                    foreach (bool applicationCursorKeys in new[] { false, true })
                    {
                        data.Add(
                            form.Key,
                            modifier.Modifiers,
                            applicationCursorKeys,
                            "\x1b[" + form.Parameter + ";" + modifier.Parameter + form.Final);
                    }
                }
            }

            return data;
        }

        [Theory]
        [MemberData(nameof(ModifiedSpecialKeyCases))]
        public void EncodeSpecialKey_WithModifiers_EmitsXtermModifierParameter(
            Key key, KeyModifiers modifiers, bool applicationCursorKeys, string expected)
        {
            var modes = new ModeState { IsApplicationCursorKeys = applicationCursorKeys };

            Assert.Equal(expected, TerminalInputModeEncoder.EncodeSpecialKey(key, modifiers, modes));
        }

        [Theory]
        [InlineData(Key.Left, KeyModifiers.Control, "\x1b[1;5D")]    // PSReadLine/readline backward-word
        [InlineData(Key.Right, KeyModifiers.Control, "\x1b[1;5C")]   // forward-word
        [InlineData(Key.Up, KeyModifiers.Shift, "\x1b[1;2A")]        // extend selection up
        [InlineData(Key.End, KeyModifiers.Shift, "\x1b[1;2F")]       // select to end of line
        [InlineData(Key.Delete, KeyModifiers.Control, "\x1b[3;5~")]  // kill-word forward
        [InlineData(Key.PageDown, KeyModifiers.Shift, "\x1b[6;2~")]
        [InlineData(Key.F1, KeyModifiers.Shift, "\x1b[1;2P")]
        [InlineData(Key.F4, KeyModifiers.Control, "\x1b[1;5S")]
        [InlineData(Key.F12, KeyModifiers.Control | KeyModifiers.Shift, "\x1b[24;6~")]
        public void EncodeSpecialKey_WithModifiers_CommonChords(Key key, KeyModifiers modifiers, string expected)
        {
            Assert.Equal(expected, TerminalInputModeEncoder.EncodeSpecialKey(key, modifiers, new ModeState()));
        }

        [Fact]
        public void EncodeSpecialKey_WithModifiersAndNoModeState_StillEmitsModifierParameter()
        {
            Assert.Equal("\x1b[1;5D", TerminalInputModeEncoder.EncodeSpecialKey(Key.Left, KeyModifiers.Control, null));
        }

        // The no-modifier output is the pre-existing contract and must stay byte-identical.
        public static TheoryData<Key, bool, string> UnmodifiedSpecialKeyCases() => new()
        {
            { Key.Up, false, "\x1b[A" },
            { Key.Up, true, "\x1bOA" },
            { Key.Down, false, "\x1b[B" },
            { Key.Down, true, "\x1bOB" },
            { Key.Right, false, "\x1b[C" },
            { Key.Right, true, "\x1bOC" },
            { Key.Left, false, "\x1b[D" },
            { Key.Left, true, "\x1bOD" },
            { Key.Home, false, "\x1b[H" },
            { Key.Home, true, "\x1bOH" },
            { Key.End, false, "\x1b[F" },
            { Key.End, true, "\x1bOF" },
            { Key.Insert, false, "\x1b[2~" },
            { Key.Insert, true, "\x1b[2~" },
            { Key.Delete, false, "\x1b[3~" },
            { Key.Delete, true, "\x1b[3~" },
            { Key.PageUp, false, "\x1b[5~" },
            { Key.PageUp, true, "\x1b[5~" },
            { Key.PageDown, false, "\x1b[6~" },
            { Key.PageDown, true, "\x1b[6~" },
            { Key.F1, false, "\x1bOP" },
            { Key.F1, true, "\x1bOP" },
            { Key.F2, false, "\x1bOQ" },
            { Key.F3, false, "\x1bOR" },
            { Key.F4, false, "\x1bOS" },
            { Key.F5, false, "\x1b[15~" },
            { Key.F5, true, "\x1b[15~" },
            { Key.F6, false, "\x1b[17~" },
            { Key.F7, false, "\x1b[18~" },
            { Key.F8, false, "\x1b[19~" },
            { Key.F9, false, "\x1b[20~" },
            { Key.F10, false, "\x1b[21~" },
            { Key.F11, false, "\x1b[23~" },
            { Key.F12, false, "\x1b[24~" },
        };

        [Theory]
        [MemberData(nameof(UnmodifiedSpecialKeyCases))]
        public void EncodeSpecialKey_WithoutModifiers_IsUnchanged(Key key, bool applicationCursorKeys, string expected)
        {
            var modes = new ModeState { IsApplicationCursorKeys = applicationCursorKeys };

            Assert.Equal(expected, TerminalInputModeEncoder.EncodeSpecialKey(key, KeyModifiers.None, modes));
        }

        [Fact]
        public void EncodeSpecialKey_WithoutModifiersAndNoModeState_UsesNormalCursorMode()
        {
            Assert.Equal("\x1b[A", TerminalInputModeEncoder.EncodeSpecialKey(Key.Up, KeyModifiers.None, null));
        }

        [Theory]
        [InlineData(Key.A, KeyModifiers.Control)]
        [InlineData(Key.Space, KeyModifiers.Shift)]
        [InlineData(Key.Enter, KeyModifiers.Shift)]
        [InlineData(Key.Tab, KeyModifiers.Control)]
        [InlineData(Key.Escape, KeyModifiers.Alt)]
        [InlineData(Key.Back, KeyModifiers.Control)]
        [InlineData(Key.NumPad5, KeyModifiers.Control)]
        [InlineData(Key.LeftCtrl, KeyModifiers.Control)]
        public void EncodeSpecialKey_KeysOutsideItsTable_ReturnNullWhateverTheModifiers(Key key, KeyModifiers modifiers)
        {
            Assert.Null(TerminalInputModeEncoder.EncodeSpecialKey(key, modifiers, new ModeState()));
            Assert.Null(TerminalInputModeEncoder.EncodeSpecialKey(key, KeyModifiers.None, new ModeState()));
        }

        [Fact]
        public void EncodeFocusChanged_RequiresFocusReportingMode()
        {
            Assert.Null(TerminalInputModeEncoder.EncodeFocusChanged(new ModeState(), isFocused: true));
            Assert.Equal(
                "\x1b[I",
                TerminalInputModeEncoder.EncodeFocusChanged(
                    new ModeState { IsFocusEventReporting = true },
                    isFocused: true));
            Assert.Equal(
                "\x1b[O",
                TerminalInputModeEncoder.EncodeFocusChanged(
                    new ModeState { IsFocusEventReporting = true },
                    isFocused: false));
        }
    }
}
