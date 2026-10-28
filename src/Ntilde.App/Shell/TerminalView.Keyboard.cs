using Avalonia.Input;

namespace Ntilde.Shell
{
    /// <summary>
    /// Keyboard and text input: keys to the session (with the kitty keyboard protocol and the macOS clipboard
    /// chords), and the input events the pane observes.
    /// </summary>
    public partial class TerminalView
    {
        protected override void OnTextInput(TextInputEventArgs e)
        {
            base.OnTextInput(e);
            if (!e.Handled && _session != null && !string.IsNullOrEmpty(e.Text))
            {
                ResetCursorBlink();
                SendUserInput(e.Text);
                TextInputObserved?.Invoke(e.Text);
                e.Handled = true;
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled) return;

            if (HandleKeyDownCore(e.Key, e.KeyModifiers))
            {
                e.Handled = true;
            }
        }

        internal Func<Key, KeyModifiers, bool>? KeyDownInterceptor { get; set; }

        /// <summary>
        /// macOS clipboard conventions: Cmd+C copies, and Ctrl+C is always the interrupt. Off macOS,
        /// Ctrl+C copies when there is a selection. Settable so tests can pin either behavior on any OS.
        /// </summary>
        internal bool UseMacOSClipboardChords { get; set; } = OperatingSystem.IsMacOS();

        /// <summary>
        /// True for Cmd+<paramref name="expected"/> with no other modifier, on macOS only.
        /// </summary>
        internal static bool IsMacClipboardChord(Key key, KeyModifiers modifiers, Key expected, bool isMacOS)
        {
            const KeyModifiers relevant = KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift | KeyModifiers.Meta;
            return isMacOS && key == expected && (modifiers & relevant) == KeyModifiers.Meta;
        }

