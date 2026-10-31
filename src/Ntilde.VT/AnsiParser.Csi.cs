using System.Buffers;
using System.Reflection;

namespace Ntilde.VT
{
    /// <summary>
    /// Control sequences (CSI): decoding the parameters and executing each final byte, with the cursor, report
    /// and mode helpers they use, DECRQM, DEC private modes and the kitty keyboard protocol.
    /// </summary>
    public partial class AnsiParser
    {
        /// <summary>
        /// The XTVERSION reply, <c>DCS &gt; | Ntilde &lt;version&gt; ST</c>. The name matches the
        /// <c>TERM_PROGRAM</c> the PTY layer exports. The version is this assembly's, which the build
        /// pins for every project in the solution (and a release overrides for all of them), minus any
        /// <c>+</c> build metadata.
        /// </summary>
        internal static readonly string XtVersionReply = $"\x1bP>|Ntilde {TerminalVersion()}\x1b\\";

        private static string TerminalVersion()
        {
            string? version = typeof(AnsiParser).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(AnsiParser).Assembly.GetName().Version?.ToString();
            return string.IsNullOrWhiteSpace(version) ? "0.0.0" : version.Split('+')[0].Trim();
        }

        private void HandleCsi(char finalByte, ReadOnlySpan<char> parameters)
        {
            // CSI format: CSI [leader] [params] [intermediates] final
            // leader: '<' '=' '>' '?'
            // params: digits + ';' + ':'
            // intermediates: 0x20..0x2F
            char leader = '\0';
            ReadOnlySpan<char> csiBody = parameters;
            if (csiBody.Length > 0)
            {
                char p0 = csiBody[0];
                if (p0 == '<' || p0 == '=' || p0 == '>' || p0 == '?')
                {
                    leader = p0;
                    csiBody = csiBody.Slice(1);
                }
            }

            int firstIntermediate = -1;
            for (int i = 0; i < csiBody.Length; i++)
            {
                char c = csiBody[i];
                if (c >= '\x20' && c <= '\x2F')
                {
                    firstIntermediate = i;
                    break;
                }
            }

            ReadOnlySpan<char> paramPart = firstIntermediate >= 0 ? csiBody.Slice(0, firstIntermediate) : csiBody;
            ReadOnlySpan<char> intermediates = firstIntermediate >= 0 ? csiBody.Slice(firstIntermediate) : ReadOnlySpan<char>.Empty;

            // A private-parameter byte (0x3C-0x3F) is only meaningful as the leader, and once
            // intermediates have begun no parameter byte may follow. The VT500 state machine
            // sends both cases to csi_ignore, and so do we: the sequence is discarded, not
            // parsed leniently.
            //
            // Leniency here would walk straight past the identity guards below. CSI 1 ? 2 A
            // has no leader by the rule above, so it used to reach the parameter loop, which
            // swallowed the '?' as a hard separator and executed a bare CUU - a malformed
            // private sequence running the bare meaning of a final byte, which is exactly what
            // those guards exist to stop.
            foreach (char pc in paramPart)
            {
                if (pc >= '\x3C' && pc <= '\x3F')
                {
                    TerminalLogger.Debug($"[ANSI_PARSER] Discarded CSI with a misplaced private-parameter byte: final '{finalByte}', params '{new string(parameters)}'.");
                    return;
                }
            }

            foreach (char ic in intermediates)
            {
                if (ic >= '\x30' && ic <= '\x3F')
                {
                    TerminalLogger.Debug($"[ANSI_PARSER] Discarded CSI with a parameter byte after an intermediate: final '{finalByte}', params '{new string(parameters)}'.");
                    return;
                }
            }

            int[]? rentedArgs = null;
            char[]? rentedSeparators = null;
            Span<int> args;
            Span<char> separators;

            // Reserve enough room for all parameters in this CSI so trailing reset
            // codes (for example 24 = no underline) are never dropped.
            int estimatedArgs = 1;
            for (int i = 0; i < paramPart.Length; i++)
            {
                char c = paramPart[i];
                if (c == ';' || c == ':') estimatedArgs++;
            }
            estimatedArgs = Math.Clamp(estimatedArgs, 1, _paramBuffer.Length + 1);

            rentedArgs = ArrayPool<int>.Shared.Rent(estimatedArgs);
            rentedSeparators = ArrayPool<char>.Shared.Rent(estimatedArgs);
            args = rentedArgs.AsSpan(0, estimatedArgs);
            separators = rentedSeparators.AsSpan(0, estimatedArgs);

            int argCount = 0;
            int currentVal = 0;
            bool hasVal = false;

            for (int i = 0; i < paramPart.Length; i++)
            {
                char c = paramPart[i];
                if (c >= '0' && c <= '9')
                {
                    // Clamp during accumulation: bounds DoS-prone counts (scroll/repeat/insert)
                    // and avoids int overflow on absurdly long digit runs.
                    if (currentVal < MaxCsiParamValue)
                    {
                        currentVal = Math.Min(MaxCsiParamValue, (currentVal * 10) + (c - '0'));
                    }
                    hasVal = true;
                }
                else if (c == ';' || c == ':')
                {
                    if (argCount < args.Length)
                    {
                        args[argCount++] = hasVal ? currentVal : 0;
                        separators[argCount - 1] = c;
                    }
                    currentVal = 0;
                    hasVal = false;
                }
                else
                {
                    // Ignore unexpected chars in params; treat them as hard separators.
                    if (hasVal && argCount < args.Length)
                    {
                        args[argCount++] = currentVal;
                        separators[argCount - 1] = '\0';
                    }
                    currentVal = 0;
                    hasVal = false;
                }
            }
            // Add final arg
            bool paramEndsWithSeparator = paramPart.Length > 0 &&
                                          (paramPart[paramPart.Length - 1] == ';' || paramPart[paramPart.Length - 1] == ':');
            if (hasVal || paramEndsWithSeparator)
            {
                if (argCount < args.Length)
                {
                    args[argCount++] = hasVal ? currentVal : 0;
                    separators[argCount - 1] = '\0';
                }
            }

            // Slice to actual count
            ReadOnlySpan<int> validArgs = args.Slice(0, argCount);
            ReadOnlySpan<char> validSeparators = separators.Slice(0, argCount);

            // Every control sequence ends the run of graphic characters REP could repeat, so the
            // pending character is consumed here rather than in each of ExecuteCsi's forty-odd cases.
            // REP is the one that wants it, and it puts it back — `CSI 5 b CSI 5 b` is two repeats
            // of the same character, not a repeat followed by a no-op.
            char? repeatable = _lastGraphicChar;
            _lastGraphicChar = null;

            try
            {
                ExecuteCsi(
                    new CsiSequence(finalByte, leader, intermediates, validArgs, validSeparators, parameters),
                    repeatable);
            }
            finally
            {
                if (rentedArgs != null) ArrayPool<int>.Shared.Return(rentedArgs);
                if (rentedSeparators != null) ArrayPool<char>.Shared.Return(rentedSeparators);
            }
        }

