using System;
using System.Threading.Tasks;
using Ntilde.CommandAssist.Domain;
using Ntilde.CommandAssist.Models;
using Ntilde.CommandAssist.ShellIntegration.Contracts;

namespace Ntilde.CommandAssist.Application;

/// <summary>
/// Everything that turns a command the user ran into a history entry: the heuristic Enter-time
/// capture, the structured OSC 133 capture, the dedup between the two, secret redaction, and the
/// exit-code / duration patch that lands when the command finishes.
/// </summary>
/// <remarks>
/// <para>
/// There are two capture paths because there are two moments a command becomes known. The
/// structured path is the shell telling us, through <c>OSC 133;C</c>, the text it accepted; that is
/// authoritative and survives multi-line input, history recall and line editing. The Enter-time
/// path is the host telling us what it could see on the command line when the user pressed Enter.
/// Since Phase 1c its first source is the terminal grid, read between the newest <c>OSC 133;B</c>
/// mark and the cursor. Since Phase 1d it has a second, strictly narrower source for the sessions
/// that have no marks at all (`cmd.exe`, a bailed-out bootstrap, an un-instrumented SSH host): the
/// host's <c>MarklessSubmissionAccumulator</c>, which offers a line only when the user typed it
/// straight through with no editing <em>and</em> that exact text is painted on the grid at the
/// cursor. It is not the V1 keystroke mirror returning - the mirror guessed, and its wrong guesses
/// went to permanent history; this one answers "nothing" for everything it cannot model, so what
/// reaches here is either the typed line verbatim or an empty string.
/// </para>
/// <para>
/// The paths overlap for exactly one command. Structured capture only stands down the heuristic once
/// <see cref="AssistSessionContext.HasObservedStructuredCommandCaptureMarker"/> is set, and that only
/// becomes true when the first <c>CommandAccepted</c> arrives - which is after the first Enter. So
/// the first command of an instrumented session is captured twice unless something notices, and the
/// something is the pending-entry dedup below: an accepted command whose text matches the entry the
/// heuristic path just wrote is dropped, and the finish event patches the heuristic entry instead.
/// </para>
/// <para>
/// Capture is best-effort throughout. Every store call is wrapped: a history write must never take
/// down the keystroke that triggered it.
/// </para>
/// </remarks>
internal sealed class CapturePipeline
{
    private readonly IHistoryStore _historyStore;
    private readonly ISecretsFilter _secretsFilter;
    private readonly AssistSessionContext _context;

    /// <summary>
    /// The exit code every POSIX shell uses for "I could not find that program".
    /// </summary>
    /// <remarks>
    /// bash, zsh and fish all report it; PowerShell does not, and reports 1 for an unresolved name
    /// exactly as it does for a command that ran and failed. That gap is why the flag has a second
    /// source - see <see cref="MarkLastCommandInvalidAsync"/>.
    /// </remarks>
    internal const int CommandNotFoundExitCode = CommandHistoryEntry.CommandNotFoundExitCode;

    /// <summary>
    /// Guards <see cref="_pending"/> and <see cref="_lastCompleted"/>, which are written by two
    /// threads: the Enter-time capture runs on whatever thread the pane started it from (the UI
    /// thread), and the structured capture and the completion patch run on the pane's serialized
    /// shell-integration dispatcher, which is a thread-pool queue.
    /// </summary>
    private readonly object _pendingGate = new();

    /// <summary>
    /// The entry the next completion patches. Registered <em>before</em> its append starts, never
    /// after.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ordering is the whole point. The Enter-time append and the <c>OSC 133;D</c> that finishes
    /// the same command travel on different queues, and a fast command (<c>true</c>, <c>cd</c>)
    /// produces its <c>D</c> within milliseconds of Enter - inside the append, which takes the
    /// store's gate and a cross-process file lease. When the id was assigned only after the append
    /// returned, a <c>D</c> handled in that window found nothing pending and the exit code and
    /// duration were silently dropped; a <c>C</c> handled in it missed the double-capture guard and
    /// the first command of a session was written twice.
    /// </para>
    /// <para>
    /// Registering first closes both. The dedup only needs the text, and the completion awaits
    /// <see cref="PendingEntry.Appended"/> before it patches, so a patch can never reach the store
    /// ahead of the entry it patches.
    /// </para>
    /// </remarks>
    private PendingEntry? _pending;

