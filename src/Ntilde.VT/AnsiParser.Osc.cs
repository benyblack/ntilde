using System.Globalization;
using System.Text;

namespace Ntilde.VT
{
    /// <summary>
    /// Operating system commands (OSC): titles, the working directory, colors, the clipboard, notifications,
    /// progress, hyperlinks and shell integration marks.
    /// </summary>
    public partial class AnsiParser
    {
        /// <summary>
        /// Size cap for OSC 52 clipboard-write payloads (issue #268): 1 MiB decoded.
        /// Oversized payloads are dropped silently (logged via <see cref="TerminalLogger"/>)
        /// rather than raising <see cref="OnClipboardWrite"/>, so a runaway or malicious
        /// payload can't push an arbitrarily large blob onto the system clipboard.
        ///
        /// Enforced twice: once on the *encoded* length before <c>Convert.FromBase64String</c>,
        /// so an oversized payload never gets allocated at all (the 16 Mi-char OSC accumulator
        /// cap alone would allow a ~12.6 MB LOH allocation per sequence, decoded then thrown
        /// away), and again on the decoded length as defense in depth.
        /// </summary>
        private const int Osc52MaxDecodedBytes = 1024 * 1024;

        /// <summary>
        /// Length cap on OSC 9 notification text (issue #271): the host's toast stays a
        /// compact single line, so longer text is flattened and elided with "…".
        /// </summary>
        private const int Osc9MaxNotificationChars = 200;

        /// <summary>
        /// The legal xterm OSC 52 selection alphabet: c = clipboard, p = primary, q = secondary,
        /// s = select, 0-7 = cut buffers. Used to sanitize the targets string before it is echoed
        /// back in a query-denial reply - see <see cref="SanitizeEchoParameter"/>.
        /// </summary>
        private const string Osc52SelectionChars = "cpqs01234567";

        /// <summary>
        /// Length cap on the echoed OSC 52 targets string. The full legal alphabet is 12 chars and
        /// real-world values are one or two, so 8 is generous; the point is that the echoed value
        /// can never be used to amplify a single sequence into a large write to the child's stdin.
        /// </summary>
        private const int Osc52MaxEchoedTargetChars = 8;

        private void HandleOsc(string osc)
        {
            if (string.IsNullOrEmpty(osc)) return;
            if (osc.StartsWith("1337;File=", StringComparison.Ordinal))
            {
                HandleITerm2Image(osc);
                return;
            }

            if (osc.StartsWith("1339;", StringComparison.Ordinal))
            {
                HandleTunneledImage(osc.Substring(5));
                return;
            }

            int split = osc.IndexOf(';');
            if (split <= 0) return;

            string code = osc.Substring(0, split);
            string data = osc.Substring(split + 1);

            switch (code)
            {
                // OSC 0/2: Window title
                case "0":
                case "2":
                    if (!string.IsNullOrWhiteSpace(data))
                    {
                        OnTitleChanged?.Invoke(data.Trim());
                    }
                    break;

                // OSC 7: current working directory URI
                case "7":
                    if (TryExtractPathFromOsc7(data, out var cwd))
                    {
                        OnWorkingDirectoryChanged?.Invoke(cwd);
                    }
                    break;

                case "8":
                    SetHyperlink(data);
                    break;

                case "9":
                    HandleOscNotificationOrProgress(data);
                    break;

                case "10":
                    HandleOscColorQuery(10, data);
                    break;

                case "11":
                    HandleOscColorQuery(11, data);
                    break;

                // OSC 52: clipboard write (issue #268). See HandleOscClipboard for the format
                // and the security posture (write-only, size-capped, read-denial-only).
                case "52":
                    HandleOscClipboard(data);
                    break;

                case "133":
                    HandleShellIntegrationMark(data);
                    break;
            }
        }