        /// <summary>
        /// Executes a decoded control sequence: the dispatch on its final byte. A final byte is read together
        /// with its leader and intermediates; see <see cref="CsiSequence.Bare"/>.
        /// </summary>
        private void ExecuteCsi(in CsiSequence csi, char? repeatable)
        {
            char finalByte = csi.Final;
            char leader = csi.Leader;
            ReadOnlySpan<char> intermediates = csi.Intermediates;
            ReadOnlySpan<int> validArgs = csi.Args;
            ReadOnlySpan<char> validSeparators = csi.Separators;
            ReadOnlySpan<char> parameters = csi.Raw;
            int argCount = validArgs.Length;
            int arg0 = csi.Arg0;
            bool bare = csi.Bare;
            bool isPrivate = csi.IsPrivate;

            switch (finalByte)
            {
                case 'A': // Cursor Up
                          // CSI Pn SP A is SR (scroll right), not CUU.
                    if (bare)
                    {
                        MoveCursorUp(Math.Max(1, arg0), toFirstColumn: false);
                    }
                    break;
                case 'F': // Cursor Preceding Line
                    if (leader == '\0' && intermediates.Length == 0)
                    {
                        MoveCursorUp(Math.Max(1, arg0), toFirstColumn: true);
                    }
                    break;
                case 'B': // Cursor Down
                          // No prefixed form is defined; only the bare spelling is CUD.
                    if (bare)
                    {
                        MoveCursorDown(Math.Max(1, arg0), toFirstColumn: false);
                    }
                    break;
                case 'E': // Cursor Next Line
                    if (leader == '\0' && intermediates.Length == 0)
                    {
                        MoveCursorDown(Math.Max(1, arg0), toFirstColumn: true);
                    }
                    break;
                case 'r': // DECSTBM - Set Scrolling Region
                          // Ignore leader-prefixed "...r" sequences, e.g. CSI ? Pm r (XTRESTORE -
                          // restore DEC private mode values). They are NOT DECSTBM.
                    if (leader == '\0' && intermediates.Length == 0)
                    {
                        int regionTop = (argCount > 0 && validArgs[0] > 0 ? validArgs[0] : 1) - 1;
                        int regionBottom = (argCount > 1 && validArgs[1] > 0 ? validArgs[1] : _buffer.Rows) - 1;
                        _buffer.SetScrollingRegion(regionTop, regionBottom);
                        // Cursor moves to home after setting region.
                        // In DECOM, home is the top of the scrolling region.
                        int homeRow = _buffer.Modes.IsOriginMode ? _buffer.ScrollTop : 0;
                        _buffer.SetCursorPosition(0, homeRow);
                    }
                    break;
                case '@': // ICH - Insert Character
                          // CSI Pn SP @ is SL (scroll left), not ICH.
                    if (bare)
                    {
                        int ichCount = Math.Max(1, arg0);
                        _buffer.InsertCharacters(ichCount);
                    }
                    break;
                case 'I': // CHT - Cursor Forward Tabulation
                          // Was guarded on !isPrivate alone, so '>' '<' '=' still reached it.
                    if (bare)
                    {
                        _buffer.HorizontalTab(GetCsiParamOrDefaultOne(validArgs, 0));
                        _buffer.Invalidate();
                    }
                    break;
                case 'b': // REP - Repeat the preceding graphic character (ECMA-48 §8.3.103).
                          //
                          // Not optional in practice: we advertise TERM=xterm-256color, whose
                          // terminfo declares `rep=%p1%c\E[%p2%{1}%-%db`, so every ncurses
                          // program on the far end of an SSH session uses this to draw runs —
                          // borders, rules, padding, meter bars. While it was unimplemented
                          // those runs rendered as a single character, and each one also cost
                          // an "Unhandled CSI" line in the debug log.
                    if (bare && repeatable is char toRepeat)
                    {
                        RepeatGraphic(toRepeat, arg0);
                    }
                    break;
                case 'P': // DCH - Delete Character
                          // CSI Pn SP P is PPA (page position absolute), not DCH.
                    if (bare)
                    {
                        int dchCount = Math.Max(1, arg0);
                        _buffer.DeleteCharacters(dchCount);
                    }
                    break;
                case 'S': // SU - Scroll Up
                          // CSI ? Pi ; Pa ; Pv S is XTSMGRAPHICS, not SU. This one is live
                          // rather than theoretical: the DA1 reply below advertises sixel
                          // (CSI ?62;4;22c), so sixel-capable clients probe XTSMGRAPHICS as a
                          // matter of course - and every probe used to scroll the screen.
                    if (bare)
                    {
                        int suCount = Math.Max(1, arg0);
                        for (int i = 0; i < suCount; i++) _buffer.ScrollUp();
                    }
                    break;
                case 'T': // SD - Scroll Down
                          // CSI > Ps T is XTRMTITLE, not SD.
                          //
                          // The five-parameter bare form is xterm's initiate-highlight-mouse-
                          // tracking, also not SD. It is not implemented, but it must not
                          // scroll: SD takes a single parameter, so a longer parameter list is
                          // by itself proof that this is the other sequence.
                    if (bare && argCount <= 1)
                    {
                        int sdCount = Math.Max(1, arg0);
                        for (int i = 0; i < sdCount; i++) _buffer.ScrollDown();
                    }
                    break;
                case 'd': // VPA - Vertical Position Absolute
                          // No prefixed form is defined; only the bare spelling is VPA.
                    if (bare)
                    {
                        int vpaRow = GetCsiParamOrDefaultOne(validArgs, 0) - 1;
                        if (_buffer.Modes.IsOriginMode) vpaRow += _buffer.ScrollTop;
                        // NOTE: _verticalOffset intentionally NOT applied here — it's a ConPTY
                        // image-rendering artifact that must not affect general cursor positioning.
                        _buffer.CursorRow = ClampRowForMode(vpaRow);
                        _buffer.Invalidate();
                    }
                    break;
                case 'C': // Cursor Forward
                case 'a': // HPR - Horizontal Position Relative
                    if (bare)
                    {
                        _buffer.CursorCol = Math.Min(_buffer.Cols - 1, _buffer.CursorCol + GetCsiParamOrDefaultOne(validArgs, 0));
                        _buffer.Invalidate();
                    }
                    break;
                case 'D': // Cursor Back
                    if (bare)
                    {
                        _buffer.CursorCol = Math.Max(0, _buffer.CursorCol - Math.Max(1, arg0));
                        _buffer.Invalidate();
                    }
                    break;
                case 'Z': // CBT - Cursor Backward Tabulation
                          // Was guarded on !isPrivate alone, so '>' '<' '=' still reached it.
                    if (bare)
                    {
                        _buffer.BackwardTab(GetCsiParamOrDefaultOne(validArgs, 0));
                        _buffer.Invalidate();
                    }
                    break;
                case 'H': // Cursor Position (row;col)
                case 'f':
                    if (bare)
                    {
                        int row = GetCsiParamOrDefaultOne(validArgs, 0) - 1;
                        int col = GetCsiParamOrDefaultOne(validArgs, 1) - 1;

                        if (_buffer.Modes.IsOriginMode)
                        {
                            row += _buffer.ScrollTop;
                        }

                        // NOTE: _verticalOffset intentionally NOT applied here — it is a ConPTY
                        // image-rendering artifact that must not displace general TUI cursor positions.

                        row = ClampRowForMode(row);
                        _buffer.SetCursorPosition(col, row);
                        _buffer.Invalidate();
                    }
                    break;
                case 'G': // Cursor Horizontal Absolute (CHA)
                case '`': // HPA - Horizontal Position Absolute
                    if (bare)
                    {
                        int val = GetCsiParamOrDefaultOne(validArgs, 0) - 1;
                        _buffer.CursorCol = Math.Clamp(val, 0, _buffer.Cols - 1);
                        _buffer.Invalidate();
                    }
                    break;
                case 'e': // VPR - Vertical Position Relative
                          // No prefixed form is defined; only the bare spelling is VPR.
                    if (bare)
                    {
                        MoveCursorDown(GetCsiParamOrDefaultOne(validArgs, 0), toFirstColumn: false);
                    }
                    break;
                case 'g': // TBC - Tab Clear
                          // Was guarded on !isPrivate alone, so '>' '<' '=' still reached it.
                    if (bare)
                    {
                        if (argCount > 0 && validArgs[0] == 3)
                        {
                            _buffer.ClearAllTabStops();
                        }
                        else
                        {
                            _buffer.ClearTabStopAtCursor();
                        }

                        _buffer.Invalidate();
                    }
                    break;
                case 'J': // Erase in Display
                          // CSI ? Ps J is DECSED, which erases only UNPROTECTED characters.
                          // DECSCA (CSI Ps " q), the sequence that marks characters as
                          // protected, is not implemented, so every character in the buffer is
                          // unprotected and DECSED is exactly ED. The alias is therefore
                          // correct today rather than merely convenient - and refusing the
                          // private form instead would leave a client's screen uncleared,
                          // which is a worse outcome than the nonconformance. If DECSCA is
                          // ever implemented, this case has to split.
                    if (intermediates.Length == 0 && (leader == '\0' || leader == '?'))
                    {
                        EraseInDisplay(argCount > 0 ? validArgs[0] : 0);
                    }
                    break;
                case 'K': // Erase in Line
                          // CSI ? Ps K is DECSEL. Aliased to EL for the same reason as DECSED
                          // above: with DECSCA unimplemented, nothing is protected.
                    if (intermediates.Length == 0 && (leader == '\0' || leader == '?'))
                    {
                        int mode = argCount > 0 ? validArgs[0] : 0;

                        if (mode == 0) _buffer.EraseLineToEnd();
                        else if (mode == 1) _buffer.EraseLineFromStart();
                        else if (mode == 2) _buffer.EraseLineAll();
                    }
                    break;
                case 'X': // Erase Character (ECH)
                    if (bare)
                    {
                        int count = argCount > 0 ? validArgs[0] : 1;
                        _buffer.EraseCharacters(count);
                    }
                    break;
                case 'L': // Insert Line (IL)
                    if (bare)
                    {
                        int linesToInsert = argCount > 0 ? validArgs[0] : 1;
                        _buffer.InsertLines(linesToInsert);
                    }
                    break;
                case 'M': // Delete Line (DL)
                    if (bare)
                    {
                        int linesToDelete = argCount > 0 ? validArgs[0] : 1;
                        _buffer.DeleteLines(linesToDelete);
                    }
                    break;
                case 's': // Save Cursor (ANSI.SYS / SCO)
                          // Ignore leader-prefixed "...s" sequences, e.g. CSI ? Pm s (XTSAVE -
                          // save DEC private mode values). They are NOT SCO save-cursor.
                    if (leader == '\0' && intermediates.Length == 0)
                    {
                        SaveCursor();
                    }
                    break;
                case 'u': // Restore Cursor (ANSI.SYS / SCO) or kitty keyboard protocol
                          // Only the bare form is SCO restore-cursor. The leader-prefixed
                          // forms belong to the kitty keyboard protocol and must never move
                          // the cursor: CSI ? u (query), CSI > Pm u (push), CSI < Pm u (pop),
                          // CSI = Pm ; Pm u (set).
                    if (intermediates.Length == 0)
                    {
                        if (leader == '\0')
                        {
                            RestoreCursor();
                        }
                        else
                        {
                            HandleKittyKeyboardProtocol(leader, validArgs);
                        }
                    }
                    break;
                case 'm': // SGR (Select Graphic Rendition)
                          // Ignore non-standard leader-prefixed "...m" control sequences, e.g.
                          // CSI > Pp ; Pv m (xterm key-modifier options). They are NOT SGR.
                    if (leader == '\0' && intermediates.Length == 0)
                    {
                        HandleSgr(validArgs, validSeparators);
                    }
                    break;
                case 'h': // Set Mode
                case 'l': // Reset Mode
                    bool enableMode = (finalByte == 'h');
                    // Only two forms are defined: CSI Ps h/l (ANSI modes) and CSI ? Ps h/l
                    // (DEC private modes). The '<', '=' and '>' leaders used to fall into the
                    // else-branch and be applied as ANSI modes, so CSI > 4 h silently turned
                    // on insert mode.
                    if (intermediates.Length == 0 && isPrivate)
                    {
                        HandleDECPrivateMode(validArgs, enableMode);
                    }
                    else if (bare)
                    {
                        SetAnsiModes(validArgs, enableMode);
                    }
                    break;
                case 'c': // DA - Device Attributes
                          // CSI c is DA1 and CSI > c is DA2. CSI = c is DA3, which expects a
                          // DECRPTUI unit-id report rather than a DA1 one; it is unimplemented
                          // and stays silent instead of answering a different question than
                          // the one that was asked. The !isPrivate test used to let both
                          // CSI = c and CSI < c through to the DA1 reply.
                    if (intermediates.Length == 0 && leader == '>')
                    {
                        // Secondary Device Attributes
                        // CSI > 1 ; 1 0 ; 0 c (standard for VT220-ish)
                        OnResponse?.Invoke("\x1b[>1;10;0c");
                    }
                    else if (bare)
                    {
                        // Primary Device Attributes (CSI c)
                        // Respond with VT100/VT102 capability to satisfy vttest
                        // ?1;2c (VT100 with AVO) is safest baseline, but we claim more.
                        // ?62;4;22c (VT220 + Sixel + ANSI)
                        // vttest checks these. 
                        // Let's stick to ?62;4;22c as it worked for XTerm.
                        OnResponse?.Invoke("\x1b[?62;4;22c");
                    }
                    break;
                case 'n': // DSR - Device Status Report
                          // CSI ? 6 n is DECXCPR, and it has to be answered in the private
                          // form CSI ? r ; c R. Replying with a plain CPR - which is what the
                          // unguarded arg0 test did - is not a partial answer but a malformed
                          // one: a client that asked the private question parses for a private
                          // response, so it either drops ours or mistakes it for an
                          // unsolicited CPR. DEC's page parameter is omitted, as xterm omits
                          // it.
                          //
                          // The other private reports (?15 printer, ?25 UDK, ?26 keyboard,
                          // ?53 locator) are unimplemented and stay silent, and CSI > Ps n
                          // (xterm's disable-key-modifiers) is not a status request at all.
                    if (intermediates.Length == 0 && (leader == '\0' || leader == '?'))
                    {
                        ReportDeviceStatus(arg0, isPrivate);
                    }
                    break;
                case 'q':
                    // DECSCUSR - Set Cursor Style (CSI Ps SP q). The intermediate has to be
                    // exactly one space. Accepting "any intermediate at all" meant that
                    // CSI Ps " q (DECSCA, character protection) set the cursor shape - the
                    // same class of mistake as the leader cases above, one column over.
                    if (leader == '\0' && intermediates.Length == 1 && intermediates[0] == ' ')
                    {
                        ApplyCursorStyle(argCount > 0 ? validArgs[0] : 0);
                    }
                    // XTVERSION - CSI > q / CSI > 0 q asks for the terminal's name and version,
                    // answered as DCS > | text ST. Tools log it, and some pick features by it.
                    // Other Ps values, and extra parameters, are not defined by xterm and stay silent.
                    else if (leader == '>' && intermediates.Length == 0 && argCount <= 1 && arg0 == 0)
                    {
                        OnResponse?.Invoke(XtVersionReply);
                    }
                    break;
                case 't': // Window operations (xterm). Two pixel-geometry reports are
                          // implemented, both consumed by clients like terminal-browser:
                          //   CSI 14 t -> CSI 4 ; paneHeightPx ; paneWidthPx t
                          //   CSI 16 t -> CSI 6 ; cellHeightPx ; cellWidthPx t
                          // CSI 16 t is the important one: without it the client assumes a
                          // hardcoded 16x32 px cell, renders its surface at the wrong size
                          // AND aspect, and every frame arrives squashed. The remaining
                          // window ops (stack, iconify, resize-by-cells) are ignored, and
                          // leader-prefixed variants stay unhandled.
                    if (leader == '\0' && intermediates.Length == 0 && (arg0 == 14 || arg0 == 16))
                    {
                        ReportWindowGeometry(arg0);
                    }
                    break;
                case 'p':
                    // DECRQM - Request Mode (Issue #267): CSI Ps $ p (ANSI) or
                    // CSI ? Ps $ p (DEC private) -> DECRPM reply CSI [?] Ps ; Pm $ y.
                    // Key on the intermediate exactly - DECSTR and bare CSI p must
                    // not be captured as mode queries, the same way
                    // PR #273 guards CSI s/u/r against leader-prefixed variants. Only the
                    // bare and '?' leader forms are defined for DECRQM; '<', '=', '>'
                    // leaders (used by other CSI...p extensions) are left unhandled.
                    if (intermediates.Length == 1 && intermediates[0] == '$' &&
                        (leader == '\0' || leader == '?'))
                    {
                        HandleDecrqm(isPrivate, arg0);
                    }
                    else if (leader == '\0' && validArgs.Length == 0 && intermediates.SequenceEqual("!"))
                    {
                        SoftReset();
                    }
                    break;
                default:
                    TerminalLogger.Debug($"[ANSI_PARSER] Unhandled CSI: {finalByte} (private={isPrivate}), params={new string(parameters)}");
                    break;
            }
        }

