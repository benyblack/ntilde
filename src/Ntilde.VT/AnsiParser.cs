using System.Globalization;

namespace Ntilde.VT
{
    public partial class AnsiParser
    {
        private TerminalBuffer _buffer;
        private enum State { Normal, Esc, Csi, Osc, OscEsc, Dcs, DcsEsc, Charset, Apc, ApcEsc, EscHash }
        private State _state = State.Normal;

        // Flag to swallow a single newline after an inline image (common in scripts)
        private bool _swallowNextNewline;

        // DEC G0..G3 charset designation + GL shift state.
        // mc/ncurses draw box characters via terminfo `smacs`/`rmacs` (SO/SI) and `ESC ( 0`
        // to designate G0 as DEC Special Graphics; printable letters then map per the table.
        private enum Charset { Ascii, DecSpecialGraphics }
        private readonly Charset[] _charsets = new Charset[] { Charset.Ascii, Charset.Ascii, Charset.Ascii, Charset.Ascii };
        private int _gl; // 0..3 — index into _charsets currently shifted to GL
        private int _pendingCharsetSlot = -1;

        // Zero-alloc buffers
        private char[] _paramBuffer = new char[256];
        private int _paramLen;

        // Set when a CSI's parameter/intermediate run overflows MaxCsiParamChars. What fell off
        // the end may have been an INTERMEDIATE byte, and an intermediate is part of the
        // sequence's identity rather than decoration - so the surviving prefix cannot be
        // classified. See the final-byte branch in Process().
        private bool _csiTruncated;
        private const int MaxCsiParamChars = 65536;

        // Upper bound on a single parsed CSI numeric parameter. Operations like Scroll Up/Down
        // (CSI Ps S/T), Repeat (CSI Ps b), and Insert/Delete Lines/Chars loop or process Ps times,
        // so an unbounded value (e.g. CSI 333333261 S) lets a hostile stream wedge the parser for
        // tens of seconds — a DoS. 65535 is far beyond any real screen dimension yet keeps every
        // such loop trivially fast, and also prevents int overflow during digit accumulation.
        private const int MaxCsiParamValue = 65535;

        // Hard safety cap on string-type control sequences (OSC/DCS/APC) to bound memory
        // against a hostile or runaway stream that never sends a terminator. Unlike CSI
        // params, these legitimately carry large payloads — iTerm2 (OSC 1337) and tunneled
        // (OSC 1339) inline images, Sixel images (DCS), and Kitty graphics (APC) — so the
        // cap is generous rather than the 64 KiB used for CSI. Past the cap we stop
        // accumulating and let the sequence terminate (a truncated image simply fails to
        // decode), mirroring how CSI params stop growing at MaxCsiParamChars.
        // Settable so tests can drive the cap with small payloads instead of allocating
        // tens of MB; the default is the production value.
        public int MaxStringSequenceChars { get; set; } = 16 * 1024 * 1024; // 16 Mi chars (~32 MB UTF-16)

        // ConPTY Sync Fix: Track vertical offset caused by inline images that ConPTY doesn't see.
        // This effectively "scrolls" the PTY's logical cursor to match our visual cursor.
        private int _verticalOffset;

        private List<char> _oscStringBuffer = new List<char>(); // OSC strings can be long (titles, etc) - Keep List for now or limit
        private List<char> _apcStringBuffer = new List<char>();
        private List<char> _dcsStringBuffer = new List<char>();
        private System.Text.StringBuilder _kittyPayloadBuffer = new System.Text.StringBuilder();

        // Clear() keeps a buffer's capacity, so one inline image used to pin its megabytes of
        // chars for the life of the pane. A buffer that grew past this is replaced instead; short
        // sequences (titles, cwd, hyperlinks) keep reusing theirs.
        private const int RetainedStringBufferChars = 64 * 1024;
        private bool _kittyPayloadOverflow; // set once a chunked Kitty payload exceeds the cap
        private Dictionary<string, string> _kittyPendingParams = new();
        private readonly bool _isConPtyFilteringLikely;

        public IImageDecoder? ImageDecoder { get; set; }

        /// <summary>
        /// File transport (<c>t=f</c>) reader for kitty graphics, injected by the host the same
        /// way <see cref="ImageDecoder"/> is: the VT layer must stay free of file I/O, so the
        /// parser hands the decoded payload path to this delegate and the App layer decides what
        /// may be read (temp-dir confinement, size caps). When null, or when the read fails,
        /// a <c>t=f</c> image is logged and skipped.
        /// </summary>
        public Func<string, byte[]?>? ReadFileBytes { get; set; }

