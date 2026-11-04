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

        /// <summary>DECSDM (?80): xterm polarity, set = page-origin graphics without SIXEL-driven scrolling.</summary>
        public bool IsSixelDisplayMode { get; set; }

        /// <summary>DECKPAM / DECKPNM and DECNKM (?66): application auxiliary keypad.</summary>
        public bool IsApplicationKeypad { get; set; }

        // Cursor and display modes
        public bool IsApplicationCursorKeys { get; set; } // ?1 - DECCKM (Application Cursor Keys)
        public bool IsAutoWrapMode { get; set; } = true;  // ?7 - DECAWM (Auto Wrap Mode)
        public bool IsOriginMode { get; set; }            // ?6 - DECOM (Origin Mode)

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
        public bool IsEchoEnabled { get; set; } = true;   // 12 - SRM (Send/Receive Mode)

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

        public ModeState Clone()
        {
            return new ModeState
            {
                MouseModeX10PressOnly = this.MouseModeX10PressOnly,
                MouseModeX10 = this.MouseModeX10,
                MouseModeButtonEvent = this.MouseModeButtonEvent,
                MouseModeAnyEvent = this.MouseModeAnyEvent,
                MouseModeSGR = this.MouseModeSGR,
                IsApplicationCursorKeys = this.IsApplicationCursorKeys,
                IsApplicationKeypad = this.IsApplicationKeypad,
                IsAutoWrapMode = this.IsAutoWrapMode,
                IsOriginMode = this.IsOriginMode,
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
                IsEchoEnabled = this.IsEchoEnabled,
                AlternateScrollOverride = this.AlternateScrollOverride,
                AlternateScrollDefault = this.AlternateScrollDefault,
                KittyKeyboard = this.KittyKeyboard.Clone(),
                ModifyOtherKeys = this.ModifyOtherKeys.Clone()
            };
        }
    }
}