        // OSC 8: hyperlinks (open/close)
        // Format: OSC 8 ; params ; URI ST/BEL
        //
        // #95 gap 2: the params field used to be discarded — only everything after the second ';' was
        // read. That field carries `id=`, which is what states that two runs of cells are one anchor,
        // so hyperlink identity was being thrown away before it reached the buffer. It is parsed here
        // and resolved into an interned identity; see Links/HyperlinkRegistry.cs.
        //
        // A close (`OSC 8 ; ; ST`) has an empty URI and resolves to null, which clears the open link.
        // Switching straight from one link to another without closing is legal per the spec and needs
        // no special handling: it is just another assignment.
        private void SetHyperlink(string data)
        {
            int secondSep = data.IndexOf(';');
            if (secondSep >= 0)
            {
                string parameters = data.Substring(0, secondSep);
                string uri = data.Substring(secondSep + 1);
                _buffer.CurrentHyperlink = _hyperlinks.Resolve(parameters, uri);
            }
        }

        // OSC 9: desktop notification / progress reporting (issue #271, ConEmu-style;
        // Claude Code emits both). Two shapes share the code:
        //   OSC 9 ; <text>                -> desktop notification with that text
        //   OSC 9 ; 4 ; <state> [; <pct>] -> progress: 0 removes, 1 normal, 2 error,
        //      3 indeterminate, 4 paused; pct is 0-100 and may be omitted (null).
        // The "4;" prefix is what distinguishes them: a bare "4" with no semicolon
        // after it is notification text, not a progress payload (ConEmu requires the
        // state parameter). The text is flattened to one line and capped so a
        // misbehaving program cannot push a wall of text at the toast; everything
        // else about showing either shape (settings, focus policy) is the App's
        // call, same split as OSC 52.
        private void HandleOscNotificationOrProgress(string data)
        {
            if (data.StartsWith("4;", StringComparison.Ordinal))
            {
                // state is mandatory; a payload without one (e.g. "4;") is dropped
                // rather than guessed at.
                string[] progressParts = data.Split(';');
                if (progressParts.Length >= 2 &&
                    int.TryParse(progressParts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int progressState))
                {
                    int? percent = null;
                    if (progressParts.Length >= 3 &&
                        int.TryParse(progressParts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rawPercent))
                    {
                        percent = Math.Clamp(rawPercent, 0, 100);
                    }

                    OnProgressReported?.Invoke(progressState, percent);
                }
            }
            else if (!string.IsNullOrWhiteSpace(data))
            {
                // One line, no stray controls. BEL/0x9C/ESC terminate the sequence,
                // but anything else — TAB, NEL, other C0/C1 — reaches here and would
                // land in the host's toast verbatim. Every surviving control becomes
                // a space; CRLF collapses to one (the \r is dropped ahead of its \n).
                var textBuilder = new StringBuilder(data.Length);
                for (int i = 0; i < data.Length; i++)
                {
                    char ch = data[i];
                    if (ch == '\r' && i + 1 < data.Length && data[i + 1] == '\n')
                    {
                        continue;
                    }

                    textBuilder.Append(char.IsControl(ch) ? ' ' : ch);
                }

                string text = textBuilder.ToString().Trim();
                if (text.Length == 0)
                {
                    // Controls-only payload: sanitization left nothing to say.
                    return;
                }

                if (text.Length > Osc9MaxNotificationChars)
                {
                    text = text[..(Osc9MaxNotificationChars - 1)] + "…";
                }

                OnDesktopNotification?.Invoke(text);
            }
        }

