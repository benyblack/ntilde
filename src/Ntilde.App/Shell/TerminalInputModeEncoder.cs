using System;
using Avalonia.Input;
using Ntilde.VT;

namespace Ntilde.Shell
{
    internal enum TerminalKeyEventKind
    {
        Press = 1,
        Repeat = 2,
        Release = 3
    }

    internal enum TerminalMouseButton
    {
        None = -1,
        Left = 0,
        Middle = 1,
        Right = 2,
        WheelUp = 64,
        WheelDown = 65
    }

    internal enum TerminalMouseEventKind
    {
        Press,
        Release,
        Move,
        Wheel
    }

    internal readonly record struct TerminalMouseEvent(
        TerminalMouseEventKind Kind,
        TerminalMouseButton Button,
        int Column,
        int Row,
        KeyModifiers Modifiers);

    internal static partial class TerminalInputModeEncoder
    {
        /// <summary>
        /// Encodes the cursor keys, Home/End, Insert/Delete, PageUp/PageDown and F1-F12, or
        /// returns <c>null</c> for any other key.
        ///
        /// Unmodified, these keep the classic VT encodings: CSI or SS3 for the cursor keys and
        /// Home/End depending on DECCKM (application cursor keys), SS3 for F1-F4, and
        /// <c>CSI n ~</c> for the rest.
        ///
        /// With any modifier held they take xterm's "PC-style" form, which carries the modifiers
        /// as a second parameter, <c>m = 1 + shift(1) + alt(2) + ctrl(4) + meta(8)</c>:
        /// - cursor keys and Home/End: <c>CSI 1 ; m A|B|C|D|H|F</c>
        /// - Insert, Delete, PageUp, PageDown, F5-F12: <c>CSI n ; m ~</c> (same n as unmodified)
        /// - F1-F4: <c>CSI 1 ; m P|Q|R|S</c>
        ///
        /// The modifiers are the whole point of these chords: Ctrl+Left/Right is word jump in
        /// PSReadLine, bash and zsh, Shift+arrows is selection in editors, Ctrl+Delete is
        /// kill-word. Sent as the plain key, each one silently becomes a one-character move or
        /// delete. The modified form is always the CSI one, even in application cursor mode, as in
        /// xterm: SS3 has no parameter slot, and the CSI form is what shells and editors bind these
        /// chords to. Windows ConPTY's VT input parser translates these same sequences back into
        /// modified key events for console applications, so no platform special-casing is needed.
        /// </summary>
        public static string? EncodeSpecialKey(Key key, KeyModifiers modifiers, ModeState? modes)
        {
            int modifierParameter = GetXtermModifierParameter(modifiers);
            if (modifierParameter > 1)
            {
                return EncodeModifiedSpecialKey(key, modifierParameter);
            }

            bool applicationCursorKeys = modes?.IsApplicationCursorKeys == true;

            return key switch
            {
                Key.Up => applicationCursorKeys ? "\x1bOA" : "\x1b[A",
                Key.Down => applicationCursorKeys ? "\x1bOB" : "\x1b[B",
                Key.Right => applicationCursorKeys ? "\x1bOC" : "\x1b[C",
                Key.Left => applicationCursorKeys ? "\x1bOD" : "\x1b[D",
                Key.Home => applicationCursorKeys ? "\x1bOH" : "\x1b[H",
                Key.End => applicationCursorKeys ? "\x1bOF" : "\x1b[F",
                Key.Delete => "\x1b[3~",
                Key.Insert => "\x1b[2~",
                Key.PageUp => "\x1b[5~",
                Key.PageDown => "\x1b[6~",
                Key.F1 => "\x1bOP",
                Key.F2 => "\x1bOQ",
                Key.F3 => "\x1bOR",
                Key.F4 => "\x1bOS",
                Key.F5 => "\x1b[15~",
                Key.F6 => "\x1b[17~",
                Key.F7 => "\x1b[18~",
                Key.F8 => "\x1b[19~",
                Key.F9 => "\x1b[20~",
                Key.F10 => "\x1b[21~",
                Key.F11 => "\x1b[23~",
                Key.F12 => "\x1b[24~",
                _ => null
            };
        }