        /// <summary>
        /// CUU and CPL: the cursor rises <paramref name="dist"/> rows, stopping at the top margin when it starts
        /// inside the scrolling region and at the first row otherwise. CPL also returns to the first column.
        /// </summary>
        private void MoveCursorUp(int dist, bool toFirstColumn)
        {
            if (_buffer.CursorRow >= _buffer.ScrollTop && _buffer.CursorRow <= _buffer.ScrollBottom)
                _buffer.CursorRow = Math.Max(_buffer.ScrollTop, _buffer.CursorRow - dist);
            else
                _buffer.CursorRow = Math.Max(0, _buffer.CursorRow - dist);
            if (toFirstColumn)
                _buffer.CursorCol = 0;
            _buffer.Invalidate();
        }

        /// <summary>
        /// CUD, CNL and VPR: the cursor descends <paramref name="dist"/> rows, stopping at the bottom margin when it
        /// starts inside the scrolling region and at the last row otherwise. CNL also returns to the first column.
        /// </summary>
        private void MoveCursorDown(int dist, bool toFirstColumn)
        {
            if (_buffer.CursorRow >= _buffer.ScrollTop && _buffer.CursorRow <= _buffer.ScrollBottom)
                _buffer.CursorRow = Math.Min(_buffer.ScrollBottom, _buffer.CursorRow + dist);
            else
                _buffer.CursorRow = Math.Min(_buffer.Rows - 1, _buffer.CursorRow + dist);
            if (toFirstColumn)
                _buffer.CursorCol = 0;
            _buffer.Invalidate();
        }

