using Avalonia.Threading;

using Ntilde.Controls;

namespace Ntilde
{
    /// <summary>
    /// The notices session persistence raises (a shell kept, detached or lost), queued until the window can
    /// show them.
    /// </summary>
    public partial class MainWindow
    {
        // UI thread: notices raised since the coalesced toast was posted, keyed by title (a remote notice by title
        // and message, see EnqueueNotice), in arrival order.
        private readonly List<(string Key, string Title, string Message, int Count)> _pendingPersistenceNotices = [];

        // UI thread: the last action raised with those notices; the merged toast offers it (Phase 4 spec §7.5).
        private PersistenceNoticeAction? _pendingNoticeAction;

        // UI thread: the notices the toast shows now - the last flush's - so DismissNotice can take one off. Empty once the
        // toast hides or shows anything else.
        private List<(string Key, string Title, string Message, int Count)> _shownPersistenceNotices = [];

        // UI thread: the keys of notices that stay on the toast until DismissNotice takes them off (EnqueueNotice's untilDismissed).
        private readonly HashSet<string> _noticesUntilDismissed = [];

        // UI thread: the merge keys of pending notices raised as the answer to a user command (EnqueueNotice's prompted).
        // Focus mode shows these at once and holds the rest until it is left.
        private readonly HashSet<string> _promptedPendingNotices = [];

        // UI thread: _pendingNoticeAction was raised with a prompted notice, so it goes with those when focus mode splits them off.
        private bool _pendingNoticeActionPrompted;

        /// <summary>
        /// A pane's session will not persist, or replaced a lost one. Shown as a toast rather than
        /// written into the pane (its shell paints over local text), with the notice's action, if any.
        /// </summary>
        private void OnPanePersistenceNotice(TerminalPane pane, string title, string message, PersistenceNoticeAction? action)
        {
            _ = pane;
            // Every pane that falls back raises it, and so does each retry after the connection
            // cooldown: the user needs to hear it once per launch, not once per pane.
            if (title == TerminalPane.MuxOrphanedNoticeTitle)
            {
                if (_muxOrphanedNoticeShown) return;
                _muxOrphanedNoticeShown = true;
            }

            // A pane refuses Remote Files only when the user just opened it or moved in it, so that refusal answers them;
            // every other pane notice arrives on its own.
            EnqueueNotice(title, message, action, prompted: title == TerminalPane.RemoteFilesUnavailableNoticeTitle);
        }

        private bool _muxOrphanedNoticeShown; // UI thread

        /// <summary>
        /// UI thread. The one way a session notice reaches the toast: everything raised together - panes
        /// restoring after a daemon crash, startup adoption, the detached-shells reminder - merges into
        /// one toast, one line per kind, instead of each ShowRecordingToast replacing the last. The toast
        /// has one action button, so the merged toast offers the last non-null <paramref name="action"/>.
        /// </summary>
        /// <remarks>
        /// A remote notice (<see cref="TerminalPane.RemoteMuxUnavailableNoticeTitle"/>) is one line per message - per
        /// host and reason - not per title: merged by title, the longer of two hosts' lines was shown with the other
        /// host's action (the Task 19 note), so the button could install on a host its line did not name. Each
        /// action's own line is now always in the toast; panes of one host and reason still merge ("(n panes)").
        /// <para>
        /// <paramref name="key"/>, when given, is what the notice merges by instead: the "from another build" notices
        /// (<see cref="Ntilde.Shell.Mux.MuxPreviousBuildNotice"/>) pass their endpoint, since two daemons whose hosts share
        /// a display name raise the same words and must still keep a line each (Task 23, review item 5).
        /// </para>
        /// <para>
        /// <paramref name="untilDismissed"/> keeps the toast up until <see cref="DismissNotice"/> takes this notice off (or the
        /// user closes the toast), instead of the usual few seconds: "Connecting to …" lasts as long as the connect does.
        /// </para>
        /// <para>
        /// <paramref name="prompted"/> says the notice answers something the user just did. Focus mode holds every other
        /// notice until it is left (focus mode spec §6) and shows these at once; outside focus mode it changes nothing.
        /// </para>
        /// </remarks>
        internal void EnqueueNotice(string title, string message, PersistenceNoticeAction? action = null, string? key = null, bool untilDismissed = false, bool prompted = false)
        {
            if (action is not null)
            {
                _pendingNoticeAction = action;
                _pendingNoticeActionPrompted = prompted;
            }

            string mergeKey = key ?? (title == TerminalPane.RemoteMuxUnavailableNoticeTitle ? $"{title}\n{message}" : title);
            if (untilDismissed) _noticesUntilDismissed.Add(mergeKey);
            if (prompted) _promptedPendingNotices.Add(mergeKey);
            int index = _pendingPersistenceNotices.FindIndex(n => n.Key == mergeKey);
            bool first = _pendingPersistenceNotices.Count == 0;
            if (index < 0) _pendingPersistenceNotices.Add((mergeKey, title, message, 1));
            else
            {
                (string k, string t, string m, int c) = _pendingPersistenceNotices[index];
                // A version-mismatch hint on any of them is worth keeping.
                _pendingPersistenceNotices[index] = (k, t, message.Length > m.Length ? message : m, c + 1);
            }

            // In focus mode the queue can hold notices waiting for leave, so a prompted one needs a flush of its own.
            if (!first && !(prompted && _isFocusMode)) return;
            this.Dispatcher.Post(FlushPersistenceNotices, DispatcherPriority.Background);
        }