        /// <summary>
        /// Host-settable opt-in for native (non-tunneled) kitty graphics APC on platforms where
        /// ConPTY filtering is likely (Windows). The historical M4.2 policy dropped such images
        /// and answered capability probes with ERR because ConPTY was assumed to strip APC; live
        /// probing showed modern ConPTY passes well-formed APC through intact, so when this flag
        /// is set the parser trusts what actually arrived: probes are answered OK and images are
        /// decoded, exactly as on non-Windows. Tunneled OSC 1339 payloads are unaffected.
        /// </summary>
        public bool AllowNativeKittyGraphics { get; set; }

        /// <summary>
        /// Kitty in-band resize (DEC private mode 2048) tracking. The client enables the mode
        /// with <c>CSI ? 2048 h</c>; while enabled the terminal must announce geometry changes
        /// by writing <see cref="SendInBandResize"/> reports to the child's stdin. Clients like
        /// terminal-browser run on Windows with no SIGWINCH and no console-size polling — the
        /// in-band report is the only way they learn the window resized.
        /// </summary>
        public bool InBandResizeReportsEnabled => _inBandResizeReportsEnabled;
        private bool _inBandResizeReportsEnabled;

        /// <summary>
        /// Emits a kitty in-band resize report (<c>CSI 48 ; rows ; cols ; heightPx ; widthPx t</c>)
        /// to the child, but only while the client has mode 2048 set — unsolicited reports to a
        /// client that never asked would land on its stdin as garbage. The host calls this when
        /// the pane's geometry changes; the numbers are host-computed, so unlike most replies
        /// there is nothing remote-controlled to sanitize. Field order matches the kitty spec:
        /// rows, cols, then pixel height, then pixel width.
        /// </summary>
        public void SendInBandResize(int rows, int cols, int widthPx, int heightPx)
        {
            if (!_inBandResizeReportsEnabled || OnResponse == null) return;
            OnResponse($"\x1b[48;{Math.Max(1, rows)};{Math.Max(1, cols)};{Math.Max(0, heightPx)};{Math.Max(0, widthPx)}t");
        }

        // M2.1: Lock Batching Buffer
        private System.Text.StringBuilder _textBuffer = new System.Text.StringBuilder(4096);
        private bool _sawCursorHideInBatch;
        private bool _sawCursorShowAfterHideInBatch;

        /// <summary>
        /// The graphic character <c>REP</c> (<c>CSI Ps b</c>) would repeat, or <c>null</c> when
        /// there is nothing repeatable. Set by <see cref="FlushText"/>; cleared by anything that
        /// is not a graphic character.
        /// </summary>
        /// <remarks>
        /// Tracked here rather than read back from the grid, because ECMA-48 §8.3.103 scopes the
        /// repeat to the preceding <em>graphic</em> character: after a control, an escape sequence
        /// or a cursor move there is nothing to repeat, and the cell under the cursor cannot say
        /// so — it still holds whatever was painted there earlier. A stale repeat is worse than no
        /// repeat, so the state is explicit and clears on everything it cannot vouch for.
        ///
        /// A <c>char</c> rather than a string, which also settles the multi-char cases: a
        /// surrogate half or a combining mark is not a character anyone can repeat on its own, so
        /// <see cref="TrailingRepeatableChar"/> answers null for them and REP becomes a no-op
        /// rather than repeating half a grapheme. It also keeps the per-flush cost at a field
        /// write, with no allocation on the print path.
        /// </remarks>
        private char? _lastGraphicChar;
        private static readonly TimeSpan CursorTransientSuppressionWindow = TimeSpan.FromMilliseconds(60);

        /// <summary>Optional authoritative device-pixel metrics for SIXEL layout (mux stream or local host).</summary>
        private SixelCellSize? _sixelCellSizeOverride;
        /// <summary>Host policy for image protocols without headless geometry support. SIXEL remains independent.</summary>
        public bool AllowNonSixelImages { get; set; } = true;

        public SixelCellSize? SixelCellSizeOverride
        {
            get => System.Threading.Volatile.Read(ref _sixelCellSizeOverride);
            set => System.Threading.Volatile.Write(ref _sixelCellSizeOverride, value);
        }
        public float? SixelCellWidthPx => SixelCellSizeOverride?.WidthPx;
        public float? SixelCellHeightPx => SixelCellSizeOverride?.HeightPx;

