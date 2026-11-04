using System;
using System.Collections.Generic;

namespace Ntilde.VT
{
    /// <summary>
    /// The raw window title and icon name the stream has requested (OSC 0/1/2), and the XTWINOPS
    /// title stack that saves and restores them (<c>CSI 22 ; Ps t</c> / <c>CSI 23 ; Ps t</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// VT keeps only the semantic state the stack needs. The title callback still carries the
    /// exact raw payload (#817) and the host still owns presentation and fallback. The icon name
    /// is state only: no callback and no host icon presentation.
    /// </para>
    /// <para>
    /// Before any OSC 0/1/2 the requested title and icon are empty, which xterm also treats as
    /// "use the default". Saving before any request and restoring later therefore raises the
    /// callback with an empty string, and the host shows its fallback label.
    /// </para>
    /// <para>
    /// The stack follows xterm's behaviour (<c>misc.c</c> <c>xtermPushTitle</c>/<c>xtermPopTitle</c>,
    /// <c>MAX_SAVED_TITLES 10</c>):
    /// <list type="bullet">
    /// <item>At most 10 entries. A push onto a full stack discards the oldest entry. Later pops
    /// below the retained depth do nothing, as xterm's cleared ring slots do.</item>
    /// <item><c>Ps</c> 0 (or omitted) saves or restores both, 1 the icon name, 2 the window title.
    /// A one-sided push copies the other half from the entry below, if there is one, so a later
    /// <c>Ps</c> 0 pop restores what xterm's "try higher levels" lookup would find. With no entry
    /// below, that half is absent and restoring it changes nothing.</item>
    /// <item>A pop on an empty stack is a no-op. Every successful title restore raises
    /// <see cref="OnTitleChanged"/>, even when the value is unchanged.</item>
    /// <item>Other selectors, a nonzero third parameter (xterm's direct slot access), colon
    /// sub-parameters, extra parameters and prefixed spellings are ignored and leave the stack
    /// unchanged.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Lifecycle: the stack is session-wide, so alternate-screen switches do not touch it. DECSTR
    /// keeps it, as xterm does. RIS clears it (xterm keeps it). RIS does not change the current
    /// title or icon or raise the callback, because the host still shows that title. Complete
    /// snapshots (<see cref="AnsiParserState"/>) carry the title, icon and stack, so a mux attach
    /// or replay checkpoint restores what a later pop will raise.
    /// </para>
    /// <para>
    /// Memory bound: a title or icon longer than <see cref="MaxSavedTitleChars"/> is still sent to
    /// the host in full. It is not retained, so saving it stores an absent half, and restoring
    /// that half changes nothing. xterm itself ignores title requests over 1000 bytes. The bound
    /// keeps the retained state at 22 strings of at most 8 Ki chars each.
    /// </para>
    /// </remarks>
    public partial class AnsiParser
    {
        /// <summary>xterm's <c>MAX_SAVED_TITLES</c>.</summary>
        internal const int MaxSavedTitles = 10;

        /// <summary>The longest title or icon name retained for the stack.</summary>
        internal const int MaxSavedTitleChars = 8 * 1024;

        // null means "too long to retain" (or, in an entry, "not saved and nothing below").
        private string? _windowTitle = string.Empty;
        private string? _iconName = string.Empty;

        // Oldest first. Entries are immutable, so exporting copies only the list.
        private readonly List<SavedTitleEntry> _savedTitles = new();

        private static string? RetainedTitle(string? value)
            => value is not null && value.Length <= MaxSavedTitleChars ? value : null;

        /// <summary>OSC 0 (title and icon) and OSC 2 (title only).</summary>
        private void SetWindowTitle(string title, bool alsoIconName)
        {
            _windowTitle = RetainedTitle(title);
            if (alsoIconName) _iconName = _windowTitle;
            // Empty requests clear the old title; presentation fallback belongs to the host.
            OnTitleChanged?.Invoke(title);
        }

        /// <summary>OSC 1: icon-name state only. No callback and no host presentation.</summary>
        private void SetIconName(string iconName) => _iconName = RetainedTitle(iconName);

        /// <summary>
        /// XTWINOPS 22 (push) and 23 (pop). <paramref name="args"/> starts with the 22/23 itself.
        /// The caller has already required a bare spelling.
        /// </summary>
        private void HandleTitleStack(ReadOnlySpan<int> args, ReadOnlySpan<char> separators)
        {
            if (args.Length is < 1 or > 3) return;
            foreach (char separator in separators)
            {
                if (separator == ':') return; // no sub-parameters are defined
            }

            int selector = args.Length > 1 ? args[1] : 0;
            if (selector is < 0 or > 2) return;
            if (args.Length == 3 && args[2] != 0) return; // xterm direct slot access: unsupported

            bool icon = selector is 0 or 1;
            bool title = selector is 0 or 2;
            if (args[0] == 22) PushTitle(icon, title);
            else PopTitle(icon, title);
        }

        private void PushTitle(bool icon, bool title)
        {
            SavedTitleEntry? below = _savedTitles.Count > 0 ? _savedTitles[^1] : null;
            var entry = new SavedTitleEntry
            {
                Icon = icon ? _iconName : below?.Icon,
                Title = title ? _windowTitle : below?.Title,
            };

            if (_savedTitles.Count == MaxSavedTitles) _savedTitles.RemoveAt(0);
            _savedTitles.Add(entry);
        }

        private void PopTitle(bool icon, bool title)
        {
            if (_savedTitles.Count == 0) return;
            SavedTitleEntry entry = _savedTitles[^1];
            _savedTitles.RemoveAt(_savedTitles.Count - 1);

            if (icon && entry.Icon is string savedIcon) _iconName = savedIcon;
            if (title && entry.Title is string savedTitle)
            {
                _windowTitle = savedTitle;
                OnTitleChanged?.Invoke(savedTitle);
            }
        }

        /// <summary>RIS: the stack goes; the current title and icon stay, as the host still shows them.</summary>
        private void ResetTitleStack() => _savedTitles.Clear();

        private List<SavedTitleEntry>? ExportTitleStack()
        {
            if (_savedTitles.Count == 0) return null;
            var copy = new List<SavedTitleEntry>(_savedTitles.Count);
            foreach (SavedTitleEntry entry in _savedTitles)
            {
                copy.Add(new SavedTitleEntry { Icon = entry.Icon, Title = entry.Title });
            }

            return copy;
        }

        /// <summary>
        /// Adopts imported title state under the same bounds the live path keeps: the newest
        /// <see cref="MaxSavedTitles"/> entries, and over-long strings become absent.
        /// </summary>
        private void ImportTitleState(AnsiParserState state)
        {
            _windowTitle = RetainedTitle(state.WindowTitle);
            _iconName = RetainedTitle(state.IconName);
            _savedTitles.Clear();
            if (state.TitleStack is not { } stack) return;
            int skip = Math.Max(0, stack.Count - MaxSavedTitles);
            for (int i = skip; i < stack.Count; i++)
            {
                SavedTitleEntry entry = stack[i];
                _savedTitles.Add(new SavedTitleEntry
                {
                    Icon = RetainedTitle(entry.Icon),
                    Title = RetainedTitle(entry.Title),
                });
            }
        }
    }
}
