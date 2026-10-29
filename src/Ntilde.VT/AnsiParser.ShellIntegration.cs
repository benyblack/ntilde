using System.Globalization;
using System.Text;

namespace Ntilde.VT
{
    /// <summary>
    /// OSC 133 shell-integration marks: where a B mark landed, and the command text a C mark carries.
    /// </summary>
    public partial class AnsiParser
    {
        // OSC 133: shell integration markers.
        // Common terminals/shell integrations emit:
        //   OSC 133;A     -> prompt start (prompt ready)
        //   OSC 133;B     -> prompt end / start of user input; reported with the cursor
        //                    position at parse time, which is where the command line begins
        //   OSC 133;C[;X] -> command accepted; X, when present, is usually the base64-encoded
        //                    command text but is not required to be (see
        //                    DecodeAcceptedCommandPayload)
        //   OSC 133;D;N[;M] -> command finished with exit code N and optional duration M
        //
        // Any parameters after the marker letter are tolerated and ignored (FinalTerm
        // allows key=value attributes on these marks, e.g. "133;B;aid=7"), so a payload
        // we don't understand still produces the mark rather than being dropped.
        private void HandleShellIntegrationMark(string data)
        {
            if (string.IsNullOrWhiteSpace(data)) return;

            string[] parts = data.Split(';', StringSplitOptions.None);
            switch (parts[0])
            {
                case "A":
                    // We're back at a shell prompt: clear any mouse/focus reporting a TUI
                    // left enabled after an unclean exit, so pointer moves don't flood the
                    // prompt with leaked ESC[<..M reports.
                    _buffer.Modes.ResetTransientInputReporting();
                    OnPromptReady?.Invoke();
                    break;

                case "B":
                    // The command-input window and the mark are one fact in two parts, so they are
                    // published together, here, under the buffer's tracked-mark lock and before any
                    // subscriber runs. Splitting them is what #448 cost twice over: first the window
                    // lagged the mark by a queue hop and a submitted command was dropped from
                    // history; then, with the window moved here but the mark still written by a
                    // subscriber, the window briefly pointed at the *previous* command's mark - which
                    // fabricates a capture rather than losing one.
                    ShellIntegrationMark mark = CaptureCursorMark();
                    _buffer.BeginCommandInput(mark);
                    OnCommandStarted?.Invoke(mark);
                    break;

                case "C":
                    // The line has been submitted. The mark deliberately survives C - the input line
                    // is still on screen at this instant - so closing the window is the only thing
                    // that stops the cells below it being served as a command line once the output
                    // starts arriving.
                    _buffer.CloseCommandInputWindow();
                    OnCommandAccepted?.Invoke(DecodeAcceptedCommandPayload(parts));
                    break;

                case "D":
                    HandleCommandFinished(parts);
                    break;
            }
        }

        /// <summary>
        /// <c>OSC 133;D;N[;M]</c>: the command finished with exit code N and, optionally, a duration of M
        /// milliseconds.
        /// </summary>
        private void HandleCommandFinished(string[] parts)
        {
            int? exitCode = null;
            long? durationMs = null;
            if (parts.Length > 1 &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                exitCode = parsed;
            }

            if (parts.Length > 2 &&
                long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedDuration))
            {
                durationMs = parsedDuration;
            }

            // Closed at D as well as C: a shell can reach D with no intervening C, and
            // leaving the window open for a command's whole run is the failure it exists to
            // prevent.
            _buffer.CloseCommandInputWindow();

            OnCommandFinished?.Invoke(exitCode);
            OnCommandFinishedDetailed?.Invoke(exitCode, durationMs);
        }

        /// <summary>
        /// Snapshots the cursor as a <see cref="ShellIntegrationMark"/>. Called synchronously
        /// while the OSC is being dispatched, so the cursor is still exactly where the shell
        /// left it when it wrote the mark — for OSC 133;B that is the first cell of the user's
        /// input, immediately after the last prompt cell.
        /// </summary>
        private ShellIntegrationMark CaptureCursorMark()
        {
            bool isAltScreen = _buffer.IsAltScreenActive;

            // The alt screen has no scrollback: rows are addressed by viewport row alone and
            // there is no eviction counter to anchor against.
            int scrollbackRows = isAltScreen ? 0 : _buffer.Scrollback.Count;
            long evictedRows = isAltScreen ? 0 : _buffer.Scrollback.TotalRowsEvicted;

            int row = scrollbackRows + _buffer.CursorRow;
            return new ShellIntegrationMark(
                Row: row,
                Column: _buffer.CursorCol,
                AbsoluteRow: evictedRows + row,
                IsAltScreen: isAltScreen,
                // Recorded even for alt-screen marks: the alt screen has no scrollback of its
                // own, so the main-screen epoch is the only coordinate space that can be reset
                // underneath a consumer holding this mark.
                Generation: _buffer.Scrollback.Generation);
        }