        // OSC 10/11: dynamic foreground/background color queries.
        // Query form is "10;?" / "11;?" (BEL and ST termination are both already
        // normalized away by the caller before HandleOsc ever sees this string).
        // xterm answers with "OSC 1x ; rgb:RRRR/GGGG/BBBB ST", 16 bits per channel
        // (8-bit channel replicated: value * 0x101). OpenCode and vim/nvim send this
        // at startup with a ~1s timeout to detect a dark/light theme; never responding
        // was the bug (#265), so an unset provider still answers with a sane default
        // instead of staying silent.
        //
        // xterm's "rgb:RRRR/GGGG/BBBB" dynamic-color-response format: each 8-bit channel is
        // widened to 16 bits by replicating the byte (0xRR -> 0xRRRR, i.e. value * 0x101).
        // xterm's dynamic-color OSC accepts multiple values chained in one sequence, each
        // advancing to the next color slot: "OSC 10;?;?" queries fg (slot 10) then bg
        // (slot 11), and xterm sends two separate replies for it. A trailing ';' is also
        // legal (e.g. "OSC 11;?;") and simply yields one extra, ignored slot. Treating the
        // whole payload as a single "is it exactly \"?\"" check — as the original code did —
        // means either of those legal, in-the-wild spellings produces silence: the same
        // stall #265 fixed, just for a different query shape. Splitting on ';' and walking
        // an incrementing slot code answers every "?" we understand (10, 11) and silently
        // skips slots we don't (e.g. 12, the cursor color) instead of dropping the request.
        private void HandleOscColorQuery(int startCode, string data)
        {
            string[] slots = data.Split(';');
            int slotCode = startCode;
            foreach (string slot in slots)
            {
                if (slot == "?")
                {
                    TermColor? color = slotCode switch
                    {
                        10 => DefaultForeground ?? TermColor.FromRgb(0xC0, 0xC0, 0xC0),
                        11 => DefaultBackground ?? TermColor.FromRgb(0x00, 0x00, 0x00),
                        _ => null
                    };

                    if (color != null)
                    {
                        OnResponse?.Invoke(FormatOscColorResponse(slotCode.ToString(System.Globalization.CultureInfo.InvariantCulture), color.Value));
                    }
                }
                // Non-"?" slots (e.g. "#ff0000" to *set* a color, or an empty slot from a
                // trailing ';') are not supported for runtime palette changes; ignore them
                // safely rather than attempting to parse/apply an unsupported request.

                slotCode++;
            }
        }

        // OSC 52 clipboard write (issue #268). Format: "OSC 52 ; <targets> ; <payload> ST/BEL"
        // (BEL and ST termination are both already normalized away by the caller before
        // HandleOsc ever sees this string, same as OSC 10/11 above).
        //
        // <targets> selects one or more selection buffers (c = clipboard, p = primary,
        // q/s/0-7 = other X11 selections/cut buffers). This terminal has no primary-selection
        // concept of its own, so both 'c' and 'p' are mapped to the single system clipboard;
        // any other/unsupported target character is ignored rather than acted on, matching
        // how other terminals no-op on selections they don't back. An empty targets string
        // defaults to "c" per xterm.
        //
        // <payload> of "?" is a read query. Clipboard READ is never implemented here (security -
        // see issue #268): terminals that deny OSC 52 reads reply with an empty payload rather
        // than staying silent (silence would leave a TUI's read timeout to expire, the same
        // failure mode #265 fixed for OSC 10/11), so an empty-payload denial is sent via
        // OnResponse instead - with the echoed targets string sanitized first, since OnResponse
        // output reaches the child's stdin (see the SECURITY notes on OnResponse and in the query
        // branch below). A sequence with no ';' at all is malformed and ignored entirely.
        // Any other payload is treated as base64; invalid base64 is
        // dropped silently, and a successfully decoded payload over Osc52MaxDecodedBytes is
        // also dropped (logged) rather than raised. Decoding happens here in the parser so
        // the App layer only ever sees raw bytes - the settings gate and clipboard access are
        // host policy and live entirely outside Ntilde.VT.
        private void HandleOscClipboard(string data)
        {
            int split = data.IndexOf(';');

            // No payload separator at all ("OSC 52 ; c BEL", "OSC 52 ; BEL"): not a write and
            // not a query, so do nothing. xterm clears the selection on an *explicitly* empty
            // payload ("OSC 52 ; c ; BEL", handled below as a zero-length decode), but a
            // sequence that never had a separator is simply malformed - clearing the user's
            // clipboard on it would be a gratuitous data loss (PR #280 review).
            if (split < 0) return;

            string targets = data.Substring(0, split);
            string payload = data.Substring(split + 1);

            if (string.IsNullOrEmpty(targets))
            {
                targets = "c";
            }

            bool targetsClipboard = false;
            foreach (char t in targets)
            {
                if (t == 'c' || t == 'p')
                {
                    targetsClipboard = true;
                    break;
                }
            }

            if (!targetsClipboard) return;

            if (payload == "?")
            {
                // Denial reply: empty payload, always ST-terminated (matches the OSC 10/11
                // reply convention above) regardless of how the query itself was terminated.
                //
                // SECURITY (PR #280 review, BLOCKER): this reply goes straight to the child's
                // stdin (see OnResponse), and `targets` is attacker-controlled - the OSC
                // accumulator passes CR/LF through untouched, so echoing it raw turned
                // `printf '\x1b]52;c\rid\r;?\a'` into `id` being executed at a shell prompt.
                // It is also unbounded up to MaxStringSequenceChars (16 Mi chars), i.e. a
                // ~16 MB stdin flood. And because this branch is parser-side and
                // unconditional, the App's AllowOsc52ClipboardWrite kill switch did not
                // mitigate it. So: whitelist the xterm selection alphabet, cap the length,
                // and fall back to "c" (the xterm default target) if nothing legal is left,
                // rather than dropping the reply - staying silent would leave a querying
                // TUI's read timeout to expire, the same failure mode #265 fixed for OSC
                // 10/11, and the denial reply exists precisely to avoid that.
                string echoTargets = SanitizeEchoParameter(targets, Osc52SelectionChars, Osc52MaxEchoedTargetChars, "c");
                OnResponse?.Invoke($"\x1b]52;{echoTargets};\x1b\\");
                return;
            }

            // Reject on *encoded* length before decoding. Convert.FromBase64String would
            // otherwise allocate up to ~12.6 MB (the 16 Mi-char OSC cap, decoded) on the LOH
            // for a payload that the decoded-size check below then throws away, once per
            // sequence. ceil(n/3)*4 is the encoded length of n bytes, so this admits exactly
            // a full Osc52MaxDecodedBytes payload and nothing larger; the decoded cap stays
            // as defense in depth (padding, and whitespace that FromBase64String skips).
            if (payload.Length > ((Osc52MaxDecodedBytes / 3) + 1) * 4)
            {
                TerminalLogger.Debug($"[ANSI_PARSER] OSC 52 clipboard write dropped: encoded payload {payload.Length} chars exceeds the {Osc52MaxDecodedBytes} byte decoded cap");
                return;
            }

            byte[] decoded;
            try
            {
                decoded = Convert.FromBase64String(payload);
            }
            catch (FormatException)
            {
                // Invalid base64: drop silently (issue #268); the parser continues normally.
                return;
            }

            if (decoded.Length > Osc52MaxDecodedBytes)
            {
                TerminalLogger.Debug($"[ANSI_PARSER] OSC 52 clipboard write dropped: decoded payload {decoded.Length} bytes exceeds {Osc52MaxDecodedBytes} byte cap");
                return;
            }

            OnClipboardWrite?.Invoke(targets, decoded);
        }

