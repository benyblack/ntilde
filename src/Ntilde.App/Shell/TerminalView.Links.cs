using Avalonia.Input;

namespace Ntilde.Shell
{
    /// <summary>
    /// Links under the pointer: detecting them, underlining the hovered one, and opening one on Ctrl+click.
    /// </summary>
    public partial class TerminalView
    {
        private bool _enableLinkDetection = true;
        private readonly Ntilde.VT.Links.UrlDetector _urlDetector = new Ntilde.VT.Links.UrlDetector();

        /// <summary>
        /// Decides whether a link is clickable (hover) and carries out a Ctrl+click on one. Hover and
        /// click both ask this one object, so a link that would not open never underlines. Settable
        /// so tests can route links through a recording environment instead of launching anything.
        /// </summary>
        internal TerminalLinkOpener LinkOpener { get; set; } = TerminalLinkOpener.Default;

        // Hovered link overlay state (transient UI state, never written to the buffer).
        private HoveredLink? _hoveredLink;

        /// <summary>The link under the pointer: every run of cells it covers, each underlined, and where it leads.</summary>
        private sealed record HoveredLink(IReadOnlyList<Ntilde.VT.Links.HyperlinkRun> Runs, string Uri)
        {
            public bool SameAs(HoveredLink? other) =>
                other is not null && other.Uri == Uri && other.Runs.SequenceEqual(Runs);
        }

        /// <summary>The link currently underlined under the pointer, if any.</summary>
        internal string? HoveredLinkUriForTest => _hoveredLink?.Uri;

        /// <summary>The underlined runs of the hovered link, in reading order; empty when there is none.</summary>
        internal IReadOnlyList<Ntilde.VT.Links.HyperlinkRun> HoveredLinkRunsForTest =>
            _hoveredLink?.Runs ?? System.Array.Empty<Ntilde.VT.Links.HyperlinkRun>();

        /// <summary>
        /// The OSC 8 anchor the hovered runs were found for, and the screen they were found on: where it starts, its size,
        /// and the content generation. Every pointer move asks for the hovered link, so moving within one anchor on an
        /// unchanged screen reuses its runs instead of rescanning every visible row under the buffer's lock (Codex review of
        /// #632). Any write to the buffer is a new generation - output can rewrite part of an <c>id</c>-joined link while
        /// the rest stays the same anchor - so the next move looks it up again.
        /// </summary>
        private (Ntilde.VT.Links.Hyperlink Link, int DisplayStart, int Rows, int Cols, int Generation)? _hoveredOsc8Screen;

        /// <summary>Bumped by every <see cref="InvalidateBuffer"/>, from whichever thread wrote to the buffer.</summary>
        private int _contentGeneration;

        /// <summary>How many times the hovered OSC 8 link's runs were looked up on screen.</summary>
        internal int Osc8ScansForTest { get; private set; }

        // One-row memo so we only re-run detection when the pointer moves to a new row - or the buffer was written to since:
        // output that rewrote a URL on the row would otherwise keep the old one previewed (Codex review of #634).
        private int _hoverScanRow = -1;
        private int _hoverScanGeneration;
        private System.Collections.Generic.IReadOnlyList<Ntilde.VT.Links.LinkSpan> _hoverScanSpans =
            System.Array.Empty<Ntilde.VT.Links.LinkSpan>();
        private int[] _hoverScanMap = System.Array.Empty<int>();
        private int[] _hoverScanEndMap = System.Array.Empty<int>();

        internal void UpdateHoveredLink(Avalonia.Point position)
        {
            if (_buffer == null) return;

            var (absRow, col) = ScreenToTerminal(position);

            // 1) Explicit OSC 8 link on this cell always takes precedence.
            Ntilde.VT.Links.Hyperlink? osc8 = _buffer.GetHyperlinkIdentityAbsolute(col, absRow);
            if (osc8 != null && !string.IsNullOrWhiteSpace(osc8.Uri))
            {
                // Only show as clickable if it would actually open (the click asks the same
                // LinkOpener), so non-openable targets (ftp://, a remote or UNC file: link) don't
                // underline or show the hand cursor.
                if (!LinkOpener.IsActivatable(osc8.Uri))
                {
                    ClearHoveredLink();
                    return;
                }

                var screen = (osc8, DisplayStart: Math.Max(0, _buffer.TotalLines - _buffer.Rows - ScrollOffset), _buffer.Rows, _buffer.Cols,
                    Generation: Volatile.Read(ref _contentGeneration));
                if (_hoveredLink is not null && _hoveredOsc8Screen is { } shown && ReferenceEquals(shown.Link, osc8)
                    && (shown.DisplayStart, shown.Rows, shown.Cols, shown.Generation) == (screen.DisplayStart, screen.Rows, screen.Cols, screen.Generation))
                {
                    return; // the same anchor on the same screen: its runs are already underlined
                }

                SetHoveredLink(new HoveredLink(Osc8RunsOnScreen(osc8, absRow, screen.DisplayStart), osc8.Uri));
                _hoveredOsc8Screen = screen;
                return;
            }

            // 2) Auto-detected link, if detection is enabled.
            if (_enableLinkDetection)
            {
                int generation = Volatile.Read(ref _contentGeneration);
                if (absRow != _hoverScanRow || generation != _hoverScanGeneration)
                {
                    var (text, map) = Ntilde.VT.Links.RowTextExtractor.Extract(_buffer, absRow, out int[] endMap);
                    _hoverScanSpans = _urlDetector.Detect(text);
                    _hoverScanMap = map;
                    _hoverScanEndMap = endMap;
                    _hoverScanRow = absRow;
                    _hoverScanGeneration = generation;
                }

                foreach (var span in _hoverScanSpans)
                {
                    var (startCol, endCol) = Ntilde.VT.Links.RowTextExtractor.SpanToColumns(span, _hoverScanMap, _hoverScanEndMap);
                    if (col >= startCol && col <= endCol)
                    {
                        // Mirror the click: don't underline links that can't open.
                        if (LinkOpener.IsActivatable(span.Uri))
                            SetHoveredLink(new HoveredLink([new Ntilde.VT.Links.HyperlinkRun(absRow, startCol, endCol)], span.Uri));
                        else
                            ClearHoveredLink();
                        return;
                    }
                }
            }

            ClearHoveredLink();
        }