        public float CellWidth { get; set; } = 10.0f;  // Default fallback
        public float CellHeight { get; set; } = 20.0f; // Default fallback

        /// <summary>
        /// Colors reported in response to OSC 10/11 (foreground/background) queries. The host
        /// (Ntilde.App) sets these from the active theme; when unset, <see cref="HandleOsc"/>
        /// falls back to a sane default rather than staying silent (#265: silence made OpenCode and
        /// vim/nvim's startup theme probes stall for their ~1s timeout on every launch).
        /// </summary>
        public TermColor? DefaultForeground { get; set; }

        /// <summary>
        /// Host-settable kill switch for the kitty keyboard protocol (issue #266 / PR #277
        /// review, Blocker 2), wired the same way <see cref="DefaultForeground"/> was in
        /// PR #275: the App layer sets this from <c>TerminalSettings.EnableKittyKeyboardProtocol</c>
        /// at parser creation and on every <c>ApplySettings</c>. Defaults to true (protocol on).
        ///
        /// When false, push/pop/set are still parsed and update <see cref="ModeState.KittyKeyboard"/>
        /// normally - this only gates the <c>CSI ? u</c> query reply, which always reports flags 0
        /// while disabled so a TUI that queries capabilities does not believe the protocol is
        /// active. The App-side encoder is gated separately (TerminalView never calls
        /// TryEncodeKittyKey while its own copy of the setting is off), so the two together give
        /// full protocol-off behavior even though the underlying stack state is untouched.
        /// </summary>
        public bool KittyKeyboardEnabled { get; set; } = true;

        /// <summary>
        /// Terminal-to-host replies (DA, DSR, DECRPM, OSC color answers, ...).
        ///
        /// SECURITY: whatever is passed here is written verbatim to the child process's stdin -
        /// the App layer wires this straight to <c>Session.SendInput</c>, i.e. the pty / ssh
        /// channel, with no sanitization in between. A reply therefore MUST NEVER contain
        /// untrusted bytes, and above all never CR (0x0D) or LF (0x0A): at a shell prompt those
        /// submit the line, so an echoed parameter becomes command execution by anything that
        /// can write to the terminal (a <c>cat</c> of a hostile file, a compromised remote host,
        /// a crafted log line or git branch name in a prompt). The OSC/APC accumulators only
        /// treat BEL / 0x9C / ESC as terminators, so CR and LF do reach sequence parameters.
        ///
        /// Any reply that interpolates a value taken from the incoming stream must run it
        /// through <see cref="SanitizeEchoParameter"/> first. Fixed-format and numeric replies
        /// are inherently safe and need nothing.
        /// </summary>
        public Action<string>? OnResponse { get; set; }
        public Action? OnBell { get; set; }
        public Action<string>? OnWorkingDirectoryChanged { get; set; }
        public Action<string>? OnTitleChanged { get; set; }

        /// <summary>
        /// Raised when an OSC 9 desktop-notification sequence (issue #271) arrives:
        /// <c>OSC 9 ; text ST/BEL</c> (ConEmu-style, as emitted by Claude Code). The parser
        /// only flattens and caps the text; whether to surface a notification (settings gate,
        /// focus policy) and how to show it are the App layer's call — the same split as
        /// <see cref="OnClipboardWrite"/>.
        /// </summary>
        public Action<string>? OnDesktopNotification { get; set; }

        /// <summary>
        /// Raised when an OSC 9;4 progress sequence (issue #271) arrives:
        /// <c>OSC 9 ; 4 ; state [; progress] ST/BEL</c>. <paramref name="state"/> is the raw
        /// integer: 0 removes the indication, 1 normal, 2 error, 3 indeterminate, 4 paused.
        /// <paramref name="percent"/> is 0–100 (clamped), or <see langword="null"/> when the
        /// sequence omitted it — legal for indeterminate. Where the progress renders (tab
        /// header, taskbar) is the App layer's call.
        /// </summary>
        public Action<int, int?>? OnProgressReported { get; set; }

