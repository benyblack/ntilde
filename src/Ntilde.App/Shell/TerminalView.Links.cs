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
        /// The OSC 8 anchor the hovered runs were found for, and the screen they were found on. Every pointer move asks
        /// for the hovered link, so moving within one anchor on an unchanged screen reuses its runs instead of rescanning
        /// every visible row under the buffer's lock (Codex review of #632). New output moves the screen, and a redrawn
        /// link is a new anchor, so either is looked up again.
        /// </summary>
        private (Ntilde.VT.Links.Hyperlink Link, int DisplayStart, int Rows, int Cols)? _hoveredOsc8Screen;

        /// <summary>How many times the hovered OSC 8 link's runs were looked up on screen.</summary>
        internal int Osc8ScansForTest { get; private set; }

        // One-row memo so we only re-run detection when the pointer moves to a new row.
        private int _hoverScanRow = -1;
        private System.Collections.Generic.IReadOnlyList<Ntilde.VT.Links.LinkSpan> _hoverScanSpans =
            System.Array.Empty<Ntilde.VT.Links.LinkSpan>();
        private int[] _hoverScanMap = System.Array.Empty<int>();

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

                var screen = (osc8, DisplayStart: Math.Max(0, _buffer.TotalLines - _buffer.Rows - ScrollOffset), _buffer.Rows, _buffer.Cols);
                if (_hoveredLink is not null && _hoveredOsc8Screen is { } shown && ReferenceEquals(shown.Link, osc8)
                    && (shown.DisplayStart, shown.Rows, shown.Cols) == (screen.DisplayStart, screen.Rows, screen.Cols))
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
                if (absRow != _hoverScanRow)
                {
                    var (text, map) = Ntilde.VT.Links.RowTextExtractor.Extract(_buffer, absRow);
                    _hoverScanSpans = _urlDetector.Detect(text);
                    _hoverScanMap = map;
                    _hoverScanRow = absRow;
                }

                foreach (var span in _hoverScanSpans)
                {
                    var (startCol, endCol) = Ntilde.VT.Links.RowTextExtractor.SpanToColumns(span, _hoverScanMap);
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
            _hoveredLink = link;
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
            InvalidateVisual();
        }

        private void ClearHoveredLink()
        {
            _hoveredOsc8Screen = null;
            if (_hoveredLink == null) return;
            _hoveredLink = null;
            Cursor = Avalonia.Input.Cursor.Default;
            InvalidateVisual();
        }

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
            return LinkOpener.TryOpen(ResolveLinkAt(row, col));
        }

        // Resolves the link under (absRow, col): OSC 8 first, then a detected span.
        private string? ResolveLinkAt(int absRow, int col)
        {
            if (_buffer == null) return null;

            string? osc8 = _buffer.GetHyperlinkAbsolute(col, absRow);
            if (!string.IsNullOrWhiteSpace(osc8)) return osc8;

            if (!_enableLinkDetection) return null;

            var (text, map) = Ntilde.VT.Links.RowTextExtractor.Extract(_buffer, absRow);
            foreach (var span in _urlDetector.Detect(text))
            {
                var (startCol, endCol) = Ntilde.VT.Links.RowTextExtractor.SpanToColumns(span, map);
                if (col >= startCol && col <= endCol) return span.Uri;
            }
            return null;
        }
    }
}