    /// <summary>
    /// The entry the most recently finished command was written to, retained after the completion
    /// has released <see cref="_pending"/>.
    /// </summary>
    /// <remarks>
    /// It exists for one caller and one ordering problem. The Fix-time recogniser is what identifies a
    /// PowerShell command-not-found, and it runs <em>after</em> <c>CommandFinished</c> has already
    /// patched the exit code and released the pending id; without somewhere to remember which entry
    /// that was, the classification would arrive with nothing to apply itself to. Cleared when a new
    /// command is captured, so a late classification can never be applied to the wrong entry.
    /// </remarks>
    private PendingEntry? _lastCompleted;

    /// <summary>
    /// Whether the current prompt cycle has already contributed a structured history entry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A volume bound, and the reason it exists is that the writer of an <c>OSC 133;C</c> is not
    /// necessarily a shell. Over SSH the marks come from whatever is on the other end of the
    /// connection, and any process that can write to the pane's stdout can write a <c>C</c>: a
    /// <c>cat</c> of a crafted file does it locally too. Entries land in the global, cross-session
    /// history, so an unbounded emitter is an unbounded writer of other sessions' suggestions.
    /// </para>
    /// <para>
    /// One per cycle rather than a rate limit because it is the bound the real integrations already
    /// satisfy: a prompt cycle is by construction one accepted command, and all four of Ntilde's own
    /// emitters - plus iTerm2's, VS Code's and starship's - emit exactly one <c>C</c> between the
    /// <c>B</c> that opened the input line and the <c>D</c> that closed it. Nothing legitimate is
    /// being clipped; a flood is.
    /// </para>
    /// <para>
    /// Reset by every edge that starts a new cycle - <c>A</c>, <c>B</c> and <c>D</c> - rather than
    /// by <c>D</c> alone, because a shell that omits <c>D</c> (or whose command was interrupted)
    /// must not be locked out of capture for the rest of the session.
    /// </para>
    /// </remarks>
    private bool _structuredEntryWrittenThisCycle;

    public CapturePipeline(
        IHistoryStore historyStore,
        ISecretsFilter secretsFilter,
        AssistSessionContext context)
    {
        _historyStore = historyStore;
        _secretsFilter = secretsFilter;
        _context = context;
    }

    /// <summary>
    /// Heuristic capture: the user pressed Enter and we believe <paramref name="submission"/> is what
    /// went to the shell.
    /// </summary>
    /// <param name="submission">
    /// The command line as the host could see it at the instant Enter was observed: read out of the
    /// terminal grid where there is a live <c>OSC 133;B</c> mark, and otherwise the markless
    /// accumulator's straight-through-typed line, gated on that text being echoed on screen. An
    /// empty string means the host had nothing truthful to offer - a closed lifecycle gate, an
    /// unreadable mark, an accumulator poisoned by an edit it could not model, or a prompt that did
    /// not echo - and nothing is persisted. That asymmetry is the whole point: the source deleted in
    /// Phase 1c (a keystroke mirror with no such gates) wrote commands the user never ran into
    /// permanent history.
    /// </param>
    /// <param name="isSubmissionSuppressed">
    /// Whether the session marked this submission untrustworthy (pasted rather than typed).
    /// </param>
    public async Task CaptureSubmissionAsync(string submission, bool isSubmissionSuppressed)
    {
        try
        {
            string trimmed = submission.Trim();

            // IsHistoryEnabled is the V2 Phase 3b decoupling: with history capture off the rest of
            // the feature still runs (paths, Help, Fix, the popup), and this pipeline is one of the
            // exactly two things the flag now gates. Checked here rather than at the call site so
            // that no caller can write history by forgetting to ask.
            bool shouldPersist = _context.IsHistoryEnabled &&
                                 !_context.IsAltScreenActive &&
                                 !_context.IsStructuredCaptureActive &&
                                 !isSubmissionSuppressed &&
                                 !string.IsNullOrWhiteSpace(trimmed) &&
                                 !trimmed.Contains('\n') &&
                                 !trimmed.Contains('\r');

            if (!shouldPersist)
            {
                return;
            }

            RedactionResult redaction = _secretsFilter.Redact(trimmed);
            var entry = new CommandHistoryEntry(
                Id: Guid.NewGuid().ToString("N"),
                CommandText: redaction.RedactedText,
                ExecutedAt: DateTimeOffset.UtcNow,
                ShellKind: _context.ShellKind ?? "unknown",
                WorkingDirectory: _context.WorkingDirectory,
                ProfileId: _context.ProfileId,
                SessionId: _context.SessionId,
                HostId: _context.HostId,
                ExitCode: null,
                IsRemote: _context.IsRemote,
                IsRedacted: redaction.WasRedacted,
                Source: CommandCaptureSource.Heuristic,
                DurationMs: null);

            // Registered before the append, not after it - see _pending. Registering also retires
            // any classification still owed to the previous command: without that, a Fix analysis
            // that arrived late could mark the wrong entry as a typo.
            PendingEntry pending = RegisterPending(entry.Id, NormalizeCommandText(trimmed));
            await AppendPendingAsync(entry, pending);
        }
        catch
        {
            // Assist capture is best-effort; Enter should still reach the shell path even if persistence fails.
        }
    }

