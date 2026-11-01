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
                // Flag 8 replaces text with key reports. Without flag 16, text-only IME
                // commits have no associated-text payload and are deliberately suppressed.
                bool suppress = _pendingKittyTextSuppression ?? ReportAllKeysActive;
                _pendingKittyTextSuppression = null;
                if (suppress)
                {
                    e.Handled = true;
                    return;
                }
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

            if (HandleKeyDownCore(e.Key, e.KeyModifiers, e.PhysicalKey, e.KeySymbol))
            {
                e.Handled = true;
            }
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (e.Handled)
            {
                _reportedKittyKeys?.Remove(KeyIdentity(e.Key, e.PhysicalKey));
                return;
            }
            if (HandleKeyUpCore(e.Key, e.KeyModifiers, e.PhysicalKey)) e.Handled = true;
        }

        private readonly record struct ReportedKittyKey(Key Key, KittyKeyIdentity Identity, Ntilde.VT.KittyKeyboardState State, int Generation);
        private Dictionary<(PhysicalKey Physical, Key Logical), ReportedKittyKey>? _reportedKittyKeys;

        private static (PhysicalKey, Key) KeyIdentity(Key key, PhysicalKey physical)
            => physical == PhysicalKey.None ? (physical, key) : (physical, Key.None);

        private TerminalKeyEventKind KittyEventKind(ref Key key, PhysicalKey physical, ref KittyKeyIdentity identity)
        {
            var keyId = KeyIdentity(key, physical);
            if (_reportedKittyKeys?.TryGetValue(keyId, out var held) == true)
            {
                if (ReferenceEquals(held.State, _buffer?.Modes.KittyKeyboard)
                    && held.Generation == held.State.Generation)
                {
                    key = held.Key;
                    identity = held.Identity;
                    return TerminalKeyEventKind.Repeat;
                }
                _reportedKittyKeys.Remove(keyId);
            }
            return TerminalKeyEventKind.Press;
        }

        internal bool HandleKeyUpCore(Key key, KeyModifiers modifiers, PhysicalKey physical = PhysicalKey.None)
        {
            if (_reportedKittyKeys?.Remove(KeyIdentity(key, physical), out var held) != true) return false;
            if (!_enableKittyKeyboardProtocol || _session?.IsProcessRunning != true
                || !ReferenceEquals(held.State, _buffer?.Modes.KittyKeyboard)
                || held.Generation != held.State.Generation) return false;
            string? sequence = TerminalInputModeEncoder.EncodeKittyKey(held.Key, modifiers, _buffer?.Modes, TerminalKeyEventKind.Release, held.Identity);
            if (sequence == null) return false;
            // A release reports state, without snapping a scrolled viewport to the live line.
            _session.SendInput(sequence);
            return true;
        }

        private bool? _pendingKittyTextSuppression;
        private int _kittyTextContextGeneration;
        private bool ReportAllKeysActive => _enableKittyKeyboardProtocol
            && ((_buffer?.Modes.KittyKeyboard.Flags ?? 0) & Ntilde.VT.KittyKeyboardState.FlagReportAllKeys) != 0;

        /// <summary>Context resets discard a pending callback; policy changes retain its captured decision.</summary>
        private void ResetKittyKeyTracking(bool discardPendingText = true)
        {
            _reportedKittyKeys?.Clear();
            if (!discardPendingText) return;
            _kittyTextContextGeneration++;
            if (_pendingKittyTextSuppression.HasValue) _pendingKittyTextSuppression = true;
        }

        /// <summary>
        /// Tracks an eligible key only after the session's input call returns. This is a
        /// fire-and-forget transport contract: it cannot acknowledge delivery to the child.
        /// A thrown write acquires no release; a context change during the call also discards it.
        /// </summary>
        private void SendKeyInput(Key key, KeyModifiers modifiers, PhysicalKey physical, string sequence, KittyKeyIdentity identity = default)
        {
            var session = _session;
            ReportedKittyKey? reported = null;
            if ((key != Key.None || physical != PhysicalKey.None)
                && _enableKittyKeyboardProtocol && _buffer?.Modes is { } modes && modes.KittyKeyboard.ReportEventTypes
                && (key == Key.Escape || TerminalInputModeEncoder.EncodeKittyKey(key, modifiers, modes, TerminalKeyEventKind.Repeat, identity) != null))
            {
                var state = modes.KittyKeyboard;
                reported = new(key, identity, state, state.Generation);
            }
            SendUserInput(sequence);
            if (reported is { } held && ReferenceEquals(session, _session)
                && ReferenceEquals(held.State, _buffer?.Modes.KittyKeyboard) && held.Generation == held.State.Generation)
            {
                (_reportedKittyKeys ??= new()).TryAdd(KeyIdentity(key, physical), held);
            }
        }

        /// <summary>Encoded unmodified recovery presses/repeats retain the pane's input-observer contract.</summary>
        private void SendKittyKeyInput(Key key, KeyModifiers modifiers, PhysicalKey physical, string sequence, KittyKeyIdentity identity)
        {
            if (modifiers == KeyModifiers.None && key == Key.Enter)
            {
                SendEnterKey(modifiers, physical, sequence, identity);
                return;
            }
            SendKeyInput(key, modifiers, physical, sequence, identity);
            if (modifiers == KeyModifiers.None && key == Key.Back)
                BackspaceObserved?.Invoke();
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

        /// <summary>
        /// Captures one text-capable keydown's policy so a ceiling/stack/kill-switch change before
        /// TextInput cannot duplicate encoded input or discard legacy text. Non-text modifier events
        /// do not replace the pending decision. A new text-capable keydown replaces it; TextInput
        /// consumes it once. Focus/session/buffer changes discard the pending callback instead.
        /// </summary>
        internal bool HandleKeyDownCore(Key key, KeyModifiers keyModifiers, PhysicalKey physicalKey = PhysicalKey.None, string? keySymbol = null)
        {
            int flags = _enableKittyKeyboardProtocol ? _buffer?.Modes.KittyKeyboard.Flags ?? 0 : 0;
            bool hasText = _session != null && TerminalInputModeEncoder.MayProduceTextInput(key, keySymbol);
            int context = _kittyTextContextGeneration;
            if (hasText) _pendingKittyTextSuppression = (flags & Ntilde.VT.KittyKeyboardState.FlagReportAllKeys) != 0;
            bool handled = HandleKeyDownCoreImpl(key, keyModifiers, physicalKey, keySymbol, flags);
            if (hasText)
            {
                // Consumed legacy keys leave their existing text-only behavior unchanged.
                bool? decision = handled ? null : false;
                if ((flags & Ntilde.VT.KittyKeyboardState.FlagReportAllKeys) != 0 || context != _kittyTextContextGeneration)
                    decision = true;
                _pendingKittyTextSuppression = decision;
            }
            return handled;
        }

        /// <summary>Routes a key using the flag snapshot captured for its corresponding text callback.</summary>
        private bool HandleKeyDownCoreImpl(Key key, KeyModifiers keyModifiers, PhysicalKey physicalKey, string? keySymbol, int kittyFlags)
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

            // Composition markers are not key identities, including when a physical slot is held.
            if (key is Key.ImeProcessed or Key.DeadCharProcessed) return false;

            KittyKeyIdentity identity = ResolveKittyInputIdentity(key, keyModifiers, physicalKey, keySymbol, kittyFlags);
            TerminalKeyEventKind eventKind = KittyEventKind(ref key, physicalKey, ref identity);
            ResetCursorBlink();

            // Kitty keyboard protocol (disambiguation and negotiated event reporting). Must run before every legacy path
            // below - including Alt-sends-ESC - because the protocol replaces those encodings
            // for the keys it claims. When the protocol is off (flags = 0) the encoder returns
            // null for every key and the legacy behavior below is byte-identical to before.
            //
            // _enableKittyKeyboardProtocol is the user-facing kill switch (Blocker 2,
            // TerminalSettings.EnableKittyKeyboardProtocol). When false, this call is skipped
            // entirely so every key falls through to the legacy path below unconditionally,
            // even if a TUI already pushed Kitty flags onto the buffer's ModeState.
            if (kittyFlags != 0 && TryEncodeKittyKey(key, keyModifiers, eventKind, identity, kittyFlags, out string? kittySequence))
            {
                SendKittyKeyInput(key, keyModifiers, physicalKey, kittySequence, identity);
                return true;
            }

            // Alt/Meta-sends-ESC must run BEFORE the unconditional Enter/Back/Tab/Escape cases
            // below, which would otherwise swallow Alt+<those> and send the bare control byte.
            if ((keyModifiers & KeyModifiers.Alt) != 0)
            {
                string? altSequence = TerminalInputModeEncoder.EncodeAltKey(key, keyModifiers);
                if (altSequence != null)
                {
                    SendKeyInput(key, keyModifiers, physicalKey, altSequence, identity);
                    return true;
                }
            }

            if (HandleLegacyKeyDown(key, keyModifiers, physicalKey, identity)) return true;

            // Cursor, editing and function keys, with their modifiers (Ctrl+Left -> CSI 1;5D). Every
            // modifier combination not claimed by Kitty event reporting arrives here:
            // EncodeAltKey declines non-printable keys (so Alt+arrow lands here too),
            // and the Ctrl branch above only claims letters. Command Assist's interceptor matches
            // its bindings with exact modifiers, so by default it takes only the unmodified
            // Up/Down. App chords on these keys never get this far - MainWindow's tunnel handler
            // takes Ctrl+Shift+PageUp/PageDown (move tab) and Alt+arrow when there is a pane to
            // move to, and marks the event handled first.
            string? sequence = TerminalInputModeEncoder.EncodeSpecialKey(key, keyModifiers, _buffer?.Modes);
            if (sequence != null)
            {
                SendKeyInput(key, keyModifiers, physicalKey, sequence);
                return true;
            }

            return false;
        }

        /// <summary>Captures primary identity for flag 8, and optional metadata for flag 4.</summary>
        private static KittyKeyIdentity ResolveKittyInputIdentity(Key key, KeyModifiers modifiers, PhysicalKey physical, string? symbol, int flags)
        {
            if ((flags & Ntilde.VT.KittyKeyboardState.FlagReportAllKeys) != 0)
                return TerminalInputModeEncoder.ResolveKittyAllKeyIdentity(key, modifiers, physical, symbol);
            return (flags & Ntilde.VT.KittyKeyboardState.FlagReportAlternateKeys) != 0
                ? TerminalInputModeEncoder.ResolveKittyKeyIdentity(key, modifiers, physical, symbol) : default;
        }

        /// <summary>Legacy control/recovery keys, after Kitty and Alt encoders decline them.</summary>
        private bool HandleLegacyKeyDown(Key key, KeyModifiers keyModifiers, PhysicalKey physicalKey, KittyKeyIdentity identity)
        {
            switch (key)
            {
                case Key.Enter:
                    // Let the pane's reconnect-on-Enter binding handle an exited session.
                    if (_session?.IsProcessRunning != true) return false;
                    return SendEnterKey(keyModifiers, physicalKey);
                case Key.Back:
                    SendKeyInput(key, keyModifiers, physicalKey, "\x7f");
                    BackspaceObserved?.Invoke();
                    return true;
                case Key.Tab:
                    SendKeyInput(key, keyModifiers, physicalKey, (keyModifiers & KeyModifiers.Shift) != 0 ? "\x1b[Z" : "\t");
                    return true;
                case Key.Escape:
                    SendKeyInput(key, keyModifiers, physicalKey, "\x1b");
                    return true;
                case Key.C:
                    return HandleLegacyControlC(keyModifiers, physicalKey, identity);
                case Key.V:
                    // Paste belongs to MainWindow's tunnel handler.
                    return false;
                default:
                    return SendLegacyControlKey(key, keyModifiers, physicalKey, identity);
            }
        }

        /// <summary>Preserves command-history reads before Enter's input bytes, and persistence after them.</summary>
        private bool SendEnterKey(KeyModifiers keyModifiers, PhysicalKey physicalKey, string sequence = "\r", KittyKeyIdentity identity = default)
        {
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
                SendKeyInput(Key.Enter, keyModifiers, physicalKey, sequence, identity);
            }

            EnterObserved?.Invoke();
            return true;
        }

        /// <summary>Off macOS, Ctrl+C copies a selection; otherwise it sends the interrupt byte.</summary>
        private bool HandleLegacyControlC(KeyModifiers keyModifiers, PhysicalKey physicalKey, KittyKeyIdentity identity)
        {
            if ((keyModifiers & (KeyModifiers.Control | KeyModifiers.Alt)) != KeyModifiers.Control) return false;
            if (HasSelection() && !UseMacOSClipboardChords)
            {
                _ = CopySelectionToClipboard();
                ClearSelection();
            }
            else
            {
                SendKeyInput(Key.C, keyModifiers, physicalKey, "\x03", identity);
            }
            return true;
        }

        /// <summary>Encodes legacy Ctrl letters/punctuation, leaving AltGr text untouched.</summary>
        private bool SendLegacyControlKey(Key key, KeyModifiers keyModifiers, PhysicalKey physicalKey, KittyKeyIdentity identity)
        {
            if ((keyModifiers & KeyModifiers.Control) == 0) return false;
            string? sequence = TerminalInputModeEncoder.EncodeLegacyControlKey(key, keyModifiers);
            if (sequence == null) return false;
            SendKeyInput(key, keyModifiers, physicalKey, sequence, identity);
            return true;
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
        private bool TryEncodeKittyKey(Key key, KeyModifiers keyModifiers, TerminalKeyEventKind eventKind, KittyKeyIdentity identity, int flags, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? sequence)
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

            sequence = TerminalInputModeEncoder.EncodeKittyKey(key, keyModifiers, flags, eventKind, identity);
            return sequence != null;
        }
        // Kill switch for the kitty keyboard protocol (Blocker 2, #277
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