        private static string? EncodeModifiedSpecialKey(Key key, int modifierParameter)
        {
            return key switch
            {
                Key.Up => FormatModifiedCsi(1, modifierParameter, 'A'),
                Key.Down => FormatModifiedCsi(1, modifierParameter, 'B'),
                Key.Right => FormatModifiedCsi(1, modifierParameter, 'C'),
                Key.Left => FormatModifiedCsi(1, modifierParameter, 'D'),
                Key.Home => FormatModifiedCsi(1, modifierParameter, 'H'),
                Key.End => FormatModifiedCsi(1, modifierParameter, 'F'),
                Key.Insert => FormatModifiedCsi(2, modifierParameter, '~'),
                Key.Delete => FormatModifiedCsi(3, modifierParameter, '~'),
                Key.PageUp => FormatModifiedCsi(5, modifierParameter, '~'),
                Key.PageDown => FormatModifiedCsi(6, modifierParameter, '~'),
                Key.F1 => FormatModifiedCsi(1, modifierParameter, 'P'),
                Key.F2 => FormatModifiedCsi(1, modifierParameter, 'Q'),
                Key.F3 => FormatModifiedCsi(1, modifierParameter, 'R'),
                Key.F4 => FormatModifiedCsi(1, modifierParameter, 'S'),
                Key.F5 => FormatModifiedCsi(15, modifierParameter, '~'),
                Key.F6 => FormatModifiedCsi(17, modifierParameter, '~'),
                Key.F7 => FormatModifiedCsi(18, modifierParameter, '~'),
                Key.F8 => FormatModifiedCsi(19, modifierParameter, '~'),
                Key.F9 => FormatModifiedCsi(20, modifierParameter, '~'),
                Key.F10 => FormatModifiedCsi(21, modifierParameter, '~'),
                Key.F11 => FormatModifiedCsi(23, modifierParameter, '~'),
                Key.F12 => FormatModifiedCsi(24, modifierParameter, '~'),
                _ => null
            };
        }

        /// <summary>
        /// xterm's modifier parameter: 1 plus shift=1, alt=2, ctrl=4, meta=8. A result of 1 means
        /// no modifier is held and the key keeps its unmodified encoding. Ctrl+Alt is deliberately
        /// not treated as AltGr here, unlike in <see cref="EncodeAltKey"/> and
        /// <see cref="EncodeKittyKey"/>: none of the keys this parameter is used for compose text,
        /// so there is no WM_CHAR to protect and Ctrl+Alt+arrow is simply m = 7.
        /// </summary>
        private static int GetXtermModifierParameter(KeyModifiers modifiers)
        {
            int parameter = 1;
            if ((modifiers & KeyModifiers.Shift) != 0) parameter += 1;
            if ((modifiers & KeyModifiers.Alt) != 0) parameter += 2;
            if ((modifiers & KeyModifiers.Control) != 0) parameter += 4;
            if ((modifiers & KeyModifiers.Meta) != 0) parameter += 8;
            return parameter;
        }

        private static string FormatModifiedCsi(int parameter, int modifierParameter, char final)
        {
            return string.Concat(
                ((char)0x1b) + "[",
                parameter.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ";",
                modifierParameter.ToString(System.Globalization.CultureInfo.InvariantCulture),
                final.ToString());
        }

        // Kitty keyboard protocol modifier bit field (shift=1, alt=2, ctrl=4, super=8).
        // The value transmitted in the escape code is 1 + this bit field.
        private const int KittyShift = 0b1;
        private const int KittyAlt = 0b10;
        private const int KittyCtrl = 0b100;
        private const int KittySuper = 0b1000;

