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
        private (int AbsRow, int StartCol, int EndCol, string Uri)? _hoveredLink;

        /// <summary>The link currently underlined under the pointer, if any.</summary>
        internal string? HoveredLinkUriForTest => _hoveredLink?.Uri;

        /// <summary>The underlined columns of the hovered link, if any.</summary>
        internal (int StartCol, int EndCol)? HoveredLinkColumnsForTest =>
            _hoveredLink is { } link ? (link.StartCol, link.EndCol) : null;

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
                if (LinkOpener.IsActivatable(osc8.Uri))
                {
                    var (startCol, endCol) = Osc8RunAround(osc8, col, absRow);
                    SetHoveredLink((absRow, startCol, endCol, osc8.Uri));
                }
                else
                    ClearHoveredLink();
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
                            SetHoveredLink((absRow, startCol, endCol, span.Uri));
                        else
                            ClearHoveredLink();
                        return;
                    }
                }
            }

            ClearHoveredLink();
        }

        /// <summary>
        /// The columns of the run of cells around <paramref name="col"/> that belong to the same OSC 8 anchor,
        /// so the whole link underlines rather than the one cell under the pointer (#95 gap 3). It used to be
        /// one cell, while a URL the terminal merely guessed at by regex got its full span - the program's
        /// explicit link got the worse treatment. Same anchor means the same identity instance, not an equal
        /// URI: two adjacent links to one target are still two links. A link wrapped onto the next row
        /// underlines on the hovered row only.
        /// </summary>
        private (int StartCol, int EndCol) Osc8RunAround(Ntilde.VT.Links.Hyperlink link, int col, int absRow)
        {
            int start = col;
            while (start > 0 && ReferenceEquals(_buffer!.GetHyperlinkIdentityAbsolute(start - 1, absRow), link))
            {
                start--;
            }

            int end = col;
            while (end < _buffer!.Cols - 1 && ReferenceEquals(_buffer.GetHyperlinkIdentityAbsolute(end + 1, absRow), link))
            {
                end++;
            }

            return (start, end);
        }

        private void SetHoveredLink((int AbsRow, int StartCol, int EndCol, string Uri) link)
        {
            if (_hoveredLink.Equals(link)) return; // no change -> no repaint
            _hoveredLink = link;
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
            InvalidateVisual();
        }

        private void ClearHoveredLink()
        {
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