        /// <summary>REP: writes the preceding graphic character again.</summary>
        private void RepeatGraphic(char toRepeat, int arg0)
        {
            // Clamped to one line's worth. A repeat count is an amplification
            // factor — five bytes in, MaxCsiParamValue cells out — and no
            // producer needs more: ncurses only emits `rep` for a run it has
            // already decided fits the line. Same reasoning as the parameter
            // clamp itself, one level up.
            int repCount = Math.Min(Math.Max(1, arg0), _buffer.Cols);
            _buffer.WriteContent(new string(toRepeat, repCount));
            _lastGraphicChar = toRepeat;
        }

        /// <summary>
        /// ED (and DECSED): erases part of the screen, or the scrollback, as <paramref name="displayMode"/> says.
        /// </summary>
        private void EraseInDisplay(int displayMode)
        {
            if (displayMode == 0) // Erase from cursor to end of screen
            {
                _buffer.EraseLineToEnd(); // Clear rest of current line
                                          // Clear all lines below cursor
                for (int r = _buffer.CursorRow + 1; r < _buffer.Rows; r++)
                {
                    _buffer.EraseLineAll(r);
                }
            }
            else if (displayMode == 1) // Erase from start of screen to cursor
            {
                // Clear all lines above cursor
                for (int r = 0; r < _buffer.CursorRow; r++)
                {
                    _buffer.EraseLineAll(r);
                }
                _buffer.EraseLineFromStart(); // Clear start of current line
            }
            else if (displayMode == 2) // Erase entire screen (scrollback is preserved)
            {
                _buffer.ClearScreen(resetCursor: false);
                _verticalOffset = 0; // Reset offset on clear screen
            }
            else if (displayMode == 3) // Erase saved lines (scrollback only, xterm ED 3)
            {
                _buffer.ClearScrollbackHistory();
                _verticalOffset = 0; // Scrollback is gone; snap view to bottom
            }
        }