    /// <summary>
    /// Patches the pending entry with an exit code observed outside shell integration (the host's own
    /// command-finished signal).
    /// </summary>
    public Task CompleteSubmissionAsync(int? exitCode) => CompletePendingAsync(exitCode, durationMs: null);

    /// <summary>
    /// Records that the command behind the most recent entry was one the shell could not resolve, so
    /// nothing may offer it back as a suggestion.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called by <c>CommandAssistController.HandleCommandFailureAsync</c> when the recogniser table
    /// classified the failure as <c>command-not-found</c>. It is the PowerShell half of the signal:
    /// see <see cref="CommandNotFoundExitCode"/> for why the exit code cannot carry it there, and
    /// <c>CommandHistoryEntry.IsInvalidCommand</c> for why the entry is flagged rather than deleted.
    /// </para>
    /// <para>
    /// <strong>Only ever the completed entry, never the pending one.</strong> A failure analysis is
    /// always preceded by the completion that reported the exit code - that is what tells the host a
    /// command failed at all - so the completed entry is the right target by construction. Falling
    /// back to the pending entry would buy nothing and would risk the one outcome worse than
    /// suggesting a typo: silently suppressing a command that works, because an analysis of the
    /// previous line arrived after the next had been typed.
    /// </para>
    /// </remarks>
    /// <param name="unresolvedCommandToken">
    /// The name the shell could not resolve when the recogniser established it is the command's own
    /// first token, or <see langword="null"/>. Non-null propagates the classification to every older
    /// history entry starting with that word - see
    /// <c>IHistoryStore.TryMarkInvalidCommandsByFirstTokenAsync</c>. This is what makes the flag reach
    /// the owner's pre-existing <c>gti status</c> entries, which were captured before the flag existed
    /// and would otherwise have needed the typo retyped once per distinct line to suppress.
    /// </param>
    public async Task MarkLastCommandInvalidAsync(string? unresolvedCommandToken = null)
    {
        PendingEntry? completed;
        lock (_pendingGate)
        {
            completed = _lastCompleted;
        }

        if (completed is null)
        {
            return;
        }

        try
        {
            // The completion released the entry before waiting for its append (see
            // CompletePendingAsync), so the append may still be in flight here too.
            if (!await completed.Appended)
            {
                return;
            }

            await _historyStore.TryMarkInvalidCommandAsync(completed.Id);

            // After the entry itself, and in the same best-effort block: the just-captured entry is the
            // one the user can see the consequence of, so it is flagged first and a failure in the
            // sweep cannot cost it.
            if (!string.IsNullOrWhiteSpace(unresolvedCommandToken))
            {
                await _historyStore.TryMarkInvalidCommandsByFirstTokenAsync(unresolvedCommandToken!);
            }
        }
        catch
        {
            // Best-effort like every other write here: failing to suppress a typo must not take down
            // the Fix suggestion the user is about to be shown for it.
        }
    }

    private static bool IsCommandNotFoundExit(int? exitCode) => exitCode == CommandNotFoundExitCode;