        /// <summary>
        /// Makes a sequence parameter safe to interpolate into an <see cref="OnResponse"/> reply.
        ///
        /// Replies are written verbatim to the child process's stdin, so echoing a parameter that
        /// came off the wire is a response-injection primitive: CR/LF survive the OSC/APC
        /// accumulators, and at a shell prompt CR submits the line (PR #280 review, BLOCKER 1).
        /// An unbounded parameter is also a write-amplification primitive - one sequence can carry
        /// up to <see cref="MaxStringSequenceChars"/> characters.
        ///
        /// This is a whitelist, not an escape: every character outside <paramref name="allowed"/>
        /// is dropped, the result is truncated to <paramref name="maxLength"/>, and if nothing
        /// legal survives, <paramref name="fallback"/> is returned so the caller can still emit a
        /// well-formed reply instead of going silent (silence makes a querying TUI wait out its
        /// read timeout - see #265). <paramref name="fallback"/> must itself be a literal.
        /// </summary>
        private static string SanitizeEchoParameter(string value, string allowed, int maxLength, string fallback)
        {
            if (string.IsNullOrEmpty(value)) return fallback;

            var sb = new StringBuilder(Math.Min(value.Length, maxLength));
            foreach (char c in value)
            {
                if (allowed.IndexOf(c) < 0) continue;
                sb.Append(c);
                if (sb.Length >= maxLength) break;
            }

            return sb.Length == 0 ? fallback : sb.ToString();
        }

        private static string FormatOscColorResponse(string code, TermColor color)
        {
            int r = color.R * 0x101;
            int g = color.G * 0x101;
            int b = color.B * 0x101;
            return $"\x1b]{code};rgb:{r:x4}/{g:x4}/{b:x4}\x1b\\";
        }