        /// <summary>
        /// Raised when an OSC 52 clipboard-write sequence (issue #268) decodes successfully:
        /// <c>target</c> is the raw targets string from the sequence (already defaulted to
        /// "c" if the sequence omitted it) and <c>data</c> is the base64-decoded payload,
        /// size-capped at <see cref="Osc52MaxDecodedBytes"/>. Decoding, target defaulting,
        /// the size cap, and the query-denial reply are all handled in this parser (see
        /// <see cref="HandleOscClipboard"/>) - Ntilde.VT does not know about settings
        /// or the system clipboard by design. The App layer decides whether to honor this
        /// event (its own settings gate) and how to reach the clipboard.
        ///
        /// Clipboard READ (answering a query with real clipboard contents) is never
        /// implemented; queries always get the empty-payload denial reply via
        /// <see cref="OnResponse"/>, never this event.
        /// </summary>
        public Action<string, byte[]>? OnClipboardWrite { get; set; }

        /// <summary>OSC 5522 packets. The presenting host owns permissions, transfers and clipboard I/O.</summary>
        public Action<string>? OnKittyClipboardPacket { get; set; }
        /// <summary>Host opt-in; headless and mux parsers must not advertise unavailable clipboard services.</summary>
        public bool KittyClipboardEnabled { get; set; }
        /// <summary>Invalidates host clipboard grants and in-progress transfers on RIS.</summary>
        public Action? OnKittyClipboardReset { get; set; }
        public Action? OnPromptReady { get; set; }

        /// <summary>
        /// OSC 133;C — the shell accepted the line and is about to run it. The argument is the
        /// command text when the mark carried a payload we could make sense of, and
        /// <see langword="null"/> when it did not.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Raised for <em>every</em> C mark, payload or not. Ntilde's own bootstraps always send
        /// <c>133;C;&lt;base64&gt;</c>, but FinalTerm does not require a payload and the
        /// third-party snippets this event now has to consume — iTerm2's, VS Code's, and hand-rolled
        /// ones — routinely emit a bare <c>133;C</c> or a bare <c>133;C;</c>. Dropping those was
        /// survivable while the only armed sessions were ones we had instrumented ourselves
        /// (V2 Phase 2b changed that): C is the edge that closes Command Assist's command-input
        /// window, so a swallowed C leaves the grid reader serving a running command's output as a
        /// command line until D arrives.
        /// </para>
        /// <para>
        /// A <see langword="null"/> argument therefore means "the line was submitted, and I cannot
        /// tell you what it was" — a lifecycle fact with no text attached. Consumers must treat it
        /// as the lifecycle edge and must not treat it as proof that the shell reports command
        /// text (which is what stands the heuristic capture path down).
        /// </para>
        /// </remarks>
        public Action<string?>? OnCommandAccepted { get; set; }
        /// <summary>
        /// OSC 133;B — prompt end / start of the user's input line. The argument carries where
        /// the mark landed in the buffer (see <see cref="ShellIntegrationMark"/>), which is the
        /// anchor for reading the live command line out of the grid.
        /// </summary>
        /// <remarks>
        /// B is <b>not</b> "the command began executing" — that edge is OSC 133;C
        /// (<see cref="OnCommandAccepted"/>). B fires once per prompt, including on every
        /// prompt repaint, and the shell is idle waiting for input when it does.
        /// </remarks>
        public Action<ShellIntegrationMark>? OnCommandStarted { get; set; }
        public Action<int?>? OnCommandFinished { get; set; }
        public Action<int?, long?>? OnCommandFinishedDetailed { get; set; }

        /// <summary>
        /// Interns OSC 8 hyperlink identities. Per-parser rather than static: two terminals must not share
        /// a link table, or the same (URI, id) pair written in one pane would group with cells in another.
        /// </summary>
        private readonly Links.HyperlinkRegistry _hyperlinks = new();

        public AnsiParser(TerminalBuffer buffer, bool? forceConPtyFiltering = null)
        {
            _buffer = buffer;
            _isConPtyFilteringLikely = forceConPtyFiltering ?? DetectConPtyFiltering();
        }

        public bool IsConPtyFilteringLikely => _isConPtyFilteringLikely;

        private static bool DetectConPtyFiltering()
        {
            // Windows I/O currently always flows through ConPTY (rusty_pty), which
            // strips DCS/APC image control strings — filtering is always likely there,
            // so Kitty graphics must use the tunneled mode. The former WT_SESSION /
            // TERM_PROGRAM env probes were dead code (the fallthrough returned true
            // regardless, #169); if a future Windows backend bypasses ConPTY, make
            // this depend on the backend instead of the OS.
            return OperatingSystem.IsWindows();
        }