    /// <summary>
    /// Consumes a shell-integration event: records what it proves about the session, then runs the
    /// structured capture or the completion patch it implies.
    /// </summary>
    public async Task HandleShellIntegrationEventAsync(ShellIntegrationEvent shellEvent)
    {
        if (shellEvent.WorkingDirectory != null)
        {
            _context.SetWorkingDirectory(shellEvent.WorkingDirectory);
        }

        if (shellEvent.Type is ShellIntegrationEventType.PromptReady or
            ShellIntegrationEventType.CommandAccepted or
            ShellIntegrationEventType.CommandStarted or
            ShellIntegrationEventType.CommandFinished)
        {
            _context.ObserveShellIntegrationMarker();
        }

        // Deliberately conditioned on the text rather than on the event type. A C mark with no
        // payload is a lifecycle edge and nothing more; treating it as proof that the shell reports
        // command text would stand the heuristic path down in favour of a structured path that
        // never writes an entry, and the session would capture nothing at all. See
        // AssistSessionContext.HasObservedStructuredCommandCaptureMarker.
        if (shellEvent.Type is ShellIntegrationEventType.CommandAccepted &&
            !string.IsNullOrWhiteSpace(shellEvent.CommandText))
        {
            _context.ObserveStructuredCommandCaptureMarker();
        }

        if (shellEvent.Type is ShellIntegrationEventType.PromptReady or
            ShellIntegrationEventType.CommandStarted or
            ShellIntegrationEventType.CommandFinished)
        {
            _structuredEntryWrittenThisCycle = false;
        }

        switch (shellEvent.Type)
        {
            case ShellIntegrationEventType.WorkingDirectoryChanged:
            case ShellIntegrationEventType.PromptReady:
            case ShellIntegrationEventType.CommandStarted:
                return;

            case ShellIntegrationEventType.CommandAccepted:
                await CaptureAcceptedCommandAsync(shellEvent);
                return;

            case ShellIntegrationEventType.CommandFinished:
                await CompleteAcceptedCommandAsync(shellEvent);
                return;
        }
    }

    private async Task CaptureAcceptedCommandAsync(ShellIntegrationEvent shellEvent)
    {
        // IsShellIntegrationLive rather than IsShellIntegrationEnabled: an accepted-command event
        // only reaches here from an armed ShellLifecycleTracker, and since Phase 2b a tracker is
        // armed for remote sessions we could not inject a bootstrap into. The mark is the evidence.
        //
        // IsHistoryEnabled joins them for V2 Phase 3b: the marker observations in the caller are
        // lifecycle facts and still run with history off, but nothing is written.
        if (!_context.IsShellIntegrationLive || _context.IsAltScreenActive || !_context.IsHistoryEnabled)
        {
            return;
        }

        // A textless C (see AnsiParser.DecodeAcceptedCommandPayload) has already done its work:
        // the marker observation above, and the gate close the controller applied before calling
        // us. There is nothing to persist and nothing to dedup against, and crucially the pending
        // entry the heuristic path wrote for this same command is left intact so CommandFinished
        // can still patch its exit code and duration.
        string commandText = shellEvent.CommandText?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(commandText))
        {
            return;
        }

        // The volume bound (see _structuredEntryWrittenThisCycle). Checked after the marker
        // observations above, which are lifecycle facts and stay true however many C marks arrive,
        // and before any history write.
        if (_structuredEntryWrittenThisCycle)
        {
            return;
        }

        // The first-command double-capture guard: the heuristic path already wrote this exact command
        // moments ago, so let its entry stand and let CommandFinished patch that one.
        //
        // The heuristic entry counts from the moment it is registered, not from when its append
        // lands: the C for a fast command routinely arrives while that append is still in flight.
        string normalizedCommandText = NormalizeCommandText(commandText);
        PendingEntry? matchedHeuristic;
        lock (_pendingGate)
        {
            matchedHeuristic = _pending != null &&
                               string.Equals(_pending.CommandText, normalizedCommandText, StringComparison.Ordinal)
                ? _pending
                : null;
        }

        // But it only stands in for this C once it has actually landed. If that append fails, this C
        // is the last record of the command, so it falls through and writes its own entry; the
        // failed append has already withdrawn its registration.
        if (matchedHeuristic != null && await matchedHeuristic.Appended)
        {
            _structuredEntryWrittenThisCycle = true;
            return;
        }

        // A failed append does not prove the line is absent: JsonlHistoryStore writes it before it
        // compacts, and a compaction failure throws afterwards. Taking over the matched entry's id
        // makes this record supersede it on load (last write wins) rather than sit beside it as a
        // second copy with no exit code. Only the matched entry - an unmatched pending entry is a
        // different command, and reusing its id would overwrite it.
        string entryId = matchedHeuristic?.Id ?? Guid.NewGuid().ToString("N");