        /// <summary>SM / RM for the standard (ANSI) modes: IRM, SRM and LNM.</summary>
        private void SetAnsiModes(ReadOnlySpan<int> validArgs, bool enableMode)
        {
            foreach (int m in validArgs)
            {
                if (m == 4) // IRM - Insert Replacement Mode
                {
                    _buffer.Modes.IsInsertMode = enableMode;
                }
                else if (m == 12) // SRM - Send/Receive Mode (Echo)
                {
                    // SRM Reset (l) means Local Echo is ON.
                    // SRM Set (h) means Local Echo is OFF.
                    _buffer.Modes.IsEchoEnabled = !enableMode;
                }
                else if (m == 20) // LNM - Line Feed New Line Mode
                {
                    _buffer.Modes.IsLineFeedNewLineMode = enableMode;
                }
            }
        }

        /// <summary>
        /// DSR: the operating-status report (5) and the cursor position report (6), the latter in the private
        /// DECXCPR form when it was asked privately.
        /// </summary>
        private void ReportDeviceStatus(int arg0, bool isPrivate)
        {
            if (arg0 == 5 && !isPrivate) // DSR - operating status
            {
                OnResponse?.Invoke("\x1b[0n"); // OK
            }
            else if (arg0 == 6) // CPR (bare) / DECXCPR (private)
            {
                string cprLeader = isPrivate ? "?" : string.Empty;
                OnResponse?.Invoke($"\x1b[{cprLeader}{ReportedCursorRow()};{_buffer.CursorCol + 1}R");
            }

            // arg0 == 0 is a DSR *response* arriving on the input stream: ignored.
        }

