using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ntilde.VT.Storage;

namespace Ntilde.VT
{
    public partial class TerminalBuffer
    {
        private sealed class ReflowEngineContext
        {
            private readonly SavedCursorStates _savedCursors;
            private TerminalRow[] _viewport;
            private ScrollbackPages _scrollback;
            private readonly List<TerminalImage> _images;
            // Pruned images retire their handles through the owning buffer's queue; the
            // reflow works on a copy of the list reference, so it needs the buffer back-reference.
            private readonly TerminalBuffer _source;
            private readonly bool _isAltScreen;
            private readonly TerminalTheme Theme;
            private readonly long _maxScrollbackBytes;
            private int _cursorRow;
            private int _cursorCol;
            private readonly Func<string, int> _getGraphemeWidth;

            // Shell-integration marks carried across the reflow. See TerminalBuffer's
            // CommandStartMark for why they live on the buffer at all. Each is remapped by
            // logical-line index + offset-in-logical-line, exactly like the cursor and the
            // saved cursors, and cleared when it cannot be placed.
            private ShellIntegrationMark? _commandStartMark;
            private ShellIntegrationMark? _commandOutputStartMark;

            // The values read at construction, kept so Apply can compare-and-swap. The parser
            // runs on the PTY thread and can land a fresh OSC 133;B while the reflow is working;
            // writing the re-anchored old mark over it would leave the readers pointing at the
            // previous prompt with perfectly valid-looking coordinates, which is worse than the
            // bug being fixed. If the slot moved under us, the newer mark wins untouched.
            private readonly ShellIntegrationMark? _observedCommandStartMark;
            private readonly ShellIntegrationMark? _observedCommandOutputStartMark;

            public ReflowEngineContext(TerminalBuffer source)
            {
                _savedCursors = source._savedCursors;
                _viewport = source._viewport;
                _scrollback = source._scrollback;
                _commandStartMark = _observedCommandStartMark = source.CommandStartMark;
                _commandOutputStartMark = _observedCommandOutputStartMark = source.CommandOutputStartMark;
                _maxScrollbackBytes = source.MaxScrollbackBytes;
                _images = source._images;
                _source = source;
                _isAltScreen = source._isAltScreen;
                Theme = source.Theme;
                _cursorRow = source._cursorRow;
                _cursorCol = source._cursorCol;
                // Lambda, not a method group: GetGraphemeWidth takes a span now (#165) and does not
                // match Func<string, int> directly. The reflow engine works in strings throughout.
                _getGraphemeWidth = text => source.GetGraphemeWidth(text);
            }

            public void Execute(int oldCols, int oldRows, int newCols, int newRows)
            {
                Reflow(oldCols, oldRows, newCols, newRows);
            }

            public void Apply(TerminalBuffer target)
            {
                target._viewport = _viewport;
                target._scrollback = _scrollback;
                target._cursorRow = _cursorRow;
                target._cursorCol = _cursorCol;
                if (target.CommandStartMark == _observedCommandStartMark)
                {
                    target.CommandStartMark = _commandStartMark;
                }

                if (target.CommandOutputStartMark == _observedCommandOutputStartMark)
                {
                    target.CommandOutputStartMark = _commandOutputStartMark;
                }
            }

            /// <summary>
            /// Physical row index (scrollback rows + viewport row) a tracked mark currently
            /// names, or <c>-1</c> when the mark is not a live main-screen mark.
            /// </summary>
            /// <remarks>
            /// <c>AbsoluteRow - TotalRowsEvicted</c> rather than <c>Row</c>: eviction may have
            /// shifted the row since capture, and the absolute form is the one that survives it.
            /// An alt-screen mark shares no numbering with the main screen the reflow rebuilds,
            /// and a mark from another epoch is already meaningless, so both are refused here
            /// rather than mapped to a plausible wrong row.
            /// </remarks>
            private int GetTrackedMarkPhysicalRow(ShellIntegrationMark? mark)
            {
                // Not gated on _isAltScreen: the reflow rebuilds the *main* screen even when the
                // alt screen is the one on display (Resize swaps it in), and a main-screen mark
                // names main-screen content either way.
                if (mark is not ShellIntegrationMark live || live.IsAltScreen)
                {
                    return -1;
                }

                if (live.Generation != _scrollback.Generation)
                {
                    return -1;
                }

                long derived = live.AbsoluteRow - _scrollback.TotalRowsEvicted;
                if (derived < 0 || derived > int.MaxValue)
                {
                    return -1;
                }

                return (int)derived;
            }