        try
        {
            RedactionResult redaction = _secretsFilter.Redact(commandText);
            var entry = new CommandHistoryEntry(
                Id: entryId,
                CommandText: redaction.RedactedText,
                ExecutedAt: shellEvent.Timestamp,
                ShellKind: _context.ShellKind ?? "unknown",
                WorkingDirectory: shellEvent.WorkingDirectory ?? _context.WorkingDirectory,
                ProfileId: _context.ProfileId,
                SessionId: _context.SessionId,
                HostId: _context.HostId,
                ExitCode: null,
                IsRemote: _context.IsRemote,
                IsRedacted: redaction.WasRedacted,
                Source: CommandCaptureSource.ShellIntegration,
                DurationMs: null);

            // See CaptureSubmissionAsync: registered first, and registering retires the previous
            // command's right to a late classification.
            PendingEntry pending = RegisterPending(entry.Id, normalizedCommandText);
            await AppendPendingAsync(entry, pending);
            _structuredEntryWrittenThisCycle = true;
        }
        catch
        {
            // Structured capture is best-effort and must not affect shell execution.
        }
    }

    private Task CompleteAcceptedCommandAsync(ShellIntegrationEvent shellEvent)
    {
        long? durationMs = shellEvent.Duration.HasValue
            ? (long)Math.Round(shellEvent.Duration.Value.TotalMilliseconds)
            : null;

        return CompletePendingAsync(shellEvent.ExitCode, durationMs);
    }

    private PendingEntry RegisterPending(string entryId, string normalizedCommandText)
    {
        var pending = new PendingEntry(entryId, normalizedCommandText);
        lock (_pendingGate)
        {
            _pending = pending;
            _lastCompleted = null;
        }

        return pending;
    }

    /// <summary>
    /// Appends <paramref name="entry"/> and resolves <paramref name="pending"/>'s
    /// <see cref="PendingEntry.Appended"/> either way, so no completion waiting on it can hang.
    /// </summary>
    /// <remarks>
    /// A failed append also withdraws the registration, if nothing newer has replaced it: there is no
    /// entry to patch, and a <c>C</c> for the same command must be free to write its own rather than
    /// be deduplicated against one that never landed. Rethrows so the caller's best-effort handler
    /// still sees the failure.
    /// </remarks>
    private async Task AppendPendingAsync(CommandHistoryEntry entry, PendingEntry pending)
    {
        try
        {
            await _historyStore.AppendAsync(entry);
            pending.MarkAppended(true);
        }
        catch
        {
            pending.MarkAppended(false);
            lock (_pendingGate)
            {
                if (ReferenceEquals(_pending, pending))
                {
                    _pending = null;
                }
            }

            throw;
        }
    }

    /// <summary>
    /// Patches the pending entry with a finished command's result.
    /// </summary>
    /// <remarks>
    /// The entry is claimed - taken out of <see cref="_pending"/> - before anything is awaited, so two
    /// completions for the same command (the host signal and a <c>D</c>) cannot both patch it, and a
    /// command captured while this one waits keeps its own registration. Only then does it wait for
    /// the append, which is what lets a <c>D</c> that overtook the Enter-time write still land.
    /// </remarks>
    private async Task CompletePendingAsync(int? exitCode, long? durationMs)
    {
        PendingEntry? pending;
        lock (_pendingGate)
        {
            pending = _pending;
            if (pending is null)
            {
                return;
            }

            _pending = null;
            _lastCompleted = pending;
        }

        try
        {
            if (!await pending.Appended)
            {
                // The append failed; there is nothing in the store to patch.
                return;
            }

            await _historyStore.TryUpdateExecutionResultAsync(
                pending.Id,
                exitCode,
                durationMs,
                isInvalidCommand: IsCommandNotFoundExit(exitCode));
        }
        catch
        {
            // History metadata enrichment is best-effort only.
        }
    }

    /// <summary>
    /// A captured command awaiting its completion patch.
    /// </summary>
    private sealed class PendingEntry
    {
        private readonly TaskCompletionSource<bool> _appended =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PendingEntry(string id, string commandText)
        {
            Id = id;
            CommandText = commandText;
        }

        public string Id { get; }

        /// <summary>The normalized text, for the first-command double-capture guard.</summary>
        public string CommandText { get; }

        /// <summary>
        /// Completes once the entry's append has finished: <see langword="true"/> if it reached the
        /// store, <see langword="false"/> if it failed.
        /// </summary>
        public Task<bool> Appended => _appended.Task;

        public void MarkAppended(bool succeeded) => _appended.TrySetResult(succeeded);
    }

    private static string NormalizeCommandText(string commandText) => commandText.Trim();
}
