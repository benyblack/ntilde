using System;
using System.Collections.Generic;
using System.Linq;

namespace Ntilde.AgentHost
{
    /// <summary>
    /// One recorded agent acting attempt (A3), or a run of identical reads of a
    /// windowless session (Phase 5 ruling R5). Denied attempts are recorded too:
    /// the journal is a visibility surface, so the user can see everything an
    /// agent tried, not only what succeeded.
    /// </summary>
    public sealed record AgentActivityEntry
    {
        /// <summary>When it happened; for a folded read, the latest of its <see cref="Count"/>.</summary>
        public required DateTimeOffset TimestampUtc { get; init; }

        /// <summary>Protocol method (<see cref="Contracts.AgentHostProtocol.Methods"/>), e.g. "sendInput".</summary>
        public required string Method { get; init; }

        /// <summary>Target pane, or windowless session (<see cref="Windowless"/>), when the call addressed one.</summary>
        public Guid? PaneId { get; init; }

        /// <summary>Human-readable target (profile name, pane title, or a short summary of the payload).</summary>
        public required string Target { get; init; }

        /// <summary>"ok" for a successful call, otherwise the stable error code that was returned.</summary>
        public required string Outcome { get; init; }

        /// <summary>
        /// How many identical reads this entry stands for (<see cref="AgentActivityJournal.RecordRead"/> folds them);
        /// 1 for every act.
        /// </summary>
        public int Count { get; init; } = 1;

        /// <summary>True when <see cref="PaneId"/> is a windowless session's id (its mux session id), not a pane's.</summary>
        public bool Windowless { get; init; }
    }

    /// <summary>
    /// Thread-safe bounded ring of recent agent acting attempts (A3) and reads of
    /// windowless sessions (Phase 5 ruling R5). In-memory only — a visibility
    /// surface, not an audit log (the agent's own MCP transcript is the audit
    /// trail). The acting endpoint appends on every acting request; the UI
    /// subscribes to <see cref="EntryAdded"/>.
    /// </summary>
    /// <remarks>
    /// Reads fold (<see cref="RecordRead"/>): an agent polling a windowless session's status every second would
    /// otherwise push every act out of the ring within minutes. They fold only within the reads since the last act, so
    /// the order of acts, and of the reads between them, stays readable.
    /// </remarks>
    public sealed class AgentActivityJournal
    {
        /// <summary>Process-wide instance used by the app wiring. Tests construct their own.</summary>
        public static AgentActivityJournal Instance { get; } = new();

        /// <summary>Maximum retained entries; older ones are evicted.</summary>
        public const int Capacity = 200;

        private readonly object _gate = new();
        private readonly LinkedList<Slot> _entries = new(); // oldest first
        private readonly Func<DateTimeOffset> _now;

        /// <summary>An entry, and whether it is a read (which may fold) or an act (which never does and ends a run of reads).</summary>
        private readonly record struct Slot(AgentActivityEntry Entry, bool IsRead);

        public AgentActivityJournal(Func<DateTimeOffset>? nowProvider = null)
        {
            _now = nowProvider ?? (static () => DateTimeOffset.UtcNow);
        }

        /// <summary>
        /// Raised after an entry is appended, or after a read folded into an entry (with the entry as it now is; may fire
        /// on a background/IPC thread).
        /// </summary>
        public event Action<AgentActivityEntry>? EntryAdded;

        /// <summary>Records an acting attempt and its outcome. Never folds. Returns the stored entry.</summary>
        public AgentActivityEntry Record(string method, Guid? paneId, string target, string outcome, bool windowless = false)
        {
            var entry = NewEntry(method, paneId, target, outcome, windowless);
            lock (_gate)
            {
                AppendLocked(new Slot(entry, IsRead: false));
            }

            Raise(entry);
            return entry;
        }

        /// <summary>
        /// Records a read and its outcome. A read identical to one since the last act (same method, id, target and
        /// outcome) folds into it: that entry becomes the newest, its <see cref="AgentActivityEntry.Count"/> goes up by
        /// one and its time is now. Returns the stored entry.
        /// </summary>
        public AgentActivityEntry RecordRead(string method, Guid? paneId, string target, string outcome, bool windowless = false)
        {
            var fresh = NewEntry(method, paneId, target, outcome, windowless);
            AgentActivityEntry stored;
            lock (_gate)
            {
                LinkedListNode<Slot>? same = null;
                for (var node = _entries.Last; node is { Value.IsRead: true }; node = node.Previous)
                {
                    if (SameRead(node.Value.Entry, fresh))
                    {
                        same = node;
                        break;
                    }
                }

                if (same is null)
                {
                    stored = fresh;
                    AppendLocked(new Slot(stored, IsRead: true));
                }
                else
                {
                    stored = fresh with { Count = same.Value.Entry.Count + 1 };
                    _entries.Remove(same);
                    _entries.AddLast(new Slot(stored, IsRead: true));
                }
            }

            Raise(stored);
            return stored;
        }

        /// <summary>Point-in-time snapshot, newest first.</summary>
        public IReadOnlyList<AgentActivityEntry> Snapshot()
        {
            lock (_gate)
            {
                return _entries.Reverse().Select(slot => slot.Entry).ToArray();
            }
        }

        public int Count
        {
            get { lock (_gate) { return _entries.Count; } }
        }

        private AgentActivityEntry NewEntry(string method, Guid? paneId, string target, string outcome, bool windowless) => new()
        {
            TimestampUtc = _now(),
            Method = method,
            PaneId = paneId,
            Target = target ?? string.Empty,
            Outcome = outcome,
            Windowless = windowless,
        };

        private static bool SameRead(AgentActivityEntry a, AgentActivityEntry b) =>
            string.Equals(a.Method, b.Method, StringComparison.Ordinal)
            && a.PaneId == b.PaneId
            && string.Equals(a.Target, b.Target, StringComparison.Ordinal)
            && string.Equals(a.Outcome, b.Outcome, StringComparison.Ordinal)
            && a.Windowless == b.Windowless;

        private void AppendLocked(Slot slot)
        {
            _entries.AddLast(slot);
            while (_entries.Count > Capacity)
            {
                _entries.RemoveFirst();
            }
        }

        private void Raise(AgentActivityEntry entry)
        {
            // Isolate subscriber faults: Record runs on the acting path *after*
            // input has already been delivered, so a throwing UI subscriber must
            // not propagate out and turn a successful sendInput into an error the
            // caller would retry (double-submitting the input).
            try
            {
                EntryAdded?.Invoke(entry);
            }
            catch
            {
                // best effort — the journal is a visibility surface, not critical path
            }
        }
    }
}