        /// <summary>XTWINOPS 14 and 16: the pane's size and a cell's size, in pixels.</summary>
        private void ReportWindowGeometry(int arg0)
        {
            float cw = CellWidth > 0 ? CellWidth : 10f;
            float ch = CellHeight > 0 ? CellHeight : 20f;
            if (arg0 == 14)
            {
                int widthPx = Math.Max(1, (int)Math.Round(_buffer.Cols * cw));
                int heightPx = Math.Max(1, (int)Math.Round(_buffer.Rows * ch));
                OnResponse?.Invoke($"\x1b[4;{heightPx};{widthPx}t");
            }
            else
            {
                int cellWidthPx = Math.Max(1, (int)Math.Round(cw));
                int cellHeightPx = Math.Max(1, (int)Math.Round(ch));
                OnResponse?.Invoke($"\x1b[6;{cellHeightPx};{cellWidthPx}t");
            }
        }

        /// <summary>
        /// A control sequence once its parameters are decoded: the final byte together with the leader and
        /// intermediates that identify it, and its numeric parameters with the separator that followed each.
        /// The spans point into the caller's pooled buffers, which is why this is a ref struct.
        /// </summary>
        private readonly ref struct CsiSequence
        {
            public CsiSequence(
                char final,
                char leader,
                ReadOnlySpan<char> intermediates,
                ReadOnlySpan<int> args,
                ReadOnlySpan<char> separators,
                ReadOnlySpan<char> raw)
            {
                Final = final;
                Leader = leader;
                Intermediates = intermediates;
                Args = args;
                Separators = separators;
                Raw = raw;
            }

            public char Final { get; }

            /// <summary>'&lt;', '=', '&gt;' or '?', or '\0' when there is none.</summary>
            public char Leader { get; }

            /// <summary>The bytes 0x20-0x2F between the parameters and the final byte.</summary>
            public ReadOnlySpan<char> Intermediates { get; }

            /// <summary>The parameters, each clamped to MaxCsiParamValue; an omitted one is 0.</summary>
            public ReadOnlySpan<int> Args { get; }

            /// <summary>The separator after each parameter: ';', ':' or '\0' for none.</summary>
            public ReadOnlySpan<char> Separators { get; }

            /// <summary>The parameter text as received, for the logs.</summary>
            public ReadOnlySpan<char> Raw { get; }

            public int Arg0 => Args.Length > 0 ? Args[0] : 0;

            public bool IsPrivate => Leader == '?';

            // A control sequence is identified by its final byte TOGETHER with its leader and its
            // intermediates. "CSI ? Pi;Pa;Pv S" is not "CSI Pn S" wearing a decoration - it is
            // XTSMGRAPHICS, a different sequence that merely ends in the same byte. Executing the
            // bare meaning for a prefixed spelling is therefore never "close enough"; it runs the
            // wrong command on a caller that asked for something else entirely.
            //
            // So every case below whose final byte has exactly one defined form is gated on
            // `bare`. The few finals that genuinely have several defined forms - u, c, h/l, n,
            // J/K, p, q - branch on the leader and intermediates explicitly instead, and say so.
            //
            // This is deliberately a whitelist of accepted FORMS rather than a blacklist of the
            // conflicting spellings we happen to know about. #264 guarded s/u/r; the review of
            // its fix found eight more finals with the same hole (#274). Enumerating known-bad
            // spellings is what let them through the first two times.
            public bool Bare => Leader == '\0' && Intermediates.Length == 0;
        }

        private void ApplyCursorStyle(int value)
        {
            // DECSCUSR:
            // 0/1 blinking block, 2 steady block, 3 blinking underline, 4 steady underline, 5 blinking bar, 6 steady bar
            switch (value)
            {
                case 0:
                case 1:
                    _buffer.Modes.CursorStyle = CursorStyle.Block;
                    _buffer.Modes.IsCursorBlinkEnabled = true;
                    break;
                case 2:
                    _buffer.Modes.CursorStyle = CursorStyle.Block;
                    _buffer.Modes.IsCursorBlinkEnabled = false;
                    break;
                case 3:
                    _buffer.Modes.CursorStyle = CursorStyle.Underline;
                    _buffer.Modes.IsCursorBlinkEnabled = true;
                    break;
                case 4:
                    _buffer.Modes.CursorStyle = CursorStyle.Underline;
                    _buffer.Modes.IsCursorBlinkEnabled = false;
                    break;
                case 5:
                    _buffer.Modes.CursorStyle = CursorStyle.Beam;
                    _buffer.Modes.IsCursorBlinkEnabled = true;
                    break;
                case 6:
                    _buffer.Modes.CursorStyle = CursorStyle.Beam;
                    _buffer.Modes.IsCursorBlinkEnabled = false;
                    break;
            }
        }