            /// <summary>
            /// Rebuilds a tracked mark from the row/column the reflow placed it at, or returns
            /// <see langword="null"/> when it could not be placed.
            /// </summary>
            /// <param name="newPhysRow">Index into the reflowed row list, or <c>-1</c>.</param>
            /// <param name="newPhysCol">Column within that row.</param>
            /// <param name="discardedRows">
            /// Rows the rebuilt scrollback evicted while being refilled. Subtracting it converts
            /// a reflowed-row index into the buffer's current addressing space, which is the same
            /// conversion the image re-anchoring below performs.
            /// </param>
            private ShellIntegrationMark? RebuildTrackedMark(
                ShellIntegrationMark? original, int newPhysRow, int newPhysCol, int discardedRows, int newCols)
            {
                if (original is not ShellIntegrationMark live || newPhysRow < 0)
                {
                    return null;
                }

                int row = newPhysRow - discardedRows;
                if (row < 0)
                {
                    return null; // the marked line was discarded while the scrollback was rebuilt
                }

                // A mark parked one past the last column has no cell to name once the row is
                // re-wrapped; refusing is the same answer the readers would give.
                if (newPhysCol < 0 || newPhysCol >= newCols)
                {
                    return null;
                }

                return live with
                {
                    Row = row,
                    Column = newPhysCol,
                    // TotalRowsEvicted of the rebuilt store is exactly discardedRows, so
                    // AbsoluteRow = discardedRows + row collapses to the reflowed index.
                    AbsoluteRow = newPhysRow,
                    Generation = _scrollback.Generation,
                };
            }

            private int GetGraphemeWidth(string textElement)
            {
                return _getGraphemeWidth(textElement);
            }