        // Functional key codes from the kitty spec's functional key table. These three keep
        // their C0 byte for legacy compatibility when unmodified.
        private const int KittyKeyTab = 9;
        private const int KittyKeyEnter = 13;
        private const int KittyKeyEscape = 27;
        private const int KittyKeyBackspace = 127;

        /// <summary>
        /// Encodes supported non-text keys with the negotiated Kitty flags. Flag 1
        /// disambiguates ASCII control chords; flag 2 adds repeat/release subparameters
        /// and canonical functional-key frames. Press is the omitted/default event type.
        /// Ordinary text and AltGr remain on TextInput. Without flag 2, repeats keep the
        /// previous press encoding and releases are suppressed. Enter/Tab/Backspace
        /// retain their initial recovery bytes when unmodified and never report releases.
        /// </summary>
        public static string? EncodeKittyKey(Key key, KeyModifiers modifiers, ModeState? modes)
            => EncodeKittyKey(key, modifiers, modes, TerminalKeyEventKind.Press);

        /// <summary>
        /// Release is called only for a key whose non-text press the view forwarded. Its
        /// current modifiers can differ from the press (for example Ctrl released before C).
        /// Text/IME/AltGr presses remain on OnTextInput and are never tracked for release.
        /// </summary>
        public static string? EncodeKittyKey(Key key, KeyModifiers modifiers, ModeState? modes, TerminalKeyEventKind eventKind)
            => EncodeKittyKey(key, modifiers, modes, eventKind, default);

        /// <summary>Enriches already encoded text keys with captured layout metadata when flag 4 is negotiated.</summary>
        public static string? EncodeKittyKey(Key key, KeyModifiers modifiers, ModeState? modes, TerminalKeyEventKind eventKind, KittyKeyIdentity identity)
        {
            int flags = modes?.KittyKeyboard.Flags ?? 0;
            bool reportEvents = (flags & KittyKeyboardState.FlagReportEventTypes) != 0;
            bool disambiguate = (flags & KittyKeyboardState.FlagDisambiguateEscapeCodes) != 0;
            if ((flags & (KittyKeyboardState.FlagDisambiguateEscapeCodes | KittyKeyboardState.FlagReportEventTypes)) == 0
                || (eventKind == TerminalKeyEventKind.Release && !reportEvents)) return null;
            if (!reportEvents) eventKind = TerminalKeyEventKind.Press;

            int bits = GetKittyModifierBits(modifiers);
            if (reportEvents)
            {
                var (number, final) = KittyFunctionalKey(key);
                if (final != '\0') return FormatKittyEvent(number, bits, final, eventKind);
            }

            // Ctrl+Alt can compose AltGr text. A release is already paired with a proved
            // non-text press, so changed modifiers must not erase that release.
            // Non-text control keys also bypass composition filtering with event reporting.
            if (eventKind != TerminalKeyEventKind.Release
                && !(reportEvents && IsKittyControlKey(key))
                && (modifiers & (KeyModifiers.Control | KeyModifiers.Alt)) == (KeyModifiers.Control | KeyModifiers.Alt)) return null;

            return IsKittyControlKey(key)
                ? EncodeKittyControlKey(key, bits, disambiguate, eventKind)
                : EncodeKittyTextKey(key, bits, disambiguate, eventKind,
                    (flags & KittyKeyboardState.FlagReportAlternateKeys) != 0 ? identity : default);
        }

        private static bool IsKittyControlKey(Key key) => key is Key.Enter or Key.Tab or Key.Back or Key.Escape;