        /// <summary>
        /// The largest <c>OSC 133;C</c> payload that will be looked at, in characters of the
        /// on-the-wire (still encoded) text. See <see cref="DecodeAcceptedCommandPayload"/>.
        /// </summary>
        internal const int MaxAcceptedCommandPayloadChars = 8 * 1024;

        /// <summary>
        /// The command text an <c>OSC 133;C</c> mark carries, or <see langword="null"/> when it
        /// carries none we can trust.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Four shapes are in the wild. The first declares itself; the other three are not
        /// distinguishable by declaration, only by inspection:
        /// </para>
        /// <list type="number">
        /// <item><description>
        /// <c>133;C;cmdline_url=&lt;percent-encoded&gt;</c> — what fish 4.0.1 and later send with their
        /// own marks (kitty's attribute). Looked for among all the parameters before anything else.
        /// </description></item>
        /// <item><description>
        /// <c>133;C;&lt;base64&gt;</c> — what all four Ntilde bootstraps emit, and the only shape that
        /// survives a command containing <c>;</c> or a newline. Tried first, and accepted only if the
        /// bytes decode to plausible text: <c>make</c>, <c>date</c> and <c>true</c> are all valid
        /// base64 by shape, so "it decoded" is not evidence on its own. Byte sequences that are not
        /// UTF-8 come back peppered with U+FFFD, which is the tell.
        /// </description></item>
        /// <item><description>
        /// no payload at all (<c>133;C</c> or <c>133;C;</c>) — iTerm2's and VS Code's snippets, and
        /// most hand-rolled ones. The lifecycle edge with no text.
        /// </description></item>
        /// <item><description>
        /// plain text (<c>133;C;git status</c>) — some third-party integrations. Passed through as
        /// written, which is lossy for a command containing <c>;</c> (the parameter split already
        /// happened) but strictly better than discarding it.
        /// </description></item>
        /// </list>
        /// <para>
        /// FinalTerm attribute payloads (<c>133;C;aid=7</c>) are the trap in shape 4: they are
        /// printable, so a bare "printable means command text" rule would write <c>aid=7</c> into the
        /// user's permanent history. Anything matching an identifier followed by <c>=</c> that did not
        /// already decode as base64 is treated as an attribute and yields <see langword="null"/>.
        /// </para>
        /// <para>
        /// Every branch that cannot answer yields <see langword="null"/> rather than a guess, on the
        /// same rule the rest of the capture path follows: a missing history entry is recoverable, an
        /// invented one is not.
        /// </para>
        /// <para>
        /// <b>Known residual.</b> An <em>unpadded</em> base64 payload that decodes to non-text is
        /// indistinguishable from a short plain-text command - <c>date</c> and <c>AQID</c> are the
        /// same four characters as far as anything here can tell - so it falls through to the
        /// plain-text reading and is returned as written. Padding is checked because it is the one
        /// piece of self-description base64 has; there is no equivalent signal without it, and
        /// guessing "this four-character word is really a blob" would cost every
        /// <c>date</c>/<c>make</c>/<c>htop</c> on a plain-text integration to protect against a
        /// payload shape nothing in the wild emits. The residual runs the other way too: a short
        /// real command drawn entirely from the base64 alphabet can decode to printable garbage and
        /// be returned as that garbage rather than as itself.
        /// </para>
        /// <para>
        /// <b>Bound.</b> Anything over <see cref="MaxAcceptedCommandPayloadChars"/> is rejected
        /// before the decode. Whoever is on the other end of an SSH connection chooses this payload
        /// and it reaches permanent, cross-session history, so the size of a single entry should not
        /// be theirs to pick; 8 KiB is an order of magnitude past the longest command line anyone
        /// types and two orders past every one Ntilde's own snippets emit. Checked on the encoded
        /// text, before <c>Convert.FromBase64String</c> allocates anything.
        /// </para>
        /// </remarks>
        private static string? DecodeAcceptedCommandPayload(string[] parts)
        {
            if (parts.Length <= 1)
            {
                return null;
            }

            for (int i = 1; i < parts.Length; i++)
            {
                if (parts[i].StartsWith(CmdlineUrlAttribute, StringComparison.Ordinal))
                {
                    return DecodeCmdlineUrl(parts[i]);
                }
            }

            string payload = parts[1];
            if (string.IsNullOrWhiteSpace(payload) || payload.Length > MaxAcceptedCommandPayloadChars)
            {
                return null;
            }

            try
            {
                byte[] bytes = Convert.FromBase64String(payload);
                string decoded = Encoding.UTF8.GetString(bytes);
                if (!string.IsNullOrWhiteSpace(decoded) && IsPlausibleCommandText(decoded))
                {
                    return decoded;
                }

                // The decode produced something that is not text. Padding is the one piece of
                // self-description base64 has, so a padded payload that decodes to non-text is
                // definitively a broken base64 payload and yields null rather than being handed on
                // as literal text - nobody's command line is "//79/A==".
                if (payload.EndsWith('='))
                {
                    return null;
                }
            }
            catch (FormatException)
            {
                // Not base64. Fall through to the plain-text reading.
            }

            if (LooksLikeFinalTermAttribute(payload) || !IsPlausibleCommandText(payload))
            {
                return null;
            }

            return payload;
        }