            // Streams the reflow one logical line at a time. It used to materialise the whole
            // scrollback four times over - a TerminalRow copy of every old row, one (cell, grapheme,
            // link) entry per cell in a pooled scratch array sized rows x the wider width (rounded up
            // to a power of two by the pool, and retained by it afterwards), a TerminalRow per
            // re-wrapped row, and the rebuilt pages: about 1 GB per pane at 100k lines. Now old
            // scrollback rows are read in place through one reused row, only the logical line being
            // assembled is buffered, and re-wrapped history rows go straight into the new pages
            // through one reused row. Only lines that start in the old viewport - at most a screen's
            // worth - become TerminalRow objects, because they may become the new viewport.
            private void Reflow(int oldCols, int oldRows, int newCols, int newRows)
            {
                try
                {
                    if (newCols <= 0 || newRows <= 0) return;

                    int oldScrollbackCount = _scrollback.Count;

                    // 1. Capture Cursor Content Pre-Resize
                    int absCursorPhysicalIdx = oldScrollbackCount + _cursorRow;
                    int cursorLogicalIdx = -1;
                    int cursorInLogicalOffset = -1;

                    int absMainSavedIdx = oldScrollbackCount + _savedCursors.Main.Row;
                    int mainSavedLogicalIdx = -1;
                    int mainSavedInLogicalOffset = -1;

                    int absAltSavedIdx = oldScrollbackCount + _savedCursors.Alt.Row;
                    int altSavedLogicalIdx = -1;
                    int altSavedInLogicalOffset = -1;

                    // Shell-integration marks ride through on the same (logical line, offset)
                    // identity the cursors use. -1 means "not a live main-screen mark", which
                    // short-circuits every tracking branch below and leaves the slot cleared.
                    int absCommandStartIdx = GetTrackedMarkPhysicalRow(_commandStartMark);
                    int commandStartLogicalIdx = -1;
                    int commandStartInLogicalOffset = -1;

                    int absOutputStartIdx = GetTrackedMarkPhysicalRow(_commandOutputStartMark);
                    int outputStartLogicalIdx = -1;
                    int outputStartInLogicalOffset = -1;

                    // 2. Physical Extraction with Padding Trim

                    // Calculate how many viewport rows have content
                    int lastActiveVpRow = -1;
                    int actualVpLen = _viewport.Length;
                    for (int i = 0; i < Math.Min(oldRows, actualVpLen); i++)
                    {
                        var row = _viewport[i];
                        bool isEmpty = true;
                        foreach (var cell in row.Cells)
                        {
                            if (cell.Character != ' ' && cell.Character != '\0' || !cell.IsDefaultBackground)
                            {
                                isEmpty = false;
                                break;
                            }
                        }
                        if (!isEmpty || i <= _cursorRow || i == _savedCursors.Main.Row || i == _savedCursors.Alt.Row) lastActiveVpRow = i;
                    }

                    int vpRowsToTake = lastActiveVpRow + 1;
                    int totalPhysRows = oldScrollbackCount + vpRowsToTake;

                    // Logical lines that start before this physical row are history: every row they
                    // flow into stays in the scrollback. Lines from here on are the old viewport's.
                    int splitPhysIndex = oldScrollbackCount;

                    // Image anchoring has always let the last logical line run to the end of the
                    // TerminalRow[] the previous implementation rented for the physical rows - the
                    // pool's bucket size, the next power of two that is at least 16 - so an image
                    // parked below the content anchored to that line only within that range and
                    // otherwise kept its coordinates. Reproduced exactly: which stray images move
                    // is a behaviour question, not part of making the reflow stream.
                    int lastLineEnd = totalPhysRows == 0
                        ? 0
                        : (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(totalPhysRows, 16));

                    // 3. Metadata-Aware Logical Reconstruction
                    // The hyperlink travels alongside the extended grapheme, not separately: both are
                    // column-keyed side tables, and reflow re-columns everything (#164 item 2a).
                    var line = new (TerminalCell Cell, string? ExtendedText, Links.Hyperlink? Hyperlink)[Math.Max(oldCols, newCols) * 2 + 16];
                    int lineLen = 0;
                    int lineStartPhys = -1;
                    int logicalIdx = 0;

                    // Main-screen images still to be anchored, and where the flow puts each one.
                    // Alt-screen images use viewport-relative CellY and must not be anchored to (and
                    // repositioned by) main logical lines.
                    var unanchoredImages = new List<TerminalImage>();
                    foreach (var img in _images)
                    {
                        if (img != null && !img.IsAltScreenImage) unanchoredImages.Add(img);
                    }
                    var imagePlacements = new List<(TerminalImage Image, int NewY, int NewX)>();
                    var lineAnchors = new List<(TerminalImage Image, int OffsetInLogicalLine)>();

                    // 5. Distribution state
                    var newScrollback = new ScrollbackPages(newCols, _sharedPagePool, _maxScrollbackBytes);
                    var activeRows = new List<TerminalRow>();
                    var historyRow = new TerminalRow(newCols, Theme.Foreground, Theme.Background);
                    var scrollbackRow = new TerminalRow(oldCols, Theme.Foreground, Theme.Background);
                    var def = new TerminalCell(' ', Theme.Foreground, Theme.Background, false, false, true, true);
                    int flowedRows = 0;
                    int historyRowCount = 0; // Tracks physical rows generated from original history

                    int newCursorPhysRow = -1;
                    int newCursorPhysCol = -1;
                    int newMainSavedPhysRow = -1;
                    int newMainSavedPhysCol = -1;
                    int newAltSavedPhysRow = -1;
                    int newAltSavedPhysCol = -1;
                    int newCommandStartPhysRow = -1;
                    int newCommandStartPhysCol = -1;
                    int newOutputStartPhysRow = -1;
                    int newOutputStartPhysCol = -1;

                    void EnsureLineCapacity(int needed)
                    {
                        if (needed <= line.Length) return;
                        Array.Resize(ref line, Math.Max(needed, line.Length * 2));
                    }

                    // A flowed row of a history line goes straight into the new pages; the pages
                    // keep the side-table maps by reference, so the reused row gets fresh ones.
                    void EmitRow(TerminalRow row, bool isHistory)
                    {
                        if (isHistory)
                        {
                            newScrollback.AppendRow(row.Cells, row.IsWrapped, row.GetExtendedTextMap(), row.GetHyperlinkMap());
                            row.ClearExtendedText();
                            row.ClearHyperlinks();
                            row.IsWrapped = false;
                        }
                        else
                        {
                            activeRows.Add(row);
                        }
                        flowedRows++;
                    }

                    // Re-wraps the logical line assembled in `line` at newCols. endPhysExclusive is
                    // where the next line starts; the last line extends to the end of the buffer.
                    void FlowLine(int i, int lineCount, int startPhys, int endPhysExclusive)
                    {
                        bool isHistory = startPhys < splitPhysIndex;
                        int startFlowIndex = flowedRows;

                        // 5b. Anchor Images to Logical Positions
                        lineAnchors.Clear();
                        for (int k = unanchoredImages.Count - 1; k >= 0; k--)
                        {
                            var img = unanchoredImages[k];
                            if (img.CellY >= startPhys && img.CellY < endPhysExclusive)
                            {
                                int rowOffset = img.CellY - startPhys;
                                lineAnchors.Add((img, rowOffset * oldCols + img.CellX));
                                unanchoredImages.RemoveAt(k);
                            }
                        }

                        if (lineCount == 0)
                        {
                            // If this is the WIPED prompt, place cursor here
                            if (i == cursorLogicalIdx) { newCursorPhysRow = flowedRows; newCursorPhysCol = 0; }
                            if (i == mainSavedLogicalIdx) { newMainSavedPhysRow = flowedRows; newMainSavedPhysCol = 0; }
                            if (i == altSavedLogicalIdx) { newAltSavedPhysRow = flowedRows; newAltSavedPhysCol = 0; }
                            if (i == commandStartLogicalIdx) { newCommandStartPhysRow = flowedRows; newCommandStartPhysCol = 0; }
                            if (i == outputStartLogicalIdx) { newOutputStartPhysRow = flowedRows; newOutputStartPhysCol = 0; }
                            if (isHistory)
                            {
                                for (int c = 0; c < newCols; c++) historyRow.Cells[c] = def;
                                EmitRow(historyRow, isHistory: true);
                            }
                            else
                            {
                                EmitRow(new TerminalRow(newCols, Theme.Foreground, Theme.Background), isHistory: false);
                            }
                        }
                        else
                        {
                            int processed = 0;
                            while (processed < lineCount)
                            {
                                int remaining = lineCount - processed;
                                int take = Math.Min(remaining, newCols);

                                // Prevent splitting a wide character across lines
                                if (take < remaining && take > 0 && line[processed + take - 1].Cell.IsWide)
                                {
                                    take--; // This row will end with a space, wide char moves to next row
                                }

                                // If take is 0 but we have remaining (newCols is 1 and we have a wide char),
                                // we're forced to just take it and let it be clipped, otherwise infinite loop.
                                if (take == 0 && remaining > 0) take = 1;

                                // Mapping
                                if (i == cursorLogicalIdx)
                                {
                                    if (cursorInLogicalOffset >= processed && cursorInLogicalOffset < processed + newCols)
                                    {
                                        newCursorPhysRow = flowedRows;
                                        newCursorPhysCol = cursorInLogicalOffset - processed;
                                    }
                                    else if (cursorInLogicalOffset == processed + newCols && remaining == newCols)
                                    {
                                        newCursorPhysRow = flowedRows;
                                        newCursorPhysCol = newCols;
                                    }
                                }

                                if (i == mainSavedLogicalIdx)
                                {
                                    if (mainSavedInLogicalOffset >= processed && mainSavedInLogicalOffset < processed + newCols)
                                    {
                                        newMainSavedPhysRow = flowedRows;
                                        newMainSavedPhysCol = mainSavedInLogicalOffset - processed;
                                    }
                                }

                                if (i == altSavedLogicalIdx)
                                {
                                    if (altSavedInLogicalOffset >= processed && altSavedInLogicalOffset < processed + newCols)
                                    {
                                        newAltSavedPhysRow = flowedRows;
                                        newAltSavedPhysCol = altSavedInLogicalOffset - processed;
                                    }
                                }

                                if (i == commandStartLogicalIdx)
                                {
                                    if (commandStartInLogicalOffset >= processed && commandStartInLogicalOffset < processed + newCols)
                                    {
                                        newCommandStartPhysRow = flowedRows;
                                        newCommandStartPhysCol = commandStartInLogicalOffset - processed;
                                    }
                                }

                                if (i == outputStartLogicalIdx)
                                {
                                    if (outputStartInLogicalOffset >= processed && outputStartInLogicalOffset < processed + newCols)
                                    {
                                        newOutputStartPhysRow = flowedRows;
                                        newOutputStartPhysCol = outputStartInLogicalOffset - processed;
                                    }
                                }

                                var row = isHistory ? historyRow : new TerminalRow(newCols, Theme.Foreground, Theme.Background);
                                for (int c = 0; c < take; c++)
                                {
                                    var entry = line[processed + c];
                                    row.Cells[c] = entry.Cell;
                                    row.SetExtendedText(c, entry.ExtendedText);
                                    row.SetHyperlink(c, entry.Hyperlink);
                                }

                                // Style-Aware Padding
                                // We use TRUE default style for padding, NOT the last character's style.
                                // This prevents "Background Leakage" (e.g. blue/green bars) when resizing.
                                for (int c = take; c < newCols; c++) row.Cells[c] = def;

                                row.IsWrapped = remaining > newCols;
                                EmitRow(row, isHistory);
                                processed += take;
                            }
                        }

                        // If this line belongs to history (before viewport start), add its generated rows to count
                        if (isHistory)
                        {
                            historyRowCount += flowedRows - startFlowIndex;
                        }

                        foreach (var (image, offsetInLine) in lineAnchors)
                        {
                            imagePlacements.Add((image, startFlowIndex + (offsetInLine / newCols), offsetInLine % newCols));
                        }
                    }

                    for (int i = 0; i < totalPhysRows; i++)
                    {
                        TerminalRow physRow;
                        if (i < oldScrollbackCount)
                        {
                            // Read the paged row in place: its cells into the one reused row, its side
                            // tables borrowed (the reflow only reads them).
                            _scrollback.GetRow(i).CopyTo(scrollbackRow.Cells);
                            scrollbackRow.IsWrapped = _scrollback.IsRowWrapped(i);
                            scrollbackRow.RestoreSideTables(_scrollback.GetExtendedTextMap(i), _scrollback.GetHyperlinkMap(i));
                            physRow = scrollbackRow;
                        }
                        else
                        {
                            int vpRow = i - oldScrollbackCount;
                            physRow = vpRow < actualVpLen ? _viewport[vpRow] : new TerminalRow(oldCols, Theme.Foreground, Theme.Background);
                        }

                        if (lineStartPhys == -1)
                        {
                            lineStartPhys = i;
                            lineLen = 0;
                        }

                        // One physical row adds at most its own cells plus a right-prompt fill up to newCols.
                        EnsureLineCapacity(lineLen + physRow.Cells.Length + newCols + 4);

                        // Cursor Tracking
                        if (i == absCursorPhysicalIdx)
                        {
                            cursorLogicalIdx = logicalIdx;
                            cursorInLogicalOffset = lineLen + _cursorCol;
                        }

                        if (i == absMainSavedIdx)
                        {
                            mainSavedLogicalIdx = logicalIdx;
                            mainSavedInLogicalOffset = lineLen + _savedCursors.Main.Col;
                        }

                        if (i == absAltSavedIdx)
                        {
                            altSavedLogicalIdx = logicalIdx;
                            altSavedInLogicalOffset = lineLen + _savedCursors.Alt.Col;
                        }

                        if (i == absCommandStartIdx && _commandStartMark is ShellIntegrationMark cs)
                        {
                            commandStartLogicalIdx = logicalIdx;
                            commandStartInLogicalOffset = lineLen + cs.Column;
                        }

                        if (i == absOutputStartIdx && _commandOutputStartMark is ShellIntegrationMark os)
                        {
                            outputStartLogicalIdx = logicalIdx;
                            outputStartInLogicalOffset = lineLen + os.Column;
                        }

                        int validLen = physRow.Cells.Length;

                        // HEURISTIC: TUI Border Protection
                        // If a line is marked as Wrapped, but it ends with a box-drawing character OR a colored background,
                        // it is likely a fixed-width TUI element that should NOT flow into the next line on resize.
                        bool ignoreWrap = false;
                        if (physRow.IsWrapped)
                        {
                            // 1. Check for TUI Background Fill (at the very edge)
                            // TUI apps often fill the background with a specific color (e.g. blue for MC).
                            // If the last cell has a non-default background AND is a SPACE, it likely hit the edge of a panel.
                            // We must NOT trigger this for regular text (e.g. "Hint" with black BG), or it won't reflow.
                            if (physRow.Cells.Length > 0)
                            {
                                // Scan backwards for the last actual content (skipping newly allocated nulls from resize)
                                TerminalCell lastCell = default;
                                bool found = false;
                                for (int k = physRow.Cells.Length - 1; k >= 0; k--)
                                {
                                    if (physRow.Cells[k].Character != '\0')
                                    {
                                        lastCell = physRow.Cells[k];
                                        found = true;
                                        break;
                                    }
                                }

                                if (found && lastCell.Character == ' ' && !lastCell.IsDefaultBackground)
                                {
                                    ignoreWrap = true;
                                }
                                else
                                {
                                    // 2. Check for Border Characters (ignoring trailing spaces)
                                    for (int k = physRow.Cells.Length - 1; k >= 0; k--)
                                    {
                                        var c = physRow.Cells[k];
                                        char ch = c.Character;
                                        if (ch != ' ' && ch != '\0')
                                        {
                                            // Vertical bars, corners, etc.
                                            // U+2500 to U+257F are Box Drawing.
                                            // U+2580 to U+259F are Block Elements (Full Block, Shades, etc.) used for scrollbars/shadows.
                                            // U+FF00 to U+FFEF are Halfwidth and Fullwidth Forms (includes Fullwidth Pipe U+FF5C).
                                            // '|' is standard vertical bar (U+007C).
                                            // '+' and '-' can be ASCII borders.
                                            // '>' is often used by MC to indicate horizontal scroll overflow.
                                            if (ch == '|' || ch == '+' || ch == '-' || ch == '>' ||
                                               (ch >= '─' && ch <= '╿') ||
                                               (ch >= '▀' && ch <= '▟') ||
                                               (ch >= '＀' && ch <= '￯'))
                                            {
                                                ignoreWrap = true;
                                            }
                                            // Special Case: MC Headers like ".[^]" often end with ']' or '^'.
                                            // These are text characters, so we can't protect them globally (would break text wrapping).
                                            // However, in TUI headers, they typically have a specific background color.
                                            // Also protect Arrows '↑' (U+2191) and '↓' (U+2193) which are sometimes used as sort indicators.
                                            else if ((ch == ']' || ch == '[' || ch == '^' || ch == '↑' || ch == '↓') && !c.IsDefaultBackground)
                                            {
                                                ignoreWrap = true;
                                            }
                                            break;
                                        }
                                    }
                                }
                            }
                        }

                        if (!physRow.IsWrapped || ignoreWrap)
                        {
                            // Smart trimming: Calculate last relevant content index
                            // Include cells that are non-space OR have non-default background
                            int lastContentIdx = -1;
                            // A hyperlink counts as content even on a blank cell. OSC 8 spans
                            // routinely include trailing spaces, and those columns carry no
                            // signal in the cell itself - trimming them here dropped their
                            // links for good, since everything past validLen never enters the
                            // logical stream. HasExtendedText is checked for the same reason.
                            bool rowHasLinks = physRow.GetHyperlinkMap() is { Count: > 0 };
                            for (int scan = 0; scan < physRow.Cells.Length; scan++)
                            {
                                var cell = physRow.Cells[scan];
                                if ((cell.Character != ' ' && cell.Character != '\0') || !cell.IsDefaultBackground || cell.HasExtendedText
                                    || (rowHasLinks && physRow.GetHyperlink(scan) != null))
                                {
                                    lastContentIdx = scan;
                                }
                            }

                            // Determine valid length based on content
                            if (lastContentIdx >= 0)
                            {
                                validLen = lastContentIdx + 1;
                            }
                            else
                            {
                                validLen = 0;
                            }

                            // Special case: Preserve padding up to the cursor if it's on this row
                            if (i == absCursorPhysicalIdx && _cursorCol > validLen)
                            {
                                validLen = _cursorCol;
                            }

                            // Same rule for a tracked shell mark: an OSC 133;B on a prompt whose
                            // tail is blank (every prompt that ends in a space, once the line is
                            // empty) would otherwise have its column trimmed out of the logical
                            // stream and the mark would be dropped by a resize that changed
                            // nothing about where the prompt ends.
                            if (i == absCommandStartIdx && _commandStartMark is ShellIntegrationMark csPad && csPad.Column > validLen)
                            {
                                validLen = csPad.Column;
                            }

                            if (i == absOutputStartIdx && _commandOutputStartMark is ShellIntegrationMark osPad && osPad.Column > validLen)
                            {
                                validLen = osPad.Column;
                            }
                        }

                        // Improved sparse row detection: Find the LARGEST contiguous gap
                        // This preserves middle content (e.g. "Left ... Middle ... Right")
                        bool isSparseRowRepositioned = false;
                        if (!physRow.IsWrapped && i >= Math.Max(0, absCursorPhysicalIdx - 2) && i <= absCursorPhysicalIdx)
                        {
                            // Find largest gap strictly BETWEEN content
                            // We need to know if the gap is followed by content, otherwise it's just trailing space
                            // Scan logic:
                            // 1. Identify all gaps.
                            // 2. Identify the gap that is:
                            //    a) Large (> 10)
                            //    b) Followed by content (not end of line)
                            //    c) The largest such gap in the row

                            int bestGapStart = -1;
                            int bestGapLength = 0;

                            int currentScanStart = -1;
                            int currentScanLength = 0;

                            // First we need to find the "end of row content" to ignore trailing spaces
                            int lastContentIndex = -1;
                            for (int scan = physRow.Cells.Length - 1; scan >= 0; scan--)
                            {
                                var cell = physRow.Cells[scan];
                                if (cell.Character != ' ' && cell.Character != '\0')
                                {
                                    lastContentIndex = scan;
                                    break;
                                }
                            }

                            if (lastContentIndex > 0)
                            {
                                // ONLY Scan up to lastContentIndex
                                // This ensures any gap we find implies there is content AFTER it.
                                for (int scan = 0; scan <= lastContentIndex; scan++)
                                {
                                    var cell = physRow.Cells[scan];
                                    bool isSpace = (cell.Character == ' ' || cell.Character == '\0');

                                    if (isSpace)
                                    {
                                        if (currentScanStart == -1) currentScanStart = scan;
                                        currentScanLength++;
                                    }
                                    else
                                    {
                                        if (currentScanStart != -1)
                                        {
                                            if (currentScanLength > bestGapLength)
                                            {
                                                bestGapLength = currentScanLength;
                                                bestGapStart = currentScanStart;
                                            }
                                            currentScanStart = -1;
                                            currentScanLength = 0;
                                        }
                                    }
                                }
                                // Check gap if content resumes exactly at lastContentIndex? handled by loop
                                // The loop stops AT lastContentIndex. If the character at lastContentIndex is content,
                                // the else block triggers and we check the gap before it. Correct.
                            }

                            // Determine threshold for gap
                            // Standard: 10 spaces
                            // Special: 2 spaces IF the content touches the right edge (implies a shrunk right-prompt)
                            bool isRightPinned = lastContentIndex == physRow.Cells.Length - 1;
                            int gapThreshold = isRightPinned ? 2 : 10;

                            if (bestGapLength >= gapThreshold)
                            {
                                // We found a split!
                                // Left+Middle = 0 .. bestGapStart (exclusive)
                                // Gap = bestGapStart .. bestGapStart + bestGapLength
                                // Right = bestGapStart + bestGapLength .. lastContentIndex (inclusive)

                                int gapStart = bestGapStart;
                                int gapEnd = bestGapStart + bestGapLength;
                                int rightStart = gapEnd;
                                int rightEnd = lastContentIndex;

                                // Extract Left+Middle
                                for (int k = 0; k < gapStart; k++)
                                {
                                    line[lineLen++] = (physRow.Cells[k], physRow.GetExtendedText(k), physRow.GetHyperlink(k));
                                }

                                // Calculate new position
                                int rightBlockWidth = rightEnd - rightStart + 1;
                                int newRightPos = newCols - rightBlockWidth;

                                int currentPos = lineLen; // This is effectively gapStart

                                if (newRightPos > currentPos + 2 && (newRightPos + rightBlockWidth) <= newCols)
                                {
                                    // Fill spaces
                                    var spaceFill = new TerminalCell(' ', Theme.Foreground, Theme.Background, false, false, true, true);
                                    for (int s = currentPos; s < newRightPos; s++)
                                    {
                                        line[lineLen++] = (spaceFill, null, null);
                                    }
                                    // Add right content
                                    for (int k = rightStart; k <= rightEnd; k++)
                                    {
                                        line[lineLen++] = (physRow.Cells[k], physRow.GetExtendedText(k), physRow.GetHyperlink(k));
                                    }
                                }
                                else
                                {
                                    // Truncate/Squish
                                    var spaceFill = new TerminalCell(' ', Theme.Foreground, Theme.Background, false, false, true, true);
                                    line[lineLen++] = (spaceFill, null, null);
                                    line[lineLen++] = (spaceFill, null, null);

                                    int available = newCols - lineLen;
                                    if (available > 0)
                                    {
                                        int take = Math.Min(available, rightBlockWidth);
                                        int startOffset = rightBlockWidth - take;
                                        for (int k = rightStart + startOffset; k <= rightEnd; k++)
                                            line[lineLen++] = (physRow.Cells[k], physRow.GetExtendedText(k), physRow.GetHyperlink(k));
                                    }
                                }
                                isSparseRowRepositioned = true;
                            }

                        }

                        // Normal processing if not sparse row or repositioning failed
                        if (!isSparseRowRepositioned)
                        {
                            for (int k = 0; k < validLen; k++)
                                line[lineLen++] = (physRow.Cells[k], physRow.GetExtendedText(k), physRow.GetHyperlink(k));
                        }

                        if (!physRow.IsWrapped || ignoreWrap)
                        {
                            FlowLine(logicalIdx, lineLen, lineStartPhys, i == totalPhysRows - 1 ? lastLineEnd : i + 1);
                            logicalIdx++;
                            lineStartPhys = -1;
                        }
                    }

                    if (lineStartPhys != -1)
                    {
                        FlowLine(logicalIdx, lineLen, lineStartPhys, lastLineEnd);
                        logicalIdx++;
                    }

                    // 6. Final Layout (Anchor-to-Top of Viewport)
                    // We want _scrollback to contain AT LEAST 'historyRowCount'.
                    // But if the remaining lines (viewport content) > newRows, we must push some of them to SB (Shrink).
                    int total = flowedRows;

                    // Base split: Everything that was history stays history.
                    int sbCount = historyRowCount;

                    // Shrink Adjustment: If active content doesn't fit in new viewport, overflow goes to SB.
                    int activeContentSize = total - historyRowCount;
                    if (activeContentSize > newRows)
                    {
                        sbCount += (activeContentSize - newRows);
                    }

                    // Ensure safety
                    sbCount = Math.Clamp(sbCount, 0, total);
                    int vpCount = total - sbCount;

                    // History rows are already in the new pages; the viewport's overflow follows them.
                    int overflowRows = sbCount - historyRowCount;
                    for (int i = 0; i < overflowRows; i++)
                    {
                        // Carry each reflowed row's side tables into the rebuilt
                        // scrollback; extended graphemes must survive reflow.
                        newScrollback.AppendRow(
                            activeRows[i].Cells,
                            activeRows[i].IsWrapped,
                            activeRows[i].GetExtendedTextMap(),
                            activeRows[i].GetHyperlinkMap());
                    }

                    int discardedRows = (int)newScrollback.TotalRowsEvicted;
                    var oldScrollback = _scrollback;
                    _scrollback = newScrollback;
                    oldScrollback.Clear();

                    // Re-anchor the shell-integration marks now that the new store exists: the
                    // rebuild reads _scrollback.Generation, which must be the *new* epoch. A mark
                    // the flow could not place comes back null.
                    _commandStartMark = RebuildTrackedMark(
                        _commandStartMark, newCommandStartPhysRow, newCommandStartPhysCol, discardedRows, newCols);
                    _commandOutputStartMark = RebuildTrackedMark(
                        _commandOutputStartMark, newOutputStartPhysRow, newOutputStartPhysCol, discardedRows, newCols);

                    // Fill viewport
                    // If vpCount < newRows (Growth), we will have empty space at the bottom (Top Anchoring).
                    _viewport = new TerminalRow[newRows];
                    int vIdx = 0;
                    for (int i = 0; i < vpCount; i++) _viewport[vIdx++] = activeRows[overflowRows + i];

                    // Pad remaining viewport rows (at the BOTTOM now)
                    while (vIdx < newRows)
                    {
                        _viewport[vIdx++] = new TerminalRow(newCols, Theme.Foreground, Theme.Background);
                    }

                    // 7. Restore Cursor
                    if (newCursorPhysRow != -1)
                    {
                        // newCursorPhysRow counts every flowed row; it may be in scrollback now.
                        if (newCursorPhysRow < sbCount)
                        {
                            // Cursor pushed to scrollback: clamp to the top of the viewport.
                            _cursorRow = 0;
                        }
                        else
                        {
                            _cursorRow = newCursorPhysRow - sbCount;
                        }
                        _cursorCol = Math.Clamp(newCursorPhysCol, 0, newCols);
                    }
                    else
                    {
                        _cursorRow = newRows - 1;
                        _cursorCol = 0;
                    }

                    // 8. Reposition Images
                    if (imagePlacements.Count > 0)
                    {
                        var placementByImage = new Dictionary<TerminalImage, (int NewY, int NewX)>(ReferenceEqualityComparer.Instance);
                        foreach (var (image, newY, newX) in imagePlacements) placementByImage[image] = (newY, newX);

                        for (int i = _images.Count - 1; i >= 0; i--)
                        {
                            var img = _images[i];
                            if (img == null || !placementByImage.TryGetValue(img, out var placement)) continue;

                            img.CellY = placement.NewY - discardedRows;
                            img.CellX = placement.NewX;

                            // Prune if shifted out of history bounds
                            if (img.CellY + img.CellHeight < 0)
                            {
                                _source.RetireImage(img);
                                _images.RemoveAt(i);
                            }
                        }
                    }

                    // 7b. Restore Saved Cursors
                    if (newMainSavedPhysRow != -1)
                    {
                        if (newMainSavedPhysRow < sbCount) _savedCursors.Main.Row = 0;
                        else _savedCursors.Main.Row = newMainSavedPhysRow - sbCount;
                        _savedCursors.Main.Col = Math.Clamp(newMainSavedPhysCol, 0, newCols);
                    }
                    if (newAltSavedPhysRow != -1)
                    {
                        if (newAltSavedPhysRow < sbCount) _savedCursors.Alt.Row = 0;
                        else _savedCursors.Alt.Row = newAltSavedPhysRow - sbCount;
                        _savedCursors.Alt.Col = Math.Clamp(newAltSavedPhysCol, 0, newCols);
                    }

                    // 8. Conditional Cursor Row Clearing (REFINED)
                    // Clear ONLY truly empty padding rows on horizontal resize, not actual wrapped content.
                    // This prevents duplication in CMD while preserving oh-my-posh sparse prompts in PowerShell.
                    //
                    // Rationale:
                    // - Horizontal resize: Width changes cause line rewrapping. Some shells may duplicate prompts.
                    // - We only clear rows that are confirmed empty, not rows with actual content.
                    // - This preserves oh-my-posh right-aligned content that wraps to the next row.
                    if (newCols != oldCols)
                    {
                        if (_cursorRow >= 0 && _cursorRow < newRows && _cursorRow + 1 < newRows)
                        {
                            var nextRow = _viewport[_cursorRow + 1];

                            // Only clear if:
                            // 1. Row is NOT wrapped (not a continuation of a wrapped line)
                            // 2. Row is completely empty (no non-space content)
                            if (!nextRow.IsWrapped)
                            {
                                // Check if row has any actual content
                                bool hasContent = false;
                                for (int c = 0; c < nextRow.Cells.Length; c++)
                                {
                                    if (nextRow.Cells[c].Character != ' ' && nextRow.Cells[c].Character != '\0')
                                    {
                                        hasContent = true;
                                        break;
                                    }
                                }

                                // Only clear if truly empty (no content)
                                if (!hasContent)
                                {
                                    _viewport[_cursorRow + 1] = new TerminalRow(newCols, Theme.Foreground, Theme.Background);
                                }
                            }
                        }
                    }

                    // Explicitly check for cursor column overflow (Reflow safety)
                    _cursorCol = Math.Clamp(_cursorCol, 0, newCols);
                }
                catch (Exception ex)
                {
                    TerminalLogger.Error("Reflow failed; buffer reset to a clean state. " + ex);
                    // Failsafe: if reflow crashes, we just reset the buffer to a clean state to avoid permanent hang
                    // The content the marks named is gone with it, so they go too - a mark pointing
                    // into a blank buffer would resolve to a plausible row holding nothing.
                    _commandStartMark = null;
                    _commandOutputStartMark = null;
                    _scrollback = new ScrollbackPages(newCols, _sharedPagePool, _maxScrollbackBytes);
                    _viewport = new TerminalRow[newRows];
                    for (int i = 0; i < newRows; i++) _viewport[i] = new TerminalRow(newCols, Theme.Foreground, Theme.Background);
                    _cursorRow = 0;
                    _cursorCol = 0;
                }
            }
        }
    }
}