        /// <summary>
        /// Every run of cells of the OSC 8 anchor <paramref name="link"/> on screen, so the whole link underlines
        /// rather than the one cell under the pointer (#95 gap 3). It used to be one cell, while a URL the terminal
        /// merely guessed at by regex got its full span - the program's explicit link got the worse treatment. Then
        /// it was the run on the hovered row, which left the part a long link wrapped onto, and the parts an
        /// <c>id</c> joins, plain. Same anchor means the same identity instance, not an equal URI: two adjacent links
        /// to one target are still two links. Runs on rows out of view are not drawn, so not looked for.
        /// </summary>
        private IReadOnlyList<Ntilde.VT.Links.HyperlinkRun> Osc8RunsOnScreen(Ntilde.VT.Links.Hyperlink link, int hoveredAbsRow, int displayStart)
        {
            Osc8ScansForTest++;
            return _buffer!.FindHyperlinkRuns(
                link,
                Math.Min(displayStart, hoveredAbsRow),
                Math.Max(displayStart + _buffer.Rows - 1, hoveredAbsRow));
        }

        /// <summary>
        /// What Render draws for the hovered link: a 1-pixel bar under each of its runs that is on one of the
        /// <paramref name="rows"/> rows from <paramref name="displayStart"/>.
        /// </summary>
        internal IEnumerable<Avalonia.Rect> HoveredLinkUnderlines(int displayStart, int rows)
        {
            if (_hoveredLink is not { } link || _metrics.CellWidth <= 0 || _metrics.CellHeight <= 0) yield break;

            foreach (Ntilde.VT.Links.HyperlinkRun run in link.Runs)
            {
                int visualRow = run.AbsRow - displayStart;
                if (visualRow < 0 || visualRow >= rows) continue;

                yield return new Avalonia.Rect(
                    run.StartCol * _metrics.CellWidth,
                    (visualRow + 1) * _metrics.CellHeight - 1.0,
                    (run.EndCol - run.StartCol + 1) * _metrics.CellWidth,
                    1.0);
            }
        }

        private void SetHoveredLink(HoveredLink link)
        {
            _hoveredOsc8Screen = null; // an OSC 8 hover sets it again, once its runs are underlined
            if (link.SameAs(_hoveredLink)) return; // no change -> no repaint
            if (_hoveredLink?.Uri != link.Uri)
            {
                Avalonia.Controls.ToolTip.SetTip(this, LinkTargetTip(link.Uri, OperatingSystem.IsMacOS(), LinkOpener.ActionText(link.Uri) ?? "open"));
            }

            _hoveredLink = link;
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
            InvalidateVisual();
        }

        private void ClearHoveredLink()
        {
            _hoveredOsc8Screen = null;
            if (_hoveredLink == null) return;
            Avalonia.Controls.ToolTip.SetTip(this, null);
            _hoveredLink = null;
            Cursor = Avalonia.Input.Cursor.Default;
            InvalidateVisual();
        }

        /// <summary>The longest target the link tooltip shows; past it, the middle gives way to an ellipsis.</summary>
        internal const int LinkTargetTipMaxLength = 300;