        /// <summary>
        /// Turns an OSC 7 payload into a filesystem path.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>The authority is always dropped (PR #293 review, blocker 3; revisited PR #351).</strong>
        /// A <c>file://</c> URI's authority is the host the path lives on. Most shell integrations -
        /// ours included until PR #293, plus bash's <c>$HOSTNAME</c>, zsh's <c>$HOST</c> and fish's
        /// <c>hostname</c> - put the *local* machine's name there as a matter of convention, and the SFTP
        /// sidebar that is the other consumer of a foreign authority is already connected to that exact
        /// host, so re-encoding either one into the path is always redundant. What is left after the
        /// authority is dropped is normalized purely by what it looks like -
        /// <see cref="NormalizeOsc7PathShape"/> native-formats a Windows drive-letter path and leaves a
        /// POSIX one untouched - not by which host reported it, because that shape is a property of the
        /// path text, not of the authority: a Windows drive letter means a Windows path whether the shell
        /// reporting it is this machine or a Windows box at the far end of an SSH session, and a POSIX
        /// path stays a POSIX path the same way.
        /// </para>
        /// <para>
        /// <strong>Why not keep rendering a foreign authority as a UNC path for other local consumers
        /// (Codex review, PR #351, second pass).</strong> Windows UNC syntax and a backslash-preserving
        /// escape are mutually exclusive - <c>\</c> is Windows' only path separator, so there is no
        /// string shape that is simultaneously a valid <c>\\host\dir</c> UNC path and losslessly
        /// preserves a POSIX filename that legally contains a literal backslash. Something has to give,
        /// and <c>Ntilde.CommandAssist.Domain.FileSystemPathSuggestionProvider</c> - the one
        /// consumer of this cwd that performs real filesystem I/O (<c>Directory.Exists</c>,
        /// <c>Directory.EnumerateDirectories</c>) - already skips itself whenever the session is remote
        /// (<c>context.IsRemote</c>, sourced from <c>Profile.Type == ConnectionType.SSH</c>, independent
        /// of anything OSC 7 reports). Every shipped shell integration only reports a foreign authority
        /// either for a genuine SSH session (already gated off above) or never at all - PowerShell's
        /// bootstrap omits the authority entirely, and WSL2's default hostname matches this machine's, so
        /// it is dropped the same way. A local, non-SSH pane reporting a *third-party* UNC host via a raw,
        /// non-Ntilde-emitted OSC 7 sequence is the one remaining case this trades away; nothing shipped
        /// produces it, and the SFTP sidebar's need for a working POSIX path from the one case that is
        /// shipped and reachable - an SSH session with a backslash in a remote directory name - wins that
        /// trade.
        /// </para>
        /// <para>
        /// <strong>Why the path text is sliced out of <paramref name="data"/> by hand instead of read
        /// from <see cref="Uri.AbsolutePath"/> or <see cref="Uri.LocalPath"/> (Codex review, PR #351).
        /// </strong> Both properties are computed through .NET's <c>file:</c>-scheme-specific
        /// canonicalization, which treats a backslash - raw <em>or</em> percent-escaped as <c>%5C</c> -
        /// as an alternate path separator and silently folds it into <c>/</c> while building the URI,
        /// before any application code ever calls <see cref="Uri.UnescapeDataString"/>. A backslash is a
        /// legal character in a POSIX filename, so a directory percent-escaped by the shipped Bash/Zsh/
        /// Fish integrations specifically to preserve it (<c>weird%5Cname</c>) would otherwise come back
        /// as two directories (<c>weird</c>, <c>name</c>) instead of one. <see cref="ExtractRawEscapedOsc7Path"/>
        /// finds the still-escaped path text directly in <paramref name="data"/> - a plain substring split
        /// on the first <c>/</c> after <c>scheme://authority</c>, untouched by that canonicalization - so
        /// the one <see cref="Uri.UnescapeDataString"/> call downstream is the only place escaping is
        /// resolved, and it resolves every escape (the <c>%5C</c> backslashes of the old Windows emission,
        /// the <c>%20</c> spaces of the new one, and a literal backslash inside a POSIX name) the same way.
        /// </para>
        /// <para>
        /// Kept tolerant of the older emissions on purpose. A pwsh instrumented by a previous Ntilde build
        /// - or a remote *Windows* SSH host still running the snippet it was given months ago (Codex
        /// review, PR #351, third pass) - sends <c>file://HOST/C:%5CUsers%5Cyou</c>, and that has to keep
        /// working even though <c>HOST</c> is now a foreign authority: the fix at the emitter cannot
        /// reach a script the user pasted onto a server, and the drive-letter shape is normalized to a
        /// real Windows path (<c>C:\Users\you</c>) regardless of which authority sent it, exactly as the
        /// paragraph above describes.
        /// </para>
        /// <para>
        /// The final fallback - hand back the payload verbatim - is what catches a payload that is not a
        /// URI at all (a bare path, which some shells emit) and, before PR #293, the old emission
        /// with no hostname: <c>file:///C:%5CUsers%5Cyou</c> is rejected by
        /// <see cref="Uri.TryCreate"/> outright, so the literal URI string was being reported as the
        /// working directory.
        /// </para>
        /// </remarks>
        private static bool TryExtractPathFromOsc7(string data, out string path)
        {
            path = string.Empty;
            if (string.IsNullOrWhiteSpace(data)) return false;

            if (Uri.TryCreate(data, UriKind.Absolute, out var uri) && uri.IsFile)
            {
                string rawEscapedPath = ExtractRawEscapedOsc7Path(data, uri);
                path = NormalizeOsc7PathShape(Uri.UnescapeDataString(rawEscapedPath));
                return !string.IsNullOrWhiteSpace(path);
            }

            path = data.Trim();
            return !string.IsNullOrWhiteSpace(path);
        }