        private const string CmdlineUrlAttribute = "cmdline_url=";

        /// <summary>
        /// The command in a <c>cmdline_url=</c> attribute: percent-encoded UTF-8, which carries a
        /// <c>;</c> or a newline intact. Bounded on the encoded attribute and held to the same
        /// plausibility check as the other readings, since percent-encoding can carry control
        /// characters the raw wire format cannot.
        /// </summary>
        private static string? DecodeCmdlineUrl(string attribute)
        {
            if (attribute.Length > MaxAcceptedCommandPayloadChars)
            {
                return null;
            }

            string decoded = Uri.UnescapeDataString(attribute.AsSpan(CmdlineUrlAttribute.Length));
            return !string.IsNullOrWhiteSpace(decoded) && IsPlausibleCommandText(decoded) ? decoded : null;
        }

        /// <summary>
        /// Whether <paramref name="text"/> could be something a user typed at a prompt: no control
        /// characters other than tab/CR/LF, and no U+FFFD (which is what UTF-8 decoding leaves behind
        /// when the bytes were never text).
        /// </summary>
        private static bool IsPlausibleCommandText(string text)
        {
            // U+FFFD, written numerically so the source stays ASCII.
            const char ReplacementChar = (char)0xFFFD;

            foreach (char c in text)
            {
                if (c == '\t' || c == '\r' || c == '\n')
                {
                    continue;
                }

                if (c == ReplacementChar || char.IsControl(c))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Whether <paramref name="payload"/> reads as a FinalTerm <c>key=value</c> mark attribute
        /// (<c>aid=7</c>, <c>cl=m</c>) rather than as command text. Only consulted after base64
        /// decoding has already failed, so base64 padding (<c>bHM=</c>) never reaches it.
        /// </summary>
        /// <remarks>
        /// Three conditions, and the whitespace one is what keeps <c>FOO=bar make</c> a command:
        /// an attribute is a single token, so anything with a space in it is a command line that
        /// happens to open with an environment assignment. A payload of exactly <c>FOO=bar</c> with
        /// nothing after it is genuinely ambiguous and is read as an attribute - that is the safe
        /// direction, since the cost is one missing history entry rather than <c>aid=7</c> appearing
        /// in the user's command history.
        /// </remarks>
        private static bool LooksLikeFinalTermAttribute(string payload)
        {
            int equals = payload.IndexOf('=');
            if (equals <= 0)
            {
                return false;
            }

            if (!char.IsLetter(payload[0]) && payload[0] != '_')
            {
                return false;
            }

            for (int i = 1; i < equals; i++)
            {
                char c = payload[i];
                if (!char.IsLetterOrDigit(c) && c != '_' && c != '-')
                {
                    return false;
                }
            }

            foreach (char c in payload)
            {
                if (char.IsWhiteSpace(c))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