        internal bool HandleKeyDownCore(Key key, KeyModifiers keyModifiers)
        {
            if (KeyDownInterceptor?.Invoke(key, keyModifiers) == true)
            {
                return true;
            }

            // Cmd+C is the macOS copy chord. It is consumed whether or not there is a selection -
            // Cmd has no terminal encoding, so letting it fall through would send a bare 'c' (or
            // a kitty CSI sequence) to the shell. The selection is kept, as in Terminal.app/iTerm2.
            // Cmd+V is the "paste" binding's macOS default, dispatched by MainWindow.
            if (IsMacClipboardChord(key, keyModifiers, Key.C, UseMacOSClipboardChords))
            {
                if (HasSelection())
                {
                    _ = CopySelectionToClipboard();
                }
                return true;
            }

            if (_session == null)
            {
                return false;
            }

            ResetCursorBlink();

            // Handle keys that don't generate text input (Control codes, arrows, etc.)
            // Logic copied from MainWindow
            bool isCtrl = (keyModifiers & KeyModifiers.Control) != 0;

            // Kitty keyboard protocol (disambiguate tier). Must run before every legacy path
            // below - including Alt-sends-ESC - because the protocol replaces those encodings
            // for the keys it claims. When the protocol is off (flags = 0) the encoder returns
            // null for every key and the legacy behavior below is byte-identical to before.
            //
            // _enableKittyKeyboardProtocol is the user-facing kill switch (Blocker 2,
            // TerminalSettings.EnableKittyKeyboardProtocol). When false, this call is skipped
            // entirely so every key falls through to the legacy path below unconditionally,
            // even if a TUI already pushed flag 1 onto the buffer's ModeState.
            if (_enableKittyKeyboardProtocol && TryEncodeKittyKey(key, keyModifiers, out string? kittySequence))
            {
                SendUserInput(kittySequence!);
                return true;
            }

            // Alt/Meta-sends-ESC must run BEFORE the unconditional Enter/Back/Tab/Escape cases
            // below, which would otherwise swallow Alt+<those> and send the bare control byte.
            if ((keyModifiers & KeyModifiers.Alt) != 0)
            {
                string? altSequence = TerminalInputModeEncoder.EncodeAltKey(key, keyModifiers);
                if (altSequence != null)
                {
                    SendUserInput(altSequence);
                    return true;
                }
            }

            switch (key)
            {
                case Key.Enter:
                    if (!_session.IsProcessRunning)
                    {
                        // The session has exited (e.g. SSH disconnected) but the dead session
                        // object is kept around so the "[Press Enter to reconnect]" banner works.
                        // Don't swallow Enter into the dead PTY — let it bubble up to
                        // TerminalPane.OnKeyDown, which owns the reconnect-on-Enter logic.
                        return false;
                    }
                    // Two phases around the carriage return, and which side each lands on is
                    // load-bearing (Codex P1 and P2 on #448).
                    //
                    // EnterObserving must precede the CR. It is where the command line is read out
                    // of the grid, and that read is only truthful while the line is still on screen:
                    // the moment the CR reaches the PTY the shell may repaint over it, and - since
                    // the OSC 133 command-input window closes synchronously on the parse thread - a
                    // fast shell answering with a bare 133;C can shut the window before this method's
                    // next statement runs, at which point the read is refused and the command is
                    // lost from history silently. Cost is one grid read under the buffer's read lock.
                    //
                    // EnterObserved must NOT. It is where history persistence starts, and
                    // JsonlHistoryStore.AppendAsync does redaction, migration checks, directory
                    // creation and a file open synchronously before its first incomplete await.
                    // Slow storage has no business sitting between a keypress and the shell.
                    //
                    // finally, so a throwing observer cannot swallow the user's Enter: the keystroke
                    // reaches the shell either way, and the exception still propagates behind it.
                    try
                    {
                        EnterObserving?.Invoke();
                    }
                    finally
                    {
                        SendUserInput("\r");
                    }

                    EnterObserved?.Invoke();
                    return true;
                case Key.Back:
                    SendUserInput("\x7f");
                    BackspaceObserved?.Invoke();
                    return true;
                case Key.Tab:
                    // Shift+Tab must emit the back-tab (CBT) sequence ESC [ Z, exactly as
                    // xterm does. TUIs like Claude Code rely on it for reverse navigation
                    // (e.g. cycling permission modes backward); a literal tab breaks that.
                    SendUserInput((keyModifiers & KeyModifiers.Shift) != 0 ? "\x1b[Z" : "\t");
                    return true;
                case Key.Escape:
                    SendUserInput("\x1b");
                    return true;

                default:
                    if (isCtrl)
                    {
                        // Ctrl+A..Z -> 0x01..0x1A, plus the Ctrl+punctuation/digit C0 rows
                        // (Ctrl+\ -> FS, Ctrl+] -> GS, Ctrl+_ -> US, ...). Declines AltGr
                        // (Ctrl+Alt) so composed text still arrives through OnTextInput. App
                        // shortcuts on these chords never get here: MainWindow's tunnel handler
                        // consumes them first.
                        string? controlSequence = TerminalInputModeEncoder.EncodeLegacyControlKey(key, keyModifiers);
                        if (controlSequence != null)
                        {
                            SendUserInput(controlSequence);
                            return true;
                        }
                    }
                    break;

                case Key.C:
                    if (isCtrl)
                    {
                        // Copy-on-Ctrl+C is the Windows/Linux convenience. macOS copies with Cmd+C
                        // (above), so there Ctrl+C is always the interrupt, selection or not.
                        if (HasSelection() && !UseMacOSClipboardChords)
                        {
                            _ = CopySelectionToClipboard();
                            ClearSelection();
                        }
                        else
                        {
                            SendUserInput("\x03");
                        }
                        return true;
                    }
                    break;
                case Key.V:
                    if (isCtrl)
                    {
                        // Clipboard paste - handled by wrapping logic or we need dependency injection?
                        // TerminalView technically doesn't know about Window's PasteFromClipboard.
                        // But providing a way to paste is essential.
                        // For now we might leave CTRL+V in MainWindow or raise an event?
                        // MainWindow is better for accessing Clipboard safely.
                        // So we WON'T handle Ctrl+V here, let it bubble/tunnel?
                        // Actually MainWindow has a global handler for Ctrl+V.
                        // If we don't handle it here, MainWindow will see it?
                        // MainWindow's handler is Tunnel, so it sees it BEFORE this.
                        // So Ctrl+V is fine in MainWindow.
                    }
                    break;

                    // Arrows
            }

            // Cursor, editing and function keys, with their modifiers (Ctrl+Left -> CSI 1;5D). Every
            // modifier combination of these keys arrives here: the kitty encoder leaves functional
            // keys alone, EncodeAltKey declines non-printable keys (so Alt+arrow lands here too),
            // and the Ctrl branch above only claims letters. Command Assist's interceptor matches
            // its bindings with exact modifiers, so by default it takes only the unmodified
            // Up/Down. App chords on these keys never get this far - MainWindow's tunnel handler
            // takes Ctrl+Shift+PageUp/PageDown (move tab) and Alt+arrow when there is a pane to
            // move to, and marks the event handled first.
            string? sequence = TerminalInputModeEncoder.EncodeSpecialKey(key, keyModifiers, _buffer?.Modes);
            if (sequence != null)
            {
                SendUserInput(sequence);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Applies the two Ntilde-local carve-outs that must win over the kitty keyboard
        /// protocol, then defers to <see cref="TerminalInputModeEncoder.EncodeKittyKey"/>.
        ///
        /// 1. Enter on a dead session still has to bubble up to TerminalPane's
        ///    "[Press Enter to reconnect]" handler instead of being swallowed into a dead PTY.
        /// 2. Ctrl+C with an active selection is copy-to-clipboard, matching the behavior users
        ///    already have; without a selection it is a real Ctrl+C and the protocol encodes it
        ///    as CSI 99;5u (the spec explicitly notes Ctrl+C stops raising SIGINT in this mode).
        ///    Not on macOS, where copy is Cmd+C and Ctrl+C is always the real key.
        /// </summary>
        private bool TryEncodeKittyKey(Key key, KeyModifiers keyModifiers, out string? sequence)
        {
            sequence = null;

            if (key == Key.Enter && _session?.IsProcessRunning != true)
            {
                return false;
            }

            if (key == Key.C && (keyModifiers & KeyModifiers.Control) != 0 && HasSelection() && !UseMacOSClipboardChords)
            {
                return false;
            }

            sequence = TerminalInputModeEncoder.EncodeKittyKey(key, keyModifiers, _buffer?.Modes);
            return sequence != null;
        }
        // Kill switch for the kitty keyboard protocol's disambiguate tier (Blocker 2, #277
        // review). Defaults true so behavior is unchanged until ApplySettings runs; a fresh
        // TerminalView created outside the settings pipeline (as several tests do) still gets
        // the protocol, matching pre-kill-switch behavior.
        private bool _enableKittyKeyboardProtocol = true;
        public event Action<string>? TextInputObserved;
        public event Action? BackspaceObserved;
        /// <summary>
        /// Enter, before the carriage return reaches the PTY: the grid still holds the command line
        /// exactly as the user submitted it. For reads only - see the <c>Key.Enter</c> case.
        /// </summary>
        public event Action? EnterObserving;

        /// <summary>
        /// Enter, after the carriage return has gone. For work that must not delay the keystroke.
        /// </summary>
        public event Action? EnterObserved;
        public event Action<string>? PasteObserved;

        /// <summary>
        /// Sends user-originated input (keystroke, typed text, pasted or dropped text) to
        /// the PTY after returning the viewport to the live line, so what the user is
        /// writing stays visible even when they had scrolled up into scrollback.
        /// </summary>
        private void SendUserInput(string text)
        {
            ScrollToInputLine();
            _session?.SendInput(text);
        }
    }
}