        private void FlushText()
        {
            if (_textBuffer.Length > 0)
            {
                // Printable output closes the window in which a post-image newline may be
                // swallowed. The flag means "absorb the newline the image-emitting program sends
                // immediately after its picture" - once anything else has been printed, a newline
                // is the program's own line break and must not be eaten.
                //
                // ExecuteControl's single-character path already clears it for the same reason, but
                // printable text does not go through there: it accumulates in _textBuffer and lands
                // here, so the flag used to survive arbitrary output. Only HandleITerm2Image armed
                // it, and imgcat-style emitters send their newline straight after the image, so the
                // flag was always consumed before anything could reveal that. Arming it for sixel
                // too (#405) surfaced it: `cat` of a sixel file sends no trailing newline, the flag
                // stayed live across the whole next prompt, and it swallowed the line break between
                // the following command's echo and its output - rendering `echo done` / `done` as
                // `echo donedone`.
                _swallowNextNewline = false;

                // REP repeats whatever this run ended with, so the character is captured from the
                // charset-mapped text that is actually painted — DEC special graphics included,
                // which is the case ncurses uses `rep` for most (a box-drawing rule is one mapped
                // character plus a repeat count).
                _lastGraphicChar = TrailingRepeatableChar(_textBuffer);

                _buffer.WriteContent(_textBuffer.ToString());
                _textBuffer.Clear();
            }
        }

        /// <summary>
        /// The last character of a printable run when it is one REP can repeat on its own, else
        /// <c>null</c>. See <see cref="_lastGraphicChar"/> for why the answer is fail-closed.
        /// </summary>
        private static char? TrailingRepeatableChar(System.Text.StringBuilder text)
        {
            if (text.Length == 0)
            {
                return null;
            }

            char last = text[text.Length - 1];

            // A surrogate is half a scalar and a combining mark belongs to the character in front
            // of it; repeating either alone produces something the stream never asked for.
            if (char.IsSurrogate(last))
            {
                return null;
            }

            return CharUnicodeInfo.GetUnicodeCategory(last) switch
            {
                UnicodeCategory.NonSpacingMark or
                UnicodeCategory.SpacingCombiningMark or
                UnicodeCategory.EnclosingMark => null,
                _ => last,
            };
        }

        /// <summary>
        /// Executes a C0 control (or DEL). Shared by the Normal state and the CSI state:
        /// per ECMA-48 §5.4, C0 controls received inside a control sequence are executed
        /// while the sequence continues (#169). Callers in text-accumulating states must
        /// FlushText() first.
        /// </summary>
        private void ExecuteC0Control(char c)
        {
            // A control is not a graphic character, so nothing survives it for REP to repeat.
            // Bell included: ECMA-48 draws the line at "graphic", not at "moved the cursor".
            _lastGraphicChar = null;

            if (c == '\a')
            {
                OnBell?.Invoke();
            }
            else if (c == '\x0e') // SO — shift GL to G1
            {
                _gl = 1;
            }
            else if (c == '\x0f') // SI — shift GL to G0
            {
                _gl = 0;
            }
            else
            {
                if (_swallowNextNewline)
                {
                    if (c == '\r' || c == '\n')
                    {
                        if (c == '\n') _swallowNextNewline = false;
                        return; // swallowed
                    }
                    _swallowNextNewline = false;
                }
                _buffer.WriteChar(c);
            }
        }

        // DEC Special Graphics: maps 0x5F..0x7E to box-drawing/symbol characters.
        // Anything outside that range passes through unchanged.
        private static char MapDecSpecialGraphics(char c) => c switch
        {
            '_' => ' ',
            '`' => '◆',
            'a' => '▒',
            'b' => '␉',
            'c' => '␌',
            'd' => '␍',
            'e' => '␊',
            'f' => '°',
            'g' => '±',
            'h' => '␤',
            'i' => '␋',
            'j' => '┘',
            'k' => '┐',
            'l' => '┌',
            'm' => '└',
            'n' => '┼',
            'o' => '⎺',
            'p' => '⎻',
            'q' => '─',
            'r' => '⎼',
            's' => '⎽',
            't' => '├',
            'u' => '┤',
            'v' => '┴',
            'w' => '┬',
            'x' => '│',
            'y' => '≤',
            'z' => '≥',
            '{' => 'π',
            '|' => '≠',
            '}' => '£',
            '~' => '·',
            _ => c,
        };

