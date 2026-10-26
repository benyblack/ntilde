using Ntilde.Shell;
using Ntilde.VT;
using Ntilde.CommandAssist.Application;

namespace Ntilde.Controls
{
    /// <summary>
    /// What the user submits: Enter, typed text, backspaces and pastes, read back from the screen when the
    /// shell marks nothing.
    /// </summary>
    public partial class TerminalPane
    {
        // True between "the user pressed a key that edits the command line" and "the session sent
        // us bytes we have parsed into the grid". See NoteInputAwaitingEcho for why insertion
        // refuses while it is set. Written from the UI thread, cleared from the PTY read thread.
        private volatile bool _hasUnechoedInput;

        // Enter-time history capture for sessions with no OSC 133 marks (V2 Phase 1, task 7).
        // Never the query - see MarklessSubmissionAccumulator for why this is not the shadow
        // buffer coming back, and OnCommandAssistEnterObserved for how it composes with grid truth.
        private readonly MarklessSubmissionAccumulator _marklessSubmission = new();

        /// <remarks>
        /// The grid read happens here, synchronously, rather than inside the async continuation.
        /// <c>TerminalView</c> sends the carriage return to the PTY before raising this, so the
        /// input line is already condemned: every scheduling hop between the keypress and the read
        /// widens the window in which the shell has begun repainting over it. This is as close to
        /// the keypress as the pane can get.
        /// </remarks>
        // What OnCommandAssistEnterObserving read out of the grid, waiting for the post-CR phase to
        // persist it. Two fields rather than one, because null is a meaningful answer here: "the
        // line was observed empty" and "there was nothing to observe" are different, and only the
        // first should reach the pipeline.
        private string? _pendingEnterSubmission;
        private bool _hasPendingEnterSubmission;

        /// <summary>
        /// Enter's grid read, before the carriage return goes to the PTY.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is as close to the keypress as the pane can get, and closer than it used to be:
        /// <c>TerminalView</c> sent the carriage return first, so the shell had already been handed
        /// the chance to repaint over the line this reads. Worse, once the OSC 133 command-input
        /// window began closing synchronously on the parse thread, a bare <c>133;C</c> could shut it
        /// between the CR and this method - and a refused read here means the command is never
        /// persisted at all (Codex P1 on #448).
        /// </para>
        /// <para>
        /// Reads only. The persistence it feeds runs in <see cref="OnCommandAssistEnterObserved"/>,
        /// after the CR, so a slow history store cannot sit between the keypress and the shell.
        /// </para>
        /// </remarks>
        internal void OnCommandAssistEnterObserving()
        {
            _pendingEnterSubmission = null;
            _hasPendingEnterSubmission = false;

            // The reset is owed even when Command Assist is off or uninitialized: the accumulator
            // is fed from TermView events that fire regardless, and a line left in it would be
            // carried into the next one.
            if (!EnsureCommandAssistInitialized())
            {
                _marklessSubmission.Reset();
                return;
            }

            AssistQuerySnapshot? snapshot = _commandAssistController.TryReadQuerySnapshot();

            // Grid truth always wins where it exists, including when it says the line was empty:
            // "observed empty" and "unknown" are different answers, and only the second one falls
            // through to the accumulator. A poisoned accumulator answers null, which
            // CapturePipeline reads as "nothing to persist".
            string? submitted = snapshot is { } grid
                ? grid.Text
                : ReadEchoedMarklessSubmission();

            // After the read: Enter is both the capture point and the start of the next line.
            _marklessSubmission.Reset();

            _pendingEnterSubmission = submitted;
            _hasPendingEnterSubmission = true;
        }

        /// <summary>
        /// Persists what <see cref="OnCommandAssistEnterObserving"/> read, after the carriage return
        /// has already reached the shell.
        /// </summary>
        internal void OnCommandAssistEnterObserved()
        {
            if (!_hasPendingEnterSubmission)
            {
                return;
            }

            _hasPendingEnterSubmission = false;
            string? submitted = _pendingEnterSubmission;
            _pendingEnterSubmission = null;

            _ = HandleCommandAssistEnterObservedAsync(submitted);
        }

