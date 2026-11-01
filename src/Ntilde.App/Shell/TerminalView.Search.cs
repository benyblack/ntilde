using Ntilde.VT;

namespace Ntilde.Shell
{
    /// <summary>
    /// Find in the buffer: the matches, moving between them, and scrolling one into view.
    /// </summary>
    public partial class TerminalView
    {
        // Search state
        private List<SearchMatch> _searchMatches = new List<SearchMatch>();
        private int _activeSearchIndex = -1;
        private string _searchQuery = string.Empty;
        private bool _searchUseRegex;
        private bool _searchCaseSensitive;

        public event Action<int, int>? SearchStateChanged;

        public void Search(string query, bool useRegex = false, bool caseSensitive = false)
        {
            if (_buffer == null) return;
            _searchQuery = query;
            _searchUseRegex = useRegex;
            _searchCaseSensitive = caseSensitive;
            _searchMatches = _buffer.FindMatches(query, useRegex, caseSensitive);
            _activeSearchIndex = _searchMatches.Count > 0 ? 0 : -1;

            if (_activeSearchIndex != -1)
            {
                ScrollToMatch(_searchMatches[_activeSearchIndex]);
            }

            SearchStateChanged?.Invoke(_activeSearchIndex + 1, _searchMatches.Count);
            InvalidateVisual();
        }

        public void NextMatch()
        {
            if (_searchMatches.Count == 0) return;
            _activeSearchIndex = (_activeSearchIndex + 1) % _searchMatches.Count;
            ScrollToMatch(_searchMatches[_activeSearchIndex]);
            SearchStateChanged?.Invoke(_activeSearchIndex + 1, _searchMatches.Count);
            InvalidateVisual();
        }

        public void PrevMatch()
        {
            if (_searchMatches.Count == 0) return;
            _activeSearchIndex = (_activeSearchIndex - 1 + _searchMatches.Count) % _searchMatches.Count;
            ScrollToMatch(_searchMatches[_activeSearchIndex]);
            SearchStateChanged?.Invoke(_activeSearchIndex + 1, _searchMatches.Count);
            InvalidateVisual();
        }

        public void ClearSearch()
        {
            _searchQuery = string.Empty;
            _searchMatches.Clear();
            _activeSearchIndex = -1;
            SearchStateChanged?.Invoke(0, 0);
            InvalidateVisual();
        }

        private void RefreshSearchAfterResize()
        {
            if (_buffer == null || string.IsNullOrEmpty(_searchQuery)) return;
            _searchMatches = _buffer.FindMatches(_searchQuery, _searchUseRegex, _searchCaseSensitive);
            _activeSearchIndex = _searchMatches.Count == 0 ? -1 : Math.Clamp(_activeSearchIndex, 0, _searchMatches.Count - 1);
            if (_activeSearchIndex >= 0) ScrollToMatch(_searchMatches[_activeSearchIndex]);
            SearchStateChanged?.Invoke(_activeSearchIndex + 1, _searchMatches.Count);
            InvalidateVisual();
        }

        private void ScrollToMatch(SearchMatch match)
        {
            if (_buffer == null) return;

            int totalLines = _buffer.TotalLines;
            int viewportRows = _buffer.Rows;

            // match.AbsRow is 0-indexed from top of scrollback
            // Current viewport shows [totalLines - viewportRows - _scrollOffset, totalLines - _scrollOffset]

            int viewTop = totalLines - viewportRows - _scrollOffset;
            int viewBottom = totalLines - _scrollOffset;

            int matchRows = match.LastAbsRow - match.AbsRow + 1;
            if (match.SpanCount > 1 && matchRows <= viewportRows)
            {
                if (match.AbsRow < viewTop || match.LastAbsRow >= viewBottom)
                {
                    int top = Math.Clamp(match.AbsRow - (viewportRows - matchRows) / 2, 0, totalLines - viewportRows);
                    ScrollOffset = totalLines - viewportRows - top;
                }
                return;
            }

            if (match.AbsRow < viewTop || match.AbsRow >= viewBottom)
            {
                // Put match in the middle if possible
                int newScrollOffset = totalLines - match.AbsRow - (viewportRows / 2);
                ScrollOffset = Math.Max(0, Math.Min(newScrollOffset, totalLines - viewportRows));
            }
        }
    }
}