        private static string? EncodeKittyControlKey(Key key, int bits, bool disambiguate, TerminalKeyEventKind eventKind)
        {
            if (key is Key.Enter or Key.Tab or Key.Back)
            {
                // The report-all-keys enhancement is intentionally not implemented. Keep
                // these recovery keys usable, including suppressing their modified releases.
                if (eventKind == TerminalKeyEventKind.Release || (bits == 0 && eventKind == TerminalKeyEventKind.Press)) return null;
                int number = key switch
                {
                    Key.Enter => KittyKeyEnter,
                    Key.Tab => KittyKeyTab,
                    _ => KittyKeyBackspace
                };
                return FormatKittyEvent(number, bits, 'u', eventKind);
            }
            if (key == Key.Escape)
            {
                if (!disambiguate && eventKind == TerminalKeyEventKind.Press) return null;
                return FormatKittyEvent(KittyKeyEscape, bits, 'u', eventKind);
            }

            return null;
        }

        private static string? EncodeKittyTextKey(Key key, int bits, bool disambiguate, TerminalKeyEventKind eventKind, KittyKeyIdentity identity)
        {
            int codepoint = identity.Primary ?? GetUnshiftedCodepoint(key);
            if (codepoint < 0) return null;
            if (eventKind != TerminalKeyEventKind.Release)
            {
                if ((bits & ~KittyShift) == 0) return null;
                if (!disambiguate && eventKind == TerminalKeyEventKind.Press) return null;
            }
            return FormatKittyEvent(codepoint, bits, 'u', eventKind, identity.Format(codepoint, (bits & KittyShift) != 0));
        }

        private static (int Number, char Final) KittyFunctionalKey(Key key) => key switch
        {
            Key.Up => (1, 'A'),
            Key.Down => (1, 'B'),
            Key.Right => (1, 'C'),
            Key.Left => (1, 'D'),
            Key.Home => (1, 'H'),
            Key.End => (1, 'F'),
            Key.Insert => (2, '~'),
            Key.Delete => (3, '~'),
            Key.PageUp => (5, '~'),
            Key.PageDown => (6, '~'),
            Key.F1 => (1, 'P'),
            Key.F2 => (1, 'Q'),
            Key.F3 => (13, '~'),
            Key.F4 => (1, 'S'),
            Key.F5 => (15, '~'),
            Key.F6 => (17, '~'),
            Key.F7 => (18, '~'),
            Key.F8 => (19, '~'),
            Key.F9 => (20, '~'),
            Key.F10 => (21, '~'),
            Key.F11 => (23, '~'),
            Key.F12 => (24, '~'),
            Key.CapsLock => (57358, 'u'),
            Key.Scroll => (57359, 'u'),
            Key.NumLock => (57360, 'u'),
            Key.Snapshot => (57361, 'u'),
            Key.Pause => (57362, 'u'),
            Key.Apps => (57363, 'u'),
            >= Key.F13 and <= Key.F24 => (57376 + (key - Key.F13), 'u'),
            _ => (0, '\0')
        };

        private static int GetKittyModifierBits(KeyModifiers modifiers)
        {
            int bits = 0;
            if ((modifiers & KeyModifiers.Shift) != 0) bits |= KittyShift;
            if ((modifiers & KeyModifiers.Alt) != 0) bits |= KittyAlt;
            if ((modifiers & KeyModifiers.Control) != 0) bits |= KittyCtrl;
            if ((modifiers & KeyModifiers.Meta) != 0) bits |= KittySuper;
            return bits;
        }

        private static string FormatKittyEvent(int number, int modifierBits, char final, TerminalKeyEventKind eventKind, string? keyFields = null)
        {
            bool hasEvent = eventKind != TerminalKeyEventKind.Press;
            bool hasModifiers = modifierBits != 0 || hasEvent;
            bool omitNumber = number == 1 && final is not ('u' or '~') && !hasModifiers;
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            return string.Concat("\x1b[", omitNumber ? "" : keyFields ?? number.ToString(culture),
                hasModifiers ? ";" + (modifierBits + 1).ToString(culture) : "",
                hasEvent ? ":" + ((int)eventKind).ToString(culture) : "", final);
        }

