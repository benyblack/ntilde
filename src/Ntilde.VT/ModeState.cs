namespace Ntilde.VT
{
    /// <summary>
    /// Encapsulates terminal mode flags (mouse reporting, auto-wrap, cursor keys, etc.)
    /// </summary>
    public class ModeState
    {
        // Mouse reporting modes (for TUI apps like vim, htop)
        public bool MouseModeX10PressOnly { get; set; } // ?9 - X10 compatibility tracking
        public bool MouseModeX10 { get; set; }          // ?1000 - Normal tracking (legacy property name)
        public bool MouseModeButtonEvent { get; set; }  // ?1002 - Button event tracking
        public bool MouseModeAnyEvent { get; set; }     // ?1003 - Any event tracking
        public bool MouseModeSGR { get; set; }          // ?1006 - SGR extended mode
        public bool MouseModeUtf8 { get; set; }         // ?1005 - UTF-8 coordinate encoding
        public bool MouseModeUrxvt { get; set; }        // ?1015 - urxvt decimal coordinate encoding
        public bool MouseModePixel { get; set; }        // ?1016 - SGR format with pixel positions

        /// <summary>DECSDM (?80): xterm polarity, set = page-origin graphics without SIXEL-driven scrolling.</summary>
        public bool IsSixelDisplayMode { get; set; }

        /// <summary>DECKPAM / DECKPNM and DECNKM (?66): application auxiliary keypad.</summary>
        public bool IsApplicationKeypad { get; set; }

        /// <summary>DECBKM (?67): set sends BS for legacy Backspace, reset sends DEL.</summary>
        public bool IsBackarrowKeyMode { get; set; }

        // Cursor and display modes
        public bool IsApplicationCursorKeys { get; set; } // ?1 - DECCKM (Application Cursor Keys)
        public bool IsAutoWrapMode { get; set; } = true;  // ?7 - DECAWM (Auto Wrap Mode)
        public bool IsOriginMode { get; set; }            // ?6 - DECOM (Origin Mode)

        /// <summary>
        /// ?69 - DECLRMM (left/right margin mode). While set, <c>CSI Pl ; Pr s</c> is DECSLRM instead of SCOSC and the buffer's
        /// left/right margins bound the operations that honor them; resetting it restores full-width margins. Off by default.
        /// </summary>
        public bool IsLeftRightMarginMode { get; set; }

        /// <summary>
        /// ?45 - reverse-wraparound (xterm). With auto-wrap (?7) also set, BS at the left edge returns to the
        /// last column of the previous row when that row ended by a soft wrap. Off by default.
        /// </summary>
        public bool IsReverseWrapMode { get; set; }

        /// <summary>
        /// DECSACE (CSI Ps * x): <c>true</c> after Ps 2 (DECCARA/DECRARA affect the rectangle), <c>false</c> for the stream
        /// (Ps 0 or 1, the power-up default). Stream state: RIS restores the stream, DECSTR keeps the selection, and state transfer
        /// carries it. It is independent of the margins, origin mode and the current pen.
        /// </summary>
        public bool IsRectangularAttributeExtent { get; set; }

        /// <summary>
        /// DECSCNM (?5): screen-wide reverse video. While set, the render snapshot presents the
        /// theme's default foreground and background swapped (xterm semantics). Set it through
        /// <see cref="TerminalBuffer.SetReverseVideo"/>, which also invalidates every rendered row.
        /// </summary>
        public bool IsReverseVideo { get; internal set; }
        public bool IsFocusEventReporting { get; set; }   // ?1004 - FocusIn/FocusOut reporting
        public bool IsClipboardPasteMode { get; set; }    // ?5522 - MIME paste notifications
        public bool IsBracketedPasteMode { get; set; }    // ?2004 - Bracketed Paste Mode
        public bool IsCursorVisible { get; set; } = true; // ?25 - DECTCEM (Text Cursor Enable Mode)
        public bool IsCursorBlinkEnabled { get; set; } = true;
        public CursorStyle CursorStyle { get; set; } = CursorStyle.Underline;
        /// <summary>Use the presenting host's configured cursor appearance instead of a DECSCUSR override.</summary>
        public bool CursorStyleIsDefault { get; set; } = true;
        public bool IsInsertMode { get; set; }            //  4 - IRM (Insert Replacement Mode)
        public bool IsLineFeedNewLineMode { get; set; }   // 20 - LNM (Line Feed New Line Mode)
        private enum EchoSetting : byte { Default, Child, Local }
        private volatile EchoSetting _echoSetting;

        /// <summary>Echo hint used by drag and drop. Before SRM is negotiated the hint permits drops.</summary>
        public bool IsEchoEnabled
        {
            get => _echoSetting != EchoSetting.Child;
            set => SetLocalEcho(value);
        }

        /// <summary>ANSI mode 12 enables local keyboard echo only after an explicit reset (CSI 12 l).</summary>
        public bool IsLocalEchoEnabled => _echoSetting == EchoSetting.Local;

        internal void SetLocalEcho(bool? enabled) => _echoSetting = enabled switch
        {
            true => EchoSetting.Local,
            false => EchoSetting.Child,
            null => EchoSetting.Default,
        };

        /// <summary>
        /// ?1007 alternate scroll as the application last set it: <c>true</c> after DECSET,
        /// <c>false</c> after DECRST, <c>null</c> while it has not touched the mode since RIS (or
        /// ever). Null defers to <see cref="AlternateScrollDefault"/>. This half is stream state:
        /// RIS clears it and state transfer carries it.
        /// </summary>
        public bool? AlternateScrollOverride { get; set; }

        /// <summary>
        /// The presenting host's initial ?1007 value (the App's <c>TerminalSettings.AlternateScroll</c>),
        /// on by default as in iTerm2, kitty, WezTerm, Ghostty, Windows Terminal and VTE (xterm's
        /// own <c>alternateScroll</c> resource defaults off). Host configuration, not stream state: RIS keeps it and
        /// state transfer does not carry it, so each pane that mirrors a session applies its own.
        /// </summary>
        public bool AlternateScrollDefault { get; set; } = true;

        /// <summary>
        /// Effective ?1007: on the alternate screen with mouse reporting off, the wheel sends
        /// cursor-up/down keys instead of scrolling the (absent) scrollback. The VT layer only
        /// tracks and reports it; the App's wheel handler acts on it.
        /// </summary>
        public bool IsAlternateScroll => AlternateScrollOverride ?? AlternateScrollDefault;

        /// <summary>
        /// Kitty keyboard protocol progressive-enhancement flags (CSI ? u / CSI &gt; u /
        /// CSI &lt; u / CSI = u). Per-screen-buffer flag stacks; the App input layer reads
        /// <see cref="KittyKeyboardState.DisambiguateEscapeCodes"/> when encoding key events.
        /// </summary>
        public KittyKeyboardState KittyKeyboard { get; set; } = new KittyKeyboardState();

        /// <summary>
        /// xterm modifyOtherKeys level (CSI &gt; 4 ; Pv m), one per screen buffer. The App input
        /// layer reads <see cref="ModifyOtherKeysState.Level"/> when no kitty flags are active.
        /// </summary>
        public ModifyOtherKeysState ModifyOtherKeys { get; set; } = new ModifyOtherKeysState();

        /// <summary>
        /// Clears the input-reporting modes a full-screen application turns on for itself
        /// (mouse tracking, focus reporting, the kitty keyboard protocol and xterm's
        /// modifyOtherKeys). Called when the
        /// shell signals a fresh prompt so a TUI that exited uncleanly — Ctrl+C, crash, or
        /// output dropped during PTY teardown — can't leave mouse reporting on and flood the
        /// prompt with ESC[&lt;..M reports on every pointer move. Shell-owned modes (bracketed
        /// paste, application cursor keys, auto-wrap) are intentionally left untouched.
        ///
        /// Non-blocking review note (#277): kitty itself does not reset its keyboard stacks on
        /// this kind of transient signal, and doing so is a policy call, not a spec requirement.
        /// But this method already makes the opposite policy call for mouse mode with exactly
        /// the same justification ("a TUI that exited uncleanly can't leave X reporting on") -
        /// leaving disambiguate on would have the shell prompt's Ctrl+C send
        /// <c>CSI 99;5u</c> instead of raising SIGINT, and readline would print garbage instead
        /// of interrupting. Consistency with the mouse-mode policy wins here.
        /// </summary>
        public void ResetTransientInputReporting()
        {
            MouseModeX10PressOnly = false;
            MouseModeX10 = false;
            MouseModeButtonEvent = false;
            MouseModeAnyEvent = false;
            MouseModeSGR = false;
            MouseModeUtf8 = false;
            MouseModeUrxvt = false;
            MouseModePixel = false;
            IsFocusEventReporting = false;
            IsClipboardPasteMode = false;
            KittyKeyboard.Reset();
            // Same reasoning as the kitty stacks: level 2 would turn the prompt's Ctrl+C into
            // CSI 27;5;99~ after an editor that enabled it exited without resetting it.
            ModifyOtherKeys.Reset();
        }

        // Tracking is one protocol, independently of coordinate encoding (?1006).
        // Like xterm, resetting any recognized tracking selector disables tracking.
        internal void SetMouseTrackingMode(int mode, bool enabled)
        {
            MouseModeX10PressOnly = enabled && mode == 9;
            MouseModeX10 = enabled && mode == 1000;
            MouseModeButtonEvent = enabled && mode == 1002;
            MouseModeAnyEvent = enabled && mode == 1003;
        }

        /// <summary>The active tracking selector: 0 for none, otherwise the mode that selects it (9, 1000, 1002 or 1003).</summary>
        internal int MouseTrackingSelector => MouseModeX10PressOnly ? 9 : MouseModeX10 ? 1000 : MouseModeButtonEvent ? 1002 : MouseModeAnyEvent ? 1003 : 0;

        /// <summary>The active coordinate encoding selector: 0 for the default, otherwise 1005, 1006, 1015 or 1016.</summary>
        internal int MouseEncodingSelector => MouseModeUtf8 ? 1005 : MouseModeSGR ? 1006 : MouseModeUrxvt ? 1015 : MouseModePixel ? 1016 : 0;

        internal void SetMouseTrackingSelector(int selector) => SetMouseTrackingMode(selector == 0 ? 1000 : selector, selector != 0);

        internal void SetMouseEncodingSelector(int selector)
        {
            MouseModeUtf8 = selector == 1005;
            MouseModeSGR = selector == 1006;
            MouseModeUrxvt = selector == 1015;
            MouseModePixel = selector == 1016;
        }

        // Coordinate encodings are one selection, as in xterm's extend_coords: setting any of ?1005, ?1006,
        // ?1015 or ?1016 replaces the others, and a reset is only effective against the encoding that is active.
        internal void SetMouseCoordinateEncoding(int mode, bool enabled)
        {
            if (enabled)
            {
                MouseModeUtf8 = mode == 1005;
                MouseModeSGR = mode == 1006;
                MouseModeUrxvt = mode == 1015;
                MouseModePixel = mode == 1016;
                return;
            }

            switch (mode)
            {
                case 1005: MouseModeUtf8 = false; break;
                case 1006: MouseModeSGR = false; break;
                case 1015: MouseModeUrxvt = false; break;
                case 1016: MouseModePixel = false; break;
            }
        }

        public ModeState Clone()
        {
            return new ModeState
            {
                MouseModeX10PressOnly = this.MouseModeX10PressOnly,
                MouseModeX10 = this.MouseModeX10,
                MouseModeButtonEvent = this.MouseModeButtonEvent,
                MouseModeAnyEvent = this.MouseModeAnyEvent,
                MouseModeSGR = this.MouseModeSGR,
                MouseModeUtf8 = this.MouseModeUtf8,
                MouseModeUrxvt = this.MouseModeUrxvt,
                MouseModePixel = this.MouseModePixel,
                IsApplicationCursorKeys = this.IsApplicationCursorKeys,
                IsApplicationKeypad = this.IsApplicationKeypad,
                IsBackarrowKeyMode = this.IsBackarrowKeyMode,
                IsAutoWrapMode = this.IsAutoWrapMode,
                IsReverseWrapMode = this.IsReverseWrapMode,
                IsOriginMode = this.IsOriginMode,
                IsLeftRightMarginMode = this.IsLeftRightMarginMode,
                IsRectangularAttributeExtent = this.IsRectangularAttributeExtent,
                IsReverseVideo = this.IsReverseVideo,
                IsSixelDisplayMode = this.IsSixelDisplayMode,
                IsFocusEventReporting = this.IsFocusEventReporting,
                IsBracketedPasteMode = this.IsBracketedPasteMode,
                IsClipboardPasteMode = this.IsClipboardPasteMode,
                IsCursorVisible = this.IsCursorVisible,
                IsCursorBlinkEnabled = this.IsCursorBlinkEnabled,
                CursorStyle = this.CursorStyle,
                CursorStyleIsDefault = this.CursorStyleIsDefault,
                IsInsertMode = this.IsInsertMode,
                IsLineFeedNewLineMode = this.IsLineFeedNewLineMode,
                _echoSetting = this._echoSetting,
                AlternateScrollOverride = this.AlternateScrollOverride,
                AlternateScrollDefault = this.AlternateScrollDefault,
                KittyKeyboard = this.KittyKeyboard.Clone(),
                ModifyOtherKeys = this.ModifyOtherKeys.Clone()
            };
        }
    }
}
