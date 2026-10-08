namespace Ntilde.Shell
{
    /// <summary>What an OSC 9;4 progress report says (issue #271).</summary>
    public enum TerminalProgressKind
    {
        /// <summary>No progress being reported.</summary>
        None,

        /// <summary>OSC state 1: normal running progress; <c>Percent</c> carries 0–100
        /// when the sequence had one.</summary>
        Normal,

        /// <summary>OSC state 2: the tracked operation failed.</summary>
        Error,

        /// <summary>OSC state 3: running, no percentage known.</summary>
        Indeterminate,

        /// <summary>OSC state 4: paused.</summary>
        Paused,
    }

    /// <summary>One decoded OSC 9;4 report. <see langword="null"/> means "no indication".</summary>
    public readonly record struct TerminalProgressReport(TerminalProgressKind Kind, byte? Percent)
    {
        /// <summary>
        /// Maps the parser's raw (state, percent) pair for states 1–4. Returns
        /// <see langword="null"/> for state 0 and for unknown states — the caller must
        /// distinguish the two before treating null as "no report": state 0 withdraws
        /// the indication, an unknown state is ignored (forward compatibility with
        /// clients speaking a newer dialect). Percent is already clamped to 0–100 by
        /// the parser, and is meaningless for Indeterminate.
        /// </summary>
        public static TerminalProgressReport? FromOsc(int state, int? percent) => state switch
        {
            1 => new TerminalProgressReport(TerminalProgressKind.Normal, (byte?)percent),
            2 => new TerminalProgressReport(TerminalProgressKind.Error, (byte?)percent),
            3 => new TerminalProgressReport(TerminalProgressKind.Indeterminate, null),
            4 => new TerminalProgressReport(TerminalProgressKind.Paused, (byte?)percent),
            _ => null,
        };
    }

    /// <summary>
    /// Pure presentation decisions for OSC 9;4 progress (issue #271): the horizontal
    /// tab-title marker suffix, the vertical header's bar geometry, and the Windows
    /// taskbar mapping. No Avalonia types so the tests stay plain [Fact]s (same split as
    /// <see cref="TabStatusPresentation"/>); the window applies these results on its
    /// existing visual-refresh pass.
    /// </summary>
    internal static class TabProgressPresentation
    {
        // TBPFLAG values (shobjidl_core.h), mirrored here so the mapping stays in the
        // testable pure class instead of the interop type.
        internal const int TaskbarFlagNoProgress = 0;
        internal const int TaskbarFlagIndeterminate = 1;
        internal const int TaskbarFlagNormal = 2;
        internal const int TaskbarFlagError = 4;
        internal const int TaskbarFlagPaused = 8;

        /// <summary>
        /// Compact trailing suffix for a horizontal tab title; empty when there is no
        /// progress. " 42%", " ✖ 42%", " ⏸", " ⋯" — the same single-line, always-trailing
        /// contract <c>GetAttentionMarkerSuffix</c> gives bell/activity/agent markers.
        /// </summary>
        internal static string FormatMarkerSuffix(TerminalProgressReport? report)
        {
            if (report is not { } r || r.Kind == TerminalProgressKind.None)
            {
                return string.Empty;
            }

            string glyph = r.Kind switch
            {
                TerminalProgressKind.Error => " ✖",
                TerminalProgressKind.Paused => " ⏸",
                _ => string.Empty,
            };

            // Normal without a percentage has nothing numeric to show; "⋯" keeps the
            // "something is running" signal the Indeterminate state gets.
            return r.Percent is { } p ? $"{glyph} {p}%" : (glyph.Length > 0 ? glyph : " ⋯");
        }

        /// <summary>
        /// (visible, indeterminate, 0–100 value) for the vertical header's thin progress
        /// bar. Indeterminate (and a Normal report with no percentage) renders as the
        /// bar's indeterminate mode rather than a fake percentage.
        /// </summary>
        internal static (bool Visible, bool IsIndeterminate, double Value) ResolveBar(TerminalProgressReport? report)
        {
            if (report is not { } r || r.Kind == TerminalProgressKind.None)
            {
                return (false, false, 0);
            }

            return r.Kind switch
            {
                TerminalProgressKind.Indeterminate => (true, true, 0),
                _ => r.Percent is { } p ? (true, false, p) : (true, true, 0),
            };
        }

        /// <summary>
        /// (TBPFLAG, 0–100 value) to feed ITaskbarList3 for the selected tab's progress;
        /// (NoProgress, null) clears the taskbar state.
        /// </summary>
        internal static (int Flag, int? Value) ResolveTaskbar(TerminalProgressReport? report)
        {
            if (report is not { } r || r.Kind == TerminalProgressKind.None)
            {
                return (TaskbarFlagNoProgress, null);
            }

            return r.Kind switch
            {
                TerminalProgressKind.Normal => (TaskbarFlagNormal, r.Percent ?? 0),
                TerminalProgressKind.Error => (TaskbarFlagError, r.Percent ?? 0),
                TerminalProgressKind.Paused => (TaskbarFlagPaused, r.Percent ?? 0),
                TerminalProgressKind.Indeterminate => (TaskbarFlagIndeterminate, null),
                _ => (TaskbarFlagNoProgress, null),
            };
        }
    }
}