        /// <summary>
        /// Maps an Avalonia key to the unshifted ASCII codepoint the kitty protocol requires
        /// ("the codepoint used is always the lower-case (or more technically, un-shifted)
        /// version of the key"). Returns -1 for keys outside the spec's legacy-text set, which
        /// keeps them on their legacy encodings. This is the legacy fallback when no usable layout
        /// symbol is available; OEM virtual keys do not identify every layout's unshifted glyph.
        /// </summary>
        private static int GetUnshiftedCodepoint(Key key)
        {
            if (key >= Key.A && key <= Key.Z)
            {
                return 'a' + (key - Key.A);
            }

            if (key >= Key.D0 && key <= Key.D9)
            {
                return '0' + (key - Key.D0);
            }

            return key switch
            {
                Key.Space => ' ',
                Key.OemTilde => '`',
                Key.OemMinus => '-',
                Key.OemPlus => '=',
                Key.OemOpenBrackets => '[',
                Key.OemCloseBrackets => ']',
                Key.OemPipe => '\\',
                Key.OemBackslash => '\\',
                Key.OemSemicolon => ';',
                Key.OemQuotes => '\'',
                Key.OemComma => ',',
                Key.OemPeriod => '.',
                Key.OemQuestion => '/',
                _ => -1
            };
        }

        /// <summary>
        /// Encodes an Alt/Meta + printable key as an ESC-prefixed sequence, matching the
        /// standard xterm "metaSendsEscape" behavior (e.g. Alt+V -> ESC v). This restores
        /// readline/emacs Alt keybindings and is the trigger Claude Code listens for to
        /// paste an image from the clipboard. Returns null when the combination should not
        /// be meta-encoded (no Alt, Ctrl+Alt/AltGr, or a non-printable key).
        /// </summary>
        public static string? EncodeAltKey(Key key, KeyModifiers modifiers)
        {
            if ((modifiers & KeyModifiers.Alt) == 0)
            {
                return null;
            }

            // Ctrl+Alt is AltGr on many keyboard layouts and produces real text input;
            // meta-encoding it here would double-handle the key.
            if ((modifiers & KeyModifiers.Control) != 0)
            {
                return null;
            }

            bool shift = (modifiers & KeyModifiers.Shift) != 0;

            if (key >= Key.A && key <= Key.Z)
            {
                char c = (char)('a' + (key - Key.A));
                if (shift)
                {
                    c = char.ToUpperInvariant(c);
                }

                return "\x1b" + c;
            }

            if (!shift && key >= Key.D0 && key <= Key.D9)
            {
                char c = (char)('0' + (key - Key.D0));
                return "\x1b" + c;
            }

            // Common non-alphanumeric meta keys (layout-independent), used by readline/emacs.
            switch (key)
            {
                case Key.Back:
                    return "\x1b\x7f";  // M-DEL: backward-kill-word
                case Key.Enter:
                    return "\x1b\r";
                case Key.OemPeriod when !shift:
                    return "\x1b.";     // M-.: yank-last-arg
            }

            return null;
        }