        /// <summary>
        /// The cursor row as a report should state it, 1-based.
        /// </summary>
        /// <remarks>
        /// DECOM makes cursor coordinates relative to the scrolling region, and a report has to
        /// answer in the same frame the client used to set the position: CUP adds ScrollTop on the
        /// way in (see case 'H'), so a report subtracts it on the way out. Without this a client
        /// that homes to 1;1 inside a region is told it is at the region's absolute row, and will
        /// place everything that follows relative to the wrong origin.
        ///
        /// This was already wrong for plain CPR before DECXCPR existed; both are answered from
        /// here so the two can never disagree. Columns are unaffected because left/right margins
        /// (DECLRMM) are not implemented, so the horizontal origin never moves.
        /// </remarks>
        private int ReportedCursorRow()
        {
            int row = _buffer.CursorRow;
            if (_buffer.Modes.IsOriginMode)
            {
                row -= _buffer.ScrollTop;
            }

            return Math.Max(1, row + 1);
        }

        private int ClampRowForMode(int row)
        {
            if (_buffer.Modes.IsOriginMode)
            {
                return Math.Clamp(row, _buffer.ScrollTop, _buffer.ScrollBottom);
            }
            return Math.Clamp(row, 0, _buffer.Rows - 1);
        }

        private static int GetCsiParamOrDefaultOne(ReadOnlySpan<int> args, int index)
        {
            if (index >= args.Length)
            {
                return 1;
            }

            return args[index] == 0 ? 1 : args[index];
        }

        /// <summary>
        /// DECRQM (Issue #267): reports the live state of a mode back to the application via
        /// DECRPM (<c>CSI [?] Ps ; Pm $ y</c>). Pm is 1 (set), 2 (reset), or 0 (not recognized -
        /// we don't track/implement that mode at all). Apps use this to detect support for
        /// features like synchronized output (?2026) before relying on them, instead of
        /// guessing from terminal-name heuristics.
        /// </summary>
        private void HandleDecrqm(bool isPrivate, int mode)
        {
            int pm = isPrivate ? QueryPrivateModeState(mode) : QueryAnsiModeState(mode);
            string leaderText = isPrivate ? "?" : string.Empty;
            OnResponse?.Invoke($"\x1b[{leaderText}{mode};{pm}$y");
        }

        /// <summary>
        /// Live state for DEC private modes (CSI ? Ps h/l) tracked in <see cref="ModeState"/> or
        /// <see cref="TerminalBuffer"/>. Mirrors the set of modes <see cref="HandleDECPrivateMode"/>
        /// actually implements; add a case here whenever a new one gains real state.
        ///
        /// 9001 (ConPTY Passthrough Mode) is intentionally excluded: <see cref="HandleDECPrivateMode"/>
        /// accepts it but stores no flag, so there is no live state to report - reporting 1/2
        /// would be a lie, so it falls through to "not recognized" (0) like any other mode we
        /// don't track.
        /// </summary>
        private int QueryPrivateModeState(int mode)
        {
            switch (mode)
            {
                case 1: return _buffer.Modes.IsApplicationCursorKeys ? 1 : 2;    // DECCKM
                case 6: return _buffer.Modes.IsOriginMode ? 1 : 2;               // DECOM
                case 7: return _buffer.Modes.IsAutoWrapMode ? 1 : 2;             // DECAWM
                case 25: return _buffer.Modes.IsCursorVisible ? 1 : 2;           // DECTCEM
                case 47: return _buffer.IsAltScreenActive ? 1 : 2;               // Alt screen (legacy)
                case 80: return _buffer.Modes.IsSixelDisplayMode ? 1 : 2;        // DECSDM
                case 1000: return _buffer.Modes.MouseModeX10 ? 1 : 2;
                case 1002: return _buffer.Modes.MouseModeButtonEvent ? 1 : 2;
                case 1003: return _buffer.Modes.MouseModeAnyEvent ? 1 : 2;
                case 1004: return _buffer.Modes.IsFocusEventReporting ? 1 : 2;
                case 1006: return _buffer.Modes.MouseModeSGR ? 1 : 2;
                case 1047: return _buffer.IsAltScreenActive ? 1 : 2;             // Alt screen
                case 1049: return _buffer.IsAltScreenActive ? 1 : 2;             // Alt screen + save cursor
                case 2004: return _buffer.Modes.IsBracketedPasteMode ? 1 : 2;
                case 2026: return _buffer.IsSynchronizedOutput ? 1 : 2;          // Synchronized Output
                case 2048: return _inBandResizeReportsEnabled ? 1 : 2;           // Kitty in-band resize
                default: return 0; // Not recognized
            }
        }

        /// <summary>
        /// Live state for ANSI (non-private) modes (CSI Ps h/l) tracked in <see cref="ModeState"/>.
        /// </summary>
        private int QueryAnsiModeState(int mode)
        {
            switch (mode)
            {
                case 4: return _buffer.Modes.IsInsertMode ? 1 : 2;              // IRM
                // SRM: "set" (h) turns local echo OFF, "reset" (l) turns it ON. IsEchoEnabled
                // tracks the echo interpretation, so it is inverted here to report the MODE
                // flag DECRQM asks about, not the echo state directly.
                case 12: return _buffer.Modes.IsEchoEnabled ? 2 : 1;            // SRM
                case 20: return _buffer.Modes.IsLineFeedNewLineMode ? 1 : 2;     // LNM
                default: return 0; // Not recognized
            }
        }