        private void BeginCsi()
        {
            _state = State.Csi;
            _paramLen = 0;
            _csiTruncated = false;
        }

        /// <summary>
        /// The finished sequence as a string, built straight from the list's storage (no ToArray
        /// copy), leaving <paramref name="buffer"/> empty and, if it grew large, freshly allocated.
        /// </summary>
        private static string TakeSequence(ref List<char> buffer)
        {
            string sequence = new string(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(buffer));
            if (buffer.Capacity > RetainedStringBufferChars) buffer = new List<char>();
            else buffer.Clear();
            return sequence;
        }

        private void ResetKittyPayloadBuffer()
        {
            if (_kittyPayloadBuffer.Capacity > RetainedStringBufferChars) _kittyPayloadBuffer = new System.Text.StringBuilder();
            else _kittyPayloadBuffer.Clear();
        }

        private void BeginOsc()
        {
            _state = State.Osc;
            _oscStringBuffer.Clear();
        }

        private void BeginDcs()
        {
            _state = State.Dcs;
            _dcsStringBuffer.Clear();
        }

        private void BeginApc()
        {
            _state = State.Apc;
            _apcStringBuffer.Clear();
        }

        private bool TryHandle7BitC1(char c)
        {
            switch (c)
            {
                case '[':
                    BeginCsi();
                    return true;
                case ']':
                    BeginOsc();
                    return true;
                case 'P':
                    BeginDcs();
                    return true;
                case '_':
                    BeginApc();
                    return true;
                case 'D': // IND
                    Index();
                    _state = State.Normal;
                    return true;
                case 'E': // NEL
                    _buffer.CursorCol = 0;
                    Index();
                    _state = State.Normal;
                    return true;
                case 'M': // RI
                    ReverseIndex();
                    _state = State.Normal;
                    return true;
                case 'H': // HTS
                    _buffer.SetTabStopAtCursor();
                    _state = State.Normal;
                    return true;
                case '\\': // ST outside a string - ignore
                    _state = State.Normal;
                    return true;
            }

            if (c >= '@' && c <= '_')
            {
                // Unsupported 7-bit C1 control: ignore rather than leaking a printable byte.
                _state = State.Normal;
                return true;
            }

            return false;
        }