        /// <summary>
        /// Legacy Ctrl+key encoding: the C0 control character a Ctrl chord sends when no
        /// keyboard protocol is active, or <c>null</c> when the chord has none.
        ///
        /// Ctrl+A..Z map to 0x01..0x1A (Shift excluded: Ctrl+Shift+letter is an app-shortcut
        /// space). The punctuation and digit rows follow Xlib's XLookupString control mapping,
        /// which xterm, VTE and Windows Terminal all send:
        /// - NUL  0x00: Ctrl+@ (Ctrl+Shift+2), Ctrl+2, Ctrl+Space
        /// - ESC  0x1B: Ctrl+[, Ctrl+3
        /// - FS   0x1C: Ctrl+\, Ctrl+4
        /// - GS   0x1D: Ctrl+], Ctrl+5
        /// - RS   0x1E: Ctrl+^ (Ctrl+Shift+6), Ctrl+6
        /// - US   0x1F: Ctrl+_ (Ctrl+Shift+-), Ctrl+7, Ctrl+/
        /// - DEL  0x7F: Ctrl+8
        ///
        /// Without this, those chords were swallowed: Windows does put the control character in
        /// WM_CHAR, but Avalonia drops control characters from TextInput, so the only way they
        /// reach the PTY is by encoding them on KeyDown. Programs depend on them - Ctrl+\ is
        /// SIGQUIT and the <c>ntilde mux attach</c> detach prefix, Ctrl+] is telnet's escape,
        /// Ctrl+_ is readline/emacs undo, Ctrl+Space / Ctrl+@ is set-mark.
        ///
        /// Ctrl+Alt is declined: on Windows that is how AltGr arrives, and AltGr composes real
        /// text on most non-US layouts (AltGr+8/9 are '[' and ']' on German) that must reach
        /// TextInput - encoding it here would mark KeyDown handled and the character would be lost.
        /// Meta chords are declined too.
        ///
        /// Layout limitation: <see cref="Key"/> is a US-layout virtual key, so the punctuation
        /// rows match the key in that US position (the same assumption
        /// <see cref="GetUnshiftedCodepoint"/> makes for the kitty protocol). On a layout where
        /// '\' or ']' only exist behind AltGr, Ctrl+\ is not reachable this way - the same is
        /// true in Windows Terminal. The digit forms (Ctrl+4, Ctrl+5, ...) work on every layout.
        /// </summary>
        public static string? EncodeLegacyControlKey(Key key, KeyModifiers modifiers)
        {
            if ((modifiers & KeyModifiers.Control) == 0 ||
                (modifiers & (KeyModifiers.Alt | KeyModifiers.Meta)) != 0)
            {
                return null;
            }

            bool shift = (modifiers & KeyModifiers.Shift) != 0;

            int code = shift
                ? key switch
                {
                    Key.D2 => 0x00,        // Ctrl+@
                    Key.D6 => 0x1E,        // Ctrl+^
                    Key.OemMinus => 0x1F,  // Ctrl+_
                    _ => -1
                }
                : key switch
                {
                    >= Key.A and <= Key.Z => key - Key.A + 1,
                    Key.Space or Key.D2 => 0x00,
                    Key.OemOpenBrackets or Key.D3 => 0x1B,
                    Key.OemPipe or Key.OemBackslash or Key.D4 => 0x1C,
                    Key.OemCloseBrackets or Key.D5 => 0x1D,
                    Key.D6 => 0x1E,
                    Key.D7 or Key.OemQuestion => 0x1F,
                    Key.D8 => 0x7F,
                    _ => -1
                };

            return code < 0 ? null : ((char)code).ToString();
        }

        public static string? EncodeFocusChanged(ModeState? modes, bool isFocused)
        {
            if (modes?.IsFocusEventReporting != true)
            {
                return null;
            }

            return isFocused ? "\x1b[I" : "\x1b[O";
        }

        /// <summary>
        /// Highest coordinate the legacy X10 mouse encoding can carry: the byte sent is
        /// value+32 and the largest byte is 255, so 255-32 = 223.
        /// </summary>
        private const int MaxLegacyCoordinate = 223;