        /// <summary>
        /// The accumulator's answer, but only when the screen agrees with it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This gate exists to keep passwords out of history.</b> The accumulator is fed from
        /// <c>TerminalView.OnTextInput</c>, which fires for every keystroke unconditionally — it
        /// has no idea whether the shell is echoing. So in a markless session (`cmd.exe`, an
        /// un-instrumented SSH host), the sequence `ssh host` / Enter / `hunter2` / Enter leaves the
        /// accumulator holding a clean, unpoisoned `hunter2` at the hidden `password:` prompt, with
        /// no grid snapshot to override it — and it would be written to `history.jsonl` verbatim.
        /// <c>SecretsFilter</c> cannot save us there: it is pattern-based and a bare secret has no
        /// pattern.
        /// </para>
        /// <para>
        /// The check is the one thing that distinguishes the two cases. In any <em>visible</em>
        /// markless prompt the typed command is on the screen — only the <c>OSC 133;B</c> mark is
        /// missing, not the text — so requiring the accumulated string to be painted on the grid
        /// ending at the cursor costs a correct capture nothing. At a no-echo prompt the grid holds
        /// the prompt and nothing else, and the strings do not match.
        /// </para>
        /// <para>
        /// Conservative in every direction: no buffer, an alt screen, a cursor the reader will not
        /// resolve, an echo that has not landed yet, a shell that reprinted the line differently —
        /// all of them fall out as "no capture". Comparison is on text rather than columns, so a
        /// double-width character counts once (see
        /// <see cref="GridQueryReader.TryReadTextEndingAtCursor"/>).
        /// </para>
        /// </remarks>
        private string? ReadEchoedMarklessSubmission()
        {
            string? typed = _marklessSubmission.TryReadSubmission();
            if (string.IsNullOrEmpty(typed))
            {
                // Poisoned, or an empty line: either way there is nothing to prove.
                return typed;
            }

            TerminalBuffer? buffer = Buffer;
            if (buffer == null)
            {
                return null;
            }

            if (!GridQueryReader.TryReadTextEndingAtCursor(buffer, typed.Length, out string onScreen))
            {
                return null;
            }

            // Exactly typed.Length characters were requested, so equality is "the accumulated text
            // is painted on the grid and ends at the cursor". A short read (the row does not hold
            // that much) fails the same way a wrong read does.
            return string.Equals(onScreen, typed, StringComparison.Ordinal) ? typed : null;
        }

        /// <summary>
        /// The user typed printable text into the terminal. Feeds the markless accumulator and
        /// triggers a suggestion refresh; the text itself is content only for the accumulator.
        /// </summary>
        internal void NotifyTypedTextObserved(string text)
        {
            _marklessSubmission.AppendTypedText(text);
            NoteInputAwaitingEcho();
            if (EnsureCommandAssistInitialized())
            {
                _commandAssistController?.NotifyInputActivity();
            }
        }

        /// <summary>The user pressed Backspace; the accumulator drops its last character.</summary>
        internal void NotifyBackspaceObserved()
        {
            _marklessSubmission.ObserveBackspace();
            NoteInputAwaitingEcho();
            if (EnsureCommandAssistInitialized())
            {
                _commandAssistController?.NotifyInputActivity();
            }
        }

        /// <summary>
        /// Text arrived on the command line from somewhere other than the keyboard (a drag-and-drop
        /// or a clipboard paste).
        /// </summary>
        /// <remarks>
        /// Two mechanisms fire here and they are not the same one. The accumulator is
        /// <em>poisoned</em>, because it did not see the characters and can no longer describe the
        /// line. Command Assist is told the submission is <em>suppressed</em>, which is a
        /// provenance claim — the text on the line was not composed here — and that one applies to
        /// the grid path as well, where the line is perfectly readable and still should not be
        /// written to history as something the user typed. Either alone stops the paste being
        /// captured in a markless session; only suppression stops it in an instrumented one.
        /// </remarks>
        /// <param name="text">
        /// Ignored. The pasted text is deliberately not read: neither mechanism below wants it —
        /// the accumulator's answer to "what is on the line now" is "I cannot say", not "the line
        /// plus this", and suppression is a provenance flag with no payload. The parameter stays
        /// because it is the <c>TerminalView.PasteObserved</c> signature and a future consumer
        /// (length-based heuristics, say) would want it.
        /// </param>
        internal void NotifyPasteObserved(string text)
        {
            _ = text;
            _marklessSubmission.Poison();
            if (EnsureCommandAssistInitialized())
            {
                _commandAssistController?.NotifyPastedInput();
            }
        }

        /// <summary>
        /// Bytes reached this pane's PTY from somewhere other than its own keyboard handling: a
        /// broadcast from a sibling pane, the drag-and-drop path toast, a clipboard image path, the
        /// agent host's act surface. The accumulator can no longer describe the command line.
        /// </summary>
        /// <remarks>
        /// A poison rather than a reset, because none of these callers can say whether what they
        /// sent ended in a newline: unlike Enter, they leave the line in a state only the shell
        /// knows. Poison recovers at the next Enter, which costs at most one capture.
        /// </remarks>
        internal void NotifyExternalInputSent() => _marklessSubmission.Poison();

        /// <summary>
        /// Brings the terminal viewport back to the live input line, for input that enters the
        /// PTY from outside <see cref="TerminalView"/>'s own key/text handling (window clipboard
        /// paste, toast path paste). Writing while scrolled up into scrollback must reveal the
        /// line being written; see <see cref="TerminalView.ScrollToInputLine"/>.
        /// </summary>
        public void ScrollToInputLine() => TermView.ScrollToInputLine();

        private async Task HandleCommandAssistEnterObservedAsync(string? submittedText)
        {
            if (!EnsureCommandAssistInitialized())
            {
                return;
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(submittedText))
                {
                    _lastRelevantCommandText = submittedText.Trim();
                }

                await _commandAssistController.HandleEnterAsync(submittedText);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TerminalPane] Command Assist enter handling failed: {ex.Message}");
            }
        }
    }
}