        /// <summary>
        /// UI thread. A notice already raised under <paramref name="key"/> turns out to answer the user's command - the
        /// "Shell detached" a close says for itself, whoever closed - so focus mode shows it at once. A no-op when none is
        /// pending.
        /// </summary>
        private void PromptPendingNotice(string key)
        {
            if (!_pendingPersistenceNotices.Exists(n => n.Key == key)) return;
            _promptedPendingNotices.Add(key);
            if (_isFocusMode) this.Dispatcher.Post(FlushPersistenceNotices, DispatcherPriority.Background);
        }

        private void FlushPersistenceNotices()
        {
            if (_pendingPersistenceNotices.Count == 0) return;
            List<(string Key, string Title, string Message, int Count)> notices;
            PersistenceNoticeAction? action;
            if (_isFocusMode)
            {
                // Focus mode holds what arrived on its own (LeaveFocusMode flushes it) and shows what answers the user.
                notices = _pendingPersistenceNotices.FindAll(n => _promptedPendingNotices.Contains(n.Key));
                if (notices.Count == 0) return;
                _pendingPersistenceNotices.RemoveAll(n => _promptedPendingNotices.Contains(n.Key));
                _promptedPendingNotices.Clear();
                action = _pendingNoticeActionPrompted ? _pendingNoticeAction : null;
                if (_pendingNoticeActionPrompted)
                {
                    _pendingNoticeAction = null;
                    _pendingNoticeActionPrompted = false;
                }
            }
            else
            {
                notices = [.. _pendingPersistenceNotices];
                _pendingPersistenceNotices.Clear();
                _promptedPendingNotices.Clear();
                action = _pendingNoticeAction;
                _pendingNoticeAction = null;
                _pendingNoticeActionPrompted = false;
            }

            if (_teardownDone) return;

            ShowPersistenceNotices(notices, action);
        }

        /// <summary>
        /// UI thread. One toast surface: the most recent kind wins its title, every kind keeps a line. A toast that offers an
        /// action stays until the user takes it or closes it: an offer that vanished after a few seconds could not be taken at
        /// all. So does one with a notice raised to stay until dismissed.
        /// </summary>
        private void ShowPersistenceNotices(List<(string Key, string Title, string Message, int Count)> notices, PersistenceNoticeAction? action)
        {
            string title = notices[^1].Title;
            string message = string.Join('\n', notices.Select(n => BuildPersistenceNoticeMessage(n.Title, n.Message, n.Count)));
            bool stays = action is not null || notices.Exists(n => _noticesUntilDismissed.Contains(n.Key));
            ShowRecordingToast(title, message, filePath: null, folderPath: null, autoHide: !stays, action);
            _shownPersistenceNotices = notices;
        }

        /// <summary>
        /// UI thread. Takes the notice raised under <paramref name="key"/> (<see cref="EnqueueNotice"/>'s merge key) off: out of
        /// the queue if it is not shown yet, otherwise off the toast - which hides when that was its only line, and shows the
        /// others again without it. A notice the toast no longer shows (another one replaced it) is left as it is.
        /// </summary>
        internal void DismissNotice(string key)
        {
            _noticesUntilDismissed.Remove(key);
            _promptedPendingNotices.Remove(key);
            _pendingPersistenceNotices.RemoveAll(n => n.Key == key);
            int index = _shownPersistenceNotices.FindIndex(n => n.Key == key);
            if (index < 0) return;
            List<(string Key, string Title, string Message, int Count)> rest = [.. _shownPersistenceNotices];
            rest.RemoveAt(index);
            if (rest.Count == 0)
            {
                HideRecordingToast();
                return;
            }

            ShowPersistenceNotices(rest, _recordingToastAction);
        }

        internal static string BuildPersistenceNoticeMessage(string title, string message, int count)
        {
            if (count <= 1) return message;
            if (title == TerminalPane.MuxPreviousLostNoticeTitle)
                return $"[{count} previous sessions were lost \u2014 started new shells]";
            if (title == TerminalPane.MuxAttachedElsewhereNoticeTitle)
                return $"[{count} previous shells are open in another window \u2014 started new shells]";
            if (title == TerminalPane.MuxUnavailableNoticeTitle)
            {
                string hint = message.Contains(TerminalPane.MuxVersionMismatchHint, StringComparison.Ordinal)
                    ? "\n" + TerminalPane.MuxVersionMismatchHint
                    : string.Empty;
                return $"[Multiplexer unavailable \u2014 {count} sessions will not persist]{hint}";
            }

            return $"{message} ({count} panes)";
        }
    }
}
