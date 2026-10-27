using Avalonia.Controls;
using Avalonia.Input.Platform;
using Ntilde.VT;

namespace Ntilde.Shell
{
    /// <summary>
    /// Selection and the clipboard: copying the selection, and selecting a word or a line.
    /// </summary>
    public partial class TerminalView
    {
        /// <summary>
        /// Test-only seam for exercising the Ctrl+C-with-selection carve-out (both the legacy
        /// path and its interaction with the kitty keyboard protocol) without driving real
        /// pointer events to build a selection.
        /// </summary>
        internal void SetSelectionForTest(int startRow, int startCol, int endRow, int endCol)
        {
            _selection.Start = (startRow, startCol);
            _selection.End = (endRow, endCol);
            _selection.IsActive = true;
        }

        // Selection state
        private readonly SelectionState _selection = new SelectionState();
        private bool _isSelecting = false;

        /// <summary>
        /// Copies selected text to clipboard.
        /// </summary>
        public async Task<bool> CopySelectionToClipboard()
        {
            if (!_selection.IsActive || _buffer == null)
                return false;

            try
            {
                string text;
                _buffer.Lock.EnterReadLock();
                try { text = _selection.GetSelectedText(_buffer); }
                finally { _buffer.Lock.ExitReadLock(); }

                if (!string.IsNullOrEmpty(text))
                {
                    var topLevel = TopLevel.GetTopLevel(this);
                    if (topLevel?.Clipboard != null)
                    {
                        await topLevel.Clipboard.SetTextAsync(text);
                        return true;
                    }
                }
            }
            catch
            {
                // Clipboard operations can fail
            }

            return false;
        }

        /// <summary>
        /// Sets the system clipboard to the given text, sharing the <see cref="TopLevel"/>
        /// clipboard access path with <see cref="CopySelectionToClipboard"/>. Used by OSC 52
        /// clipboard-write handling (issue #268); the caller (TerminalPane) is responsible for
        /// the settings gate and base64 decoding, this method only performs the actual write.
        /// </summary>
        public async Task<bool> SetClipboardTextAsync(string text)
        {
            try
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel?.Clipboard != null)
                {
                    await topLevel.Clipboard.SetTextAsync(text);
                    return true;
                }
            }
            catch
            {
                // Clipboard operations can fail
            }

            return false;
        }

        /// <summary>
        /// Returns selected text without copying to clipboard.
        /// </summary>
        public string? GetSelectedText()
        {
            if (!_selection.IsActive || _buffer == null)
                return null;

            _buffer.Lock.EnterReadLock();
            try { return _selection.GetSelectedText(_buffer); }
            finally { _buffer.Lock.ExitReadLock(); }
        }

        /// <summary>
        /// Clears the current selection.
        /// </summary>
        public void ClearSelection()
        {
            _selection.Clear();
            InvalidateVisual();
        }

        /// <summary>
        /// Checks if there's an active selection.
        /// </summary>
        public bool HasSelection() => _selection.IsActive;

        /// <summary>
        /// Selects a word at the given position (double-click behavior).
        /// </summary>
        private void SelectWord(int row, int col)
        {
            if (_buffer == null) return;

            int startCol = col;
            int endCol = col;

            // ScreenToTerminal now returns absolute rows.
            // But SelectWord is usually called from visual clicks.
            // ScreenToTerminal handles the conversion, so 'row' passed here IS absolute row.
            // GetCellAbsolute is needed.

            _buffer.Lock.EnterReadLock();
            try
            {
                // Find word boundaries (non-whitespace characters)
                while (startCol > 0 && !IsWhitespace(_buffer.GetCellAbsolute(startCol - 1, row).Character))
                    startCol--;

                while (endCol < _buffer.Cols - 1 && !IsWhitespace(_buffer.GetCellAbsolute(endCol + 1, row).Character))
                    endCol++;
            }
            finally { _buffer.Lock.ExitReadLock(); }

            _selection.Start = (row, startCol);
            _selection.End = (row, endCol);
            _selection.IsActive = true;
        }

        /// <summary>
        /// Selects an entire line (triple-click behavior).
        /// </summary>
        private void SelectLine(int row)
        {
            if (_buffer == null) return;

            _selection.Start = (row, 0);
            _selection.End = (row, _buffer.Cols - 1);
            _selection.IsActive = true;
        }

        /// <summary>
        /// Checks if a character is whitespace.
        /// </summary>
        private static bool IsWhitespace(char c)
        {
            return char.IsWhiteSpace(c) || c == '\0';
        }
    }
}