        /// <summary>
        /// The still-percent-escaped path portion of an OSC 7 <c>file:</c> payload, read directly from
        /// the original text rather than from <see cref="Uri.AbsolutePath"/> - see the remarks on
        /// <see cref="TryExtractPathFromOsc7"/> for why that distinction matters. An authority never
        /// legally contains a raw <c>/</c>, so the first one found after <c>scheme://</c> is unambiguously
        /// where the path starts.
        /// </summary>
        private static string ExtractRawEscapedOsc7Path(string data, Uri uri)
        {
            int schemeEnd = data.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd < 0)
            {
                return uri.AbsolutePath;
            }

            int pathStart = data.IndexOf('/', schemeEnd + 3);
            return pathStart < 0 ? uri.AbsolutePath : data[pathStart..];
        }

        /// <summary>
        /// Turns the (already unescaped, authority-stripped) path component of an OSC 7 <c>file:</c>
        /// payload into a path the shape it describes recognizes - a Windows drive-letter path is
        /// un-rooted and native-separated, a POSIX path is returned untouched. Applied the same way
        /// regardless of which authority the payload came from (Codex review, PR #351, third pass): the
        /// shape is a property of the path text, not of the host that reported it, so a foreign SSH host
        /// still running an old, hostname-bearing Windows emission gets a real Windows path
        /// (<c>C:\Users\you</c>) instead of a mixed-separator string that is neither a valid Windows path
        /// nor a valid POSIX one.
        /// </summary>
        /// <remarks>
        /// The leading slash handled here (<c>ExtractRawEscapedOsc7Path</c> always includes one, whether
        /// or not an authority was present) is not itself part of a Windows path - <c>C:/x</c>, not
        /// <c>/C:/x</c>, is the real one - so it is stripped before the drive-letter check, but only ever
        /// for a drive-rooted path. A POSIX path keeps its leading slash, and must not have its
        /// separators touched - a backslash in a POSIX filename is a legal character.
        /// </remarks>
        private static string NormalizeOsc7PathShape(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;

            int start = 0;
            if (path[0] == '/' && HasDriveLetterAt(path, 1))
            {
                start = 1;
            }

            if (!HasDriveLetterAt(path, start))
            {
                // POSIX. Return the tail as-is, including any leading slash.
                return start == 0 ? path : path[start..];
            }

            return path[start..].Replace('/', '\\');
        }

        private static bool HasDriveLetterAt(string value, int index) =>
            value.Length > index + 1 &&
            char.IsLetter(value[index]) &&
            value[index + 1] == ':';
    }
}