        public static string? EncodeMouseEvent(ModeState modes, TerminalMouseEvent mouseEvent)
        {
            if (!ShouldReportMouseEvent(modes, mouseEvent))
            {
                return null;
            }

            int buttonCode = GetButtonCode(mouseEvent);
            if (buttonCode < 0)
            {
                return null;
            }

            int x = Math.Max(1, mouseEvent.Column);
            int y = Math.Max(1, mouseEvent.Row);

            if (modes.MouseModeSGR)
            {
                char finalChar = mouseEvent.Kind == TerminalMouseEventKind.Release ? 'm' : 'M';
                return $"\x1b[<{buttonCode};{x};{y}{finalChar}";
            }

            if (mouseEvent.Kind == TerminalMouseEventKind.Release)
            {
                buttonCode = 3 + GetModifierBits(mouseEvent.Modifiers);
            }

            // Legacy X10 coordinate encoding (`CSI M Cb Cx Cy`) transmits each coordinate as a
            // single byte holding value+32, so the largest representable coordinate is 223
            // (32+223 = 255, the highest byte value). Coordinates past that are CLAMPED to 223
            // rather than promoted to the SGR (`?1006`) form: an application that never enabled
            // `?1006` has no parser for `ESC [ <` and would render it as text or desync, so
            // xterm clamps (or drops) out-of-range coordinates instead of silently switching
            // protocols. Applications that need coordinates beyond 223 must enable `?1006`.
            char buttonChar = (char)(32 + buttonCode);
            char xChar = (char)(32 + Math.Clamp(x, 1, MaxLegacyCoordinate));
            char yChar = (char)(32 + Math.Clamp(y, 1, MaxLegacyCoordinate));
            return $"\x1b[M{buttonChar}{xChar}{yChar}";
        }

        private static bool ShouldReportMouseEvent(ModeState modes, TerminalMouseEvent mouseEvent)
        {
            if (!(modes.MouseModeX10 || modes.MouseModeButtonEvent || modes.MouseModeAnyEvent))
            {
                return false;
            }

            return mouseEvent.Kind switch
            {
                TerminalMouseEventKind.Press => IsButtonPress(mouseEvent.Button),
                TerminalMouseEventKind.Release => IsButtonPress(mouseEvent.Button),
                TerminalMouseEventKind.Wheel => mouseEvent.Button is TerminalMouseButton.WheelUp or TerminalMouseButton.WheelDown,
                TerminalMouseEventKind.Move => modes.MouseModeAnyEvent || (modes.MouseModeButtonEvent && IsButtonPress(mouseEvent.Button)),
                _ => false
            };
        }

        private static int GetButtonCode(TerminalMouseEvent mouseEvent)
        {
            int modifiers = GetModifierBits(mouseEvent.Modifiers);
            int baseCode = mouseEvent.Kind switch
            {
                TerminalMouseEventKind.Move => GetMotionBaseCode(mouseEvent.Button),
                TerminalMouseEventKind.Wheel => mouseEvent.Button switch
                {
                    TerminalMouseButton.WheelUp => 64,
                    TerminalMouseButton.WheelDown => 65,
                    _ => -1
                },
                TerminalMouseEventKind.Press or TerminalMouseEventKind.Release => GetButtonBaseCode(mouseEvent.Button),
                _ => -1
            };

            return baseCode >= 0 ? baseCode + modifiers : -1;
        }

        private static int GetMotionBaseCode(TerminalMouseButton button)
        {
            return button switch
            {
                TerminalMouseButton.Left => 32,
                TerminalMouseButton.Middle => 33,
                TerminalMouseButton.Right => 34,
                TerminalMouseButton.None => 35,
                _ => -1
            };
        }

        private static int GetButtonBaseCode(TerminalMouseButton button)
        {
            return button switch
            {
                TerminalMouseButton.Left => 0,
                TerminalMouseButton.Middle => 1,
                TerminalMouseButton.Right => 2,
                TerminalMouseButton.None => 3,
                _ => -1
            };
        }

        private static bool IsButtonPress(TerminalMouseButton button)
        {
            return button is TerminalMouseButton.Left or TerminalMouseButton.Middle or TerminalMouseButton.Right;
        }

        private static int GetModifierBits(KeyModifiers modifiers)
        {
            int bits = 0;
            if ((modifiers & KeyModifiers.Shift) != 0) bits += 4;
            if ((modifiers & KeyModifiers.Alt) != 0) bits += 8;
            if ((modifiers & KeyModifiers.Control) != 0) bits += 16;
            return bits;
        }
    }
}