        public void Process(string input)
        {
            if (string.IsNullOrEmpty(input)) return;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            _sawCursorHideInBatch = false;
            _sawCursorShowAfterHideInBatch = false;



            _buffer.EnterBatchWrite();
            try
            {
                for (int i = 0; i < input.Length; i++)
                {
                    char c = input[i];
                    bool reprocessCurrent;
                    do
                    {
                        reprocessCurrent = false;

                        switch (_state)
                        {
                            case State.Normal:
                                if (c == '\x1b')
                                {
                                    FlushText();
                                    _state = State.Esc;
                                }
                                else if (c >= 0x80 && c <= 0x9F) // C1 Controls
                                {
                                    FlushText();
                                    switch (c)
                                    {
                                        case '\u009B':
                                            BeginCsi();
                                            break;
                                        case '\u0090':
                                            BeginDcs();
                                            break;
                                        case '\u009D':
                                            BeginOsc();
                                            break;
                                        case '\u009F':
                                            BeginApc();
                                            break;
                                        case '\u009C':
                                            _state = State.Normal;
                                            break;
                                        default:
                                            _buffer.WriteChar(c);
                                            break;
                                    }
                                }
                                else if (c < 0x20 || c == 0x7F) // C0 Controls & DEL
                                {
                                    FlushText();
                                    ExecuteC0Control(c);
                                }
                                else
                                {
                                    // Printable Characters — translate via active GL charset.
                                    _textBuffer.Append(_charsets[_gl] == Charset.DecSpecialGraphics ? MapDecSpecialGraphics(c) : c);
                                }
                                break;

                            case State.Esc:
                                if (TryHandle7BitC1(c))
                                {
                                    break;
                                }

                                if (c == '7') // Save Cursor
                                {
                                    SaveCursor();
                                    _state = State.Normal;
                                }
                                else if (c == '8') // Restore Cursor
                                {
                                    RestoreCursor();
                                    _state = State.Normal;
                                }
                                else if (c == 'c') // RIS - Reset to Initial State
                                {
                                    _buffer.Reset();
                                    OnKittyClipboardReset?.Invoke();
                                    ResetCharsets();
                                    _verticalOffset = 0;
                                    // Parser-local mode state does not live in the buffer, so RIS
                                    // must clear it here too: a reset client that never re-enabled
                                    // mode 2048 must not keep receiving resize reports on its
                                    // stdin, and DECRQM must not report the mode as set.
                                    _inBandResizeReportsEnabled = false;
                                    _colorSchemeReportsEnabled = false;
                                    RefreshColorScheme(notify: false);
                                    _state = State.Normal;
                                }
                                else if (c == '=' || c == '>') // DECKPAM / DECKPNM
                                {
                                    // Keypad application/numeric mode does not render visible text.
                                    _state = State.Normal;
                                }
                                else if (c == '(' || c == ')' || c == '*' || c == '+' || c == '-')
                                {
                                    // G0..G3 charset designation. '(' '-' designate G0,
                                    // ')' G1, '*' G2, '+' G3. Capture slot, then consume the
                                    // designator char on the next byte.
                                    _pendingCharsetSlot = c switch
                                    {
                                        '(' => 0,
                                        '-' => 0,
                                        ')' => 1,
                                        '*' => 2,
                                        '+' => 3,
                                        _ => 0,
                                    };
                                    _state = State.Charset;
                                }
                                else if (c == '#')
                                {
                                    _state = State.EscHash;
                                }
                                else
                                {
                                    // Unknown escape sequence: ignore the introducer and current byte.
                                    _state = State.Normal;
                                }
                                break;

                            case State.Charset:
                                // The designator chooses the table loaded into the slot captured in State.Esc.
                                // '0' = DEC Special Graphics; everything else (including 'B' = US-ASCII and
                                // unsupported designators like 'A'/'1'/'2') falls back to ASCII pass-through.
                                if (_pendingCharsetSlot >= 0 && _pendingCharsetSlot < _charsets.Length)
                                {
                                    _charsets[_pendingCharsetSlot] = c == '0' ? Charset.DecSpecialGraphics : Charset.Ascii;
                                }
                                _pendingCharsetSlot = -1;
                                _state = State.Normal;
                                break;

                            case State.EscHash:
                                if (c == '8') // DECALN - Screen Alignment Pattern
                                {
                                    _buffer.ScreenAlignmentPattern();
                                }
                                // 3, 4, 5, 6 are for single/double width/height lines.
                                // We ignore them for now as we don't support per-line rendering attributes yet.
                                _state = State.Normal;
                                break;

                            case State.Osc:
                                if (c == '\a' || c == '\u009C')
                                {
                                    HandleOsc(TakeSequence(ref _oscStringBuffer));
                                    _state = State.Normal;
                                }
                                else if (c == '\x1b')
                                {
                                    _state = State.OscEsc;
                                }
                                else
                                {
                                    if (_oscStringBuffer.Count < MaxStringSequenceChars)
                                        _oscStringBuffer.Add(c);
                                }
                                break;

                            case State.OscEsc:
                                if (c == '\\')
                                {
                                    HandleOsc(TakeSequence(ref _oscStringBuffer));
                                    _state = State.Normal;
                                }
                                else
                                {
                                    // Malformed OSC ESC sequence: abandon the string and treat this
                                    // byte as the start of a fresh ESC sequence continuation.
                                    _state = State.Esc;
                                    reprocessCurrent = true;
                                }
                                break;

                            case State.Dcs:
                                if (c == '\x1b')
                                {
                                    _state = State.DcsEsc;
                                }
                                else if (c == '\a' || c == '\u009C')
                                {
                                    HandleDcs(TakeSequence(ref _dcsStringBuffer));
                                    _state = State.Normal;
                                }
                                else
                                {
                                    if (_dcsStringBuffer.Count < MaxStringSequenceChars)
                                        _dcsStringBuffer.Add(c);
                                }
                                break;

                            case State.DcsEsc:
                                if (c == '\\')
                                {
                                    HandleDcs(TakeSequence(ref _dcsStringBuffer));
                                    _state = State.Normal;
                                }
                                else
                                {
                                    _state = State.Esc;
                                    reprocessCurrent = true;
                                }
                                break;

                            case State.Apc:
                                if (c == '\x1b')
                                {
                                    _state = State.ApcEsc;
                                }
                                else if (c == '\a' || c == '\u009C')
                                {
                                    HandleApc(TakeSequence(ref _apcStringBuffer));
                                    _state = State.Normal;
                                }
                                else
                                {
                                    if (_apcStringBuffer.Count < MaxStringSequenceChars)
                                        _apcStringBuffer.Add(c);
                                }
                                break;

                            case State.ApcEsc:
                                if (c == '\\')
                                {
                                    HandleApc(TakeSequence(ref _apcStringBuffer));
                                    _state = State.Normal;
                                }
                                else
                                {
                                    _state = State.Esc;
                                    reprocessCurrent = true;
                                }
                                break;

                            case State.Csi:
                                if (c == '\x1b')
                                {
                                    // Abort malformed CSI and treat ESC as a fresh introducer.
                                    _paramLen = 0;
                                    _state = State.Esc;
                                }
                                else if (c >= 0x20 && c <= 0x3F)
                                {
                                    // Collect params (grow up to a hard safety cap).
                                    if (_paramLen < MaxCsiParamChars && EnsureCsiParamCapacity(_paramLen + 1))
                                    {
                                        _paramBuffer[_paramLen++] = c;
                                    }
                                    else
                                    {
                                        _csiTruncated = true;
                                    }
                                }
                                else if (c >= 0x40 && c <= 0x7E)
                                {
                                    // A sequence we could not read in full is discarded rather
                                    // than acted on: we do not know what it was. The bytes past
                                    // the cap are exactly where an intermediate would sit, so
                                    // dispatching the prefix runs the BARE meaning of a final
                                    // byte that was never bare - CSI <cap of digits> SP A is SR,
                                    // and used to execute CUU. Truncation is already pathological
                                    // input at 64 KiB of parameters; guessing at it is worse than
                                    // dropping it.
                                    if (_csiTruncated)
                                    {
                                        TerminalLogger.Debug(
                                            $"[ANSI_PARSER] Discarded a CSI truncated at {MaxCsiParamChars} parameter bytes (final byte '{c}').");
                                    }
                                    else
                                    {
                                        HandleCsi(c, _paramBuffer.AsSpan(0, _paramLen));
                                    }

                                    _paramLen = 0;
                                    _csiTruncated = false;
                                    _state = State.Normal;
                                }
                                else if (c == '\x18' || c == '\x1a')
                                {
                                    // CAN/SUB abort the control sequence (ECMA-48).
                                    _paramLen = 0;
                                    _state = State.Normal;
                                }
                                else if (c < 0x20)
                                {
                                    // ECMA-48 §5.4: C0 controls received inside a control
                                    // sequence are executed and the sequence continues.
                                    // ConPTY/tmux can split output so CR/LF land mid-CSI;
                                    // previously both the control AND the sequence were
                                    // dropped (#169).
                                    ExecuteC0Control(c);
                                }
                                else if (c == '\x7f')
                                {
                                    // DEL is ignored inside control sequences (xterm).
                                }
                                else
                                {
                                    // Malformed/non-final CSI byte: drop the sequence.
                                    _paramLen = 0;
                                    _state = State.Normal;
                                }
                                break;
                        }
                    } while (reprocessCurrent);
                }
                FlushText();

                if (_sawCursorShowAfterHideInBatch)
                {
                    _buffer.ExtendCursorSuppression_NoLock(CursorTransientSuppressionWindow);
                    _buffer.Invalidate();
                }
            }
            finally
            {
                _buffer.ExitBatchWrite();
                sw.Stop();
            }
        }

        private bool EnsureCsiParamCapacity(int required)
        {
            if (required <= _paramBuffer.Length) return true;
            if (_paramBuffer.Length >= MaxCsiParamChars) return false;

            int next = _paramBuffer.Length;
            while (next < required && next < MaxCsiParamChars)
            {
                next *= 2;
            }

            next = Math.Min(next, MaxCsiParamChars);
            if (next < required) return false;

            Array.Resize(ref _paramBuffer, next);
            return true;
        }

        private void Index()
        {
            if (_buffer.CursorRow == _buffer.ScrollBottom)
            {
                _buffer.ScrollUp();
                return;
            }

            _buffer.CursorRow = Math.Min(_buffer.CursorRow + 1, _buffer.Rows - 1);
        }

        private void ReverseIndex()
        {
            if (_buffer.CursorRow == _buffer.ScrollTop)
            {
                _buffer.ScrollDown();
                return;
            }

            _buffer.CursorRow = Math.Max(_buffer.CursorRow - 1, 0);
        }
    }
}