        /// <summary>
        /// Handles DEC Private Mode sequences (CSI ? Ps h/l)
        /// </summary>
        private void HandleDECPrivateMode(ReadOnlySpan<int> modes, bool enable)
        {

            foreach (int mode in modes)
            {

                switch (mode)
                {
                    case 1: // DECCKM - Cursor Keys Mode
                        _buffer.Modes.IsApplicationCursorKeys = enable;
                        break;
                    case 6: // DECOM - Origin Mode
                        _buffer.Modes.IsOriginMode = enable;
                        // On DECOM change, move cursor to home for that mode.
                        _buffer.SetCursorPosition(0, enable ? _buffer.ScrollTop : 0);
                        break;
                    case 7: // DECAWM - Auto Wrap Mode
                        _buffer.Modes.IsAutoWrapMode = enable;
                        break;
                    case 80: // DECSDM: set disables SIXEL scrolling, matching xterm.
                        _buffer.Modes.IsSixelDisplayMode = enable;
                        break;
                    case 1004: // FocusIn/FocusOut event reporting
                        _buffer.Modes.IsFocusEventReporting = enable;
                        break;
                    case 1000: // X10 mouse reporting
                        _buffer.Modes.MouseModeX10 = enable;
                        break;
                    case 1002: // Button event tracking
                        _buffer.Modes.MouseModeButtonEvent = enable;
                        break;
                    case 1003: // Any event tracking
                        _buffer.Modes.MouseModeAnyEvent = enable;
                        break;
                    case 1006: // SGR extended mouse mode
                        _buffer.Modes.MouseModeSGR = enable;
                        break;
                    case 25:    // DECTCEM - Text Cursor Enable Mode
                        if (!enable)
                        {
                            _sawCursorHideInBatch = true;
                        }
                        else if (_sawCursorHideInBatch)
                        {
                            _sawCursorShowAfterHideInBatch = true;
                        }

                        _buffer.Modes.IsCursorVisible = enable;
                        _buffer.Invalidate();
                        break;
                    case 47:    // Alternate screen (legacy)
                        if (enable) _buffer.EnterAltScreen(clearAlt: false, saveCursorForExit: false);
                        else SwitchToMainScreen();
                        break;
                    case 1047:  // Alternate screen
                        if (enable) _buffer.EnterAltScreen(clearAlt: true, saveCursorForExit: false);
                        else SwitchToMainScreen();
                        break;
                    case 1048:  // Save/restore cursor, the same banks as DECSC/DECRC
                        if (enable) SaveCursor();
                        else RestoreCursor();
                        break;
                    case 1049:  // Alternate screen + save cursor
                        if (enable)
                        {
                            if (!_buffer.IsAltScreenActive) SaveCursor();
                            _buffer.EnterAltScreen(clearAlt: true, saveCursorForExit: true);
                        }
                        else
                        {
                            SwitchToMainScreen();
                        }
                        break;
                    case 2004:  // Bracketed Paste Mode
                        _buffer.Modes.IsBracketedPasteMode = enable;
                        break;
                    case 2026: // Synchronized Output (Batch Rendering)
                        if (enable) _buffer.BeginSync();
                        else _buffer.EndSync();
                        break;
                    case 2048: // Kitty in-band resize: resize reports on while set
                        _inBandResizeReportsEnabled = enable;
                        break;
                    case 9001: // ConPTY Passthrough Mode
                        break;
                    default:
                        // Only log unhandled modes as they might be important for future features
                        break;
                }
            }
        }

        /// <summary>
        /// Kitty keyboard protocol control sequences
        /// (https://sw.kovidgoyal.net/kitty/keyboard-protocol/). All four forms end in 'u'
        /// and are distinguished from SCO restore-cursor by their CSI leader byte:
        ///   CSI ? u              query   -> reply CSI ? flags u
        ///   CSI &gt; flags u        push    (flags omitted = 0)
        ///   CSI &lt; number u       pop     (number omitted = 1)
        ///   CSI = flags ; mode u set     (mode 1 = replace, 2 = OR, 3 = AND NOT; default 1)
        /// None of them move the cursor. Flags we do not honor are masked out by
        /// <see cref="KittyKeyboardState"/> so the query never advertises unimplemented tiers.
        /// </summary>
        private void HandleKittyKeyboardProtocol(char leader, ReadOnlySpan<int> args)
        {
            KittyKeyboardState kitty = _buffer.Modes.KittyKeyboard;

            switch (leader)
            {
                case '?':
                    // Kill switch (Blocker 2): report flags 0 while disabled regardless of the
                    // actual stack state, so a TUI probing capabilities via query never believes
                    // the protocol is active when the host has turned it off.
                    OnResponse?.Invoke(KittyKeyboardEnabled ? kitty.FormatQueryResponse() : "\x1b[?0u");
                    break;
                case '>':
                    kitty.Push(args.Length > 0 ? args[0] : 0);
                    break;
                case '<':
                    // Only an *omitted* parameter defaults to 1; an explicit "CSI < 0 u" is a
                    // distinct, valid no-op (see KittyKeyboardState.Pop's doc comment).
                    kitty.Pop(args.Length > 0 ? args[0] : 1);
                    break;
                case '=':
                    kitty.Set(
                        args.Length > 0 ? args[0] : 0,
                        args.Length > 1 && args[1] > 0 ? args[1] : 1);
                    break;
            }
        }
    }
}