        /// <summary>
        /// What the tooltip over a hovered link says (#95 gap 5): where it leads, so a link whose text says one place and whose
        /// target is another can be checked before it is followed, and what a click does (<paramref name="action"/>, from
        /// <see cref="TerminalLinkOpener.ActionText"/>). The target is shown as it is, so it cannot be disguised in turn: a
        /// character that reorders, hides or breaks text (a bidi override, a zero-width space, a line separator) is shown as a
        /// visible escape, a control character is dropped, and a target too long to read keeps its start and its end. When that alone could hide where
        /// it leads - a user-info part long enough to push the host out of sight, a cut, or an international host that a
        /// look-alike letter could fake - the host it really reaches is named on a line of its own, in its ASCII form
        /// (CodeRabbit on #634).
        /// </summary>
        internal static string LinkTargetTip(string uri, bool macOS, string action)
        {
            var shown = new System.Text.StringBuilder(uri.Length);
            foreach (System.Text.Rune rune in uri.EnumerateRunes())
            {
                if (IsInvisibleFormatting(rune))
                {
                    if (rune.IsBmp) shown.Append(System.Globalization.CultureInfo.InvariantCulture, $"\\u{rune.Value:X4}");
                    else shown.Append(System.Globalization.CultureInfo.InvariantCulture, $"\\U{rune.Value:X8}");
                }
                else if (!System.Text.Rune.IsControl(rune))
                {
                    shown.Append(rune.ToString());
                }
            }

            const int Tail = 99;
            bool cut = shown.Length > LinkTargetTipMaxLength;
            string target = cut
                ? string.Concat(shown.ToString(0, LinkTargetTipMaxLength - Tail - 1), "\u2026", shown.ToString(shown.Length - Tail, Tail))
                : shown.ToString();
            string host = HostWorthNaming(uri, cut) is { } named ? $"\nHost: {named}" : string.Empty;
            return $"{target}{host}\n{(macOS ? "Cmd" : "Ctrl")}+click to {action}";
        }

        /// <summary>
        /// The host <paramref name="uri"/> reaches, in its ASCII (punycode) form, when the target as shown might not make it
        /// plain: it has a user-info part, it was <paramref name="cut"/>, or its host is an international one. Null otherwise,
        /// and for a target with no host (a local path).
        /// </summary>
        private static string? HostWorthNaming(string uri, bool cut)
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed) || string.IsNullOrEmpty(parsed.Host)) return null;

            string ascii = parsed.IdnHost;
            return parsed.UserInfo.Length > 0 || cut || !string.Equals(ascii, parsed.Host, StringComparison.OrdinalIgnoreCase)
                ? ascii
                : null;
        }

        /// <summary>
        /// Characters that change how the text around them reads without showing themselves: every format character (bidi
        /// controls, zero-width ones, the invisible tag characters past U+FFFF) and the line and paragraph separators, which a
        /// tooltip renders as line breaks - a forged "Host:" line otherwise (Codex review of #634).
        /// </summary>
        private static bool IsInvisibleFormatting(System.Text.Rune rune) =>
            System.Text.Rune.GetUnicodeCategory(rune) is System.Globalization.UnicodeCategory.Format
                or System.Globalization.UnicodeCategory.LineSeparator
                or System.Globalization.UnicodeCategory.ParagraphSeparator;

        private static bool IsLinkActivationModifier(KeyModifiers modifiers)
        {
            // Cmd (Meta) on macOS, Ctrl elsewhere — matches platform conventions.
            return OperatingSystem.IsMacOS()
                ? (modifiers & KeyModifiers.Meta) != 0
                : (modifiers & KeyModifiers.Control) != 0;
        }

        /// <summary>
        /// Ctrl+click (Cmd+click on macOS) on a link at <paramref name="position"/>: true when a link
        /// was opened and the click is consumed, false to let it fall through to selection.
        /// </summary>
        internal bool TryActivateLinkAt(Avalonia.Point position, KeyModifiers modifiers)
        {
            if (!IsLinkActivationModifier(modifiers) || _buffer == null) return false;

            var (row, col) = ScreenToTerminal(position);
            string? link = ResolveLinkAt(row, col);

            // A click opens what the tooltip previewed, never something else (Codex reviews of #634): output that rewrote the link
            // under a pointer that has not moved since leaves the tip showing the old target, and a link that appeared under a
            // pointer resting on plain text was never previewed at all. That click opens nothing; the tip is brought up to date
            // instead, so the next click opens what it now shows.
            if (!string.Equals(_hoveredLink?.Uri, link, StringComparison.Ordinal))
            {
                UpdateHoveredLink(position);
                return false;
            }

            return LinkOpener.TryOpen(link);
        }

        // Resolves the link under (absRow, col): OSC 8 first, then a detected span.
        private string? ResolveLinkAt(int absRow, int col)
        {
            if (_buffer == null) return null;

            string? osc8 = _buffer.GetHyperlinkAbsolute(col, absRow);
            if (!string.IsNullOrWhiteSpace(osc8)) return osc8;

            if (!_enableLinkDetection) return null;

            var (text, map) = Ntilde.VT.Links.RowTextExtractor.Extract(_buffer, absRow, out int[] endMap);
            foreach (var span in _urlDetector.Detect(text))
            {
                var (startCol, endCol) = Ntilde.VT.Links.RowTextExtractor.SpanToColumns(span, map, endMap);
                if (col >= startCol && col <= endCol) return span.Uri;
            }
            return null;
        }
    }
}
