using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Ntilde.VT.Storage;

namespace Ntilde.VT;

public partial class TerminalBuffer
{
    // Keep the old one-int-per-UTF16-unit map: a complemented column marks a wide cell.
    private readonly struct SearchColumn(int column, bool wide)
    {
        private readonly int _encoded = wide ? ~column : column;
        public int Start => _encoded < 0 ? ~_encoded : _encoded;
        public int End => Start + (_encoded < 0 ? 1 : 0);
    }

    private readonly record struct SearchRow(int AbsRow, int TextStart, int TextLength);
    private readonly record struct SearchQuery(string Text, bool CaseSensitive, Regex? Regex, LongLiteralPattern? LongLiteral, Dictionary<(int Row, int Col), int>? SizedEnds = null);

    public List<SearchMatch> FindMatches(string query, bool useRegex, bool caseSensitive)
    {
        var matches = new List<SearchMatch>();
        if (string.IsNullOrEmpty(query)) return matches;
        Regex? regex = null;
        if (useRegex)
        {
            try
            {
                regex = new Regex(query, caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase,
                    TimeSpan.FromMilliseconds(500));
            }
            catch (ArgumentException) { return matches; }
        }

        var pattern = new SearchQuery(query, caseSensitive, regex,
            useRegex ? null : LongLiteralPattern.Create(query, caseSensitive));
        Lock.EnterReadLock();
        try
        {
            int history = _isAltScreen ? 0 : _scrollback.Count;
            int totalRows = history + Rows;
            if (_textSizingBlocks is { Count: > 0 }) pattern = pattern with { SizedEnds = new() };
            // Reuse these across hard lines instead of allocating a builder and map for every row.
            var text = new StringBuilder(Cols);
            var columns = new List<SearchColumn>(Cols);
            var rows = new List<SearchRow>();
            var doubleRuns = new[] { new List<int>(), new List<int>() };
            bool SearchAssembledLine()
            {
                bool completed = SearchLogicalLine(pattern, text.ToString(), columns, rows, matches);
                text.Clear();
                columns.Clear();
                rows.Clear();
                pattern.SizedEnds?.Clear();
                return completed;
            }

            for (int row = 0; row < totalRows; row++)
            {
                // A double-size line is one logical line however a resize wrapped it; a double-height line with both
                // halves is two, on alternating rows. The reflow groups the rows the same way.
                if (text.Length == 0 && rows.Count == 0 && GetLineSizeAbsolute(row) != LineSize.SingleWidth)
                {
                    doubleRuns[0].Clear();
                    doubleRuns[1].Clear();
                    var line = default(DoubleSizeLineRows);
                    int end = row;
                    for (; end < totalRows; end++)
                    {
                        LineSize size = GetLineSizeAbsolute(end);
                        if (size == LineSize.SingleWidth) break;
                        int run = line.Add(end, size, IsRowWrapFlagSetAbsolute(end),
                            end + 1 < totalRows ? GetLineSizeAbsolute(end + 1) : LineSize.SingleWidth,
                            end + 2 < totalRows ? GetLineSizeAbsolute(end + 2) : LineSize.SingleWidth);
                        if (run < 0) break;
                        doubleRuns[run].Add(line.LastRowStartsNewLine ? ~end : end);
                    }

                    int firstMatch = matches.Count;
                    foreach (var run in doubleRuns)
                    {
                        if (run.Count == 0) continue;
                        bool completed = true;
                        foreach (int entry in run)
                        {
                            // A complemented index is a row that starts a new line within its half.
                            if (entry < 0) completed &= SearchAssembledLine();
                            AppendSearchRow(entry < 0 ? ~entry : entry, history, text, columns, rows, pattern.SizedEnds);
                        }
                        if (completed && SearchAssembledLine()) continue;
                        matches.Clear();
                        return matches;
                    }
                    // The two halves share rows alternately: results go back in reading order.
                    if (doubleRuns[1].Count > 0) matches.Sort(firstMatch, matches.Count - firstMatch, SearchMatchReadingOrder.Instance);
                    row = end - 1;
                    continue;
                }

                AppendSearchRow(row, history, text, columns, rows, pattern.SizedEnds);
                if (IsRowWrappedAbsolute(row) && row + 1 < totalRows && GetLineSizeAbsolute(row + 1) == LineSize.SingleWidth) continue;
                if (!SearchAssembledLine())
                {
                    matches.Clear();
                    return matches;
                }
            }
        }
        finally { Lock.ExitReadLock(); }
        return matches;
    }

    private sealed class SearchMatchReadingOrder : IComparer<SearchMatch>
    {
        public static readonly SearchMatchReadingOrder Instance = new();

        public int Compare(SearchMatch x, SearchMatch y)
            => x.AbsRow != y.AbsRow ? x.AbsRow.CompareTo(y.AbsRow) : x.StartCol.CompareTo(y.StartCol);
    }

    private bool AppendSearchRow(int absRow, int history, StringBuilder text, List<SearchColumn> columns, List<SearchRow> rows, Dictionary<(int Row, int Col), int>? sizedEnds)
    {
        ReadOnlySpan<TerminalCell> cells;
        SmallMap<string>? extended;
        bool wrapped;
        int? wrappedColumns;
        if (absRow < history)
        {
            cells = _scrollback.GetRow(absRow);
            extended = _scrollback.GetExtendedTextMap(absRow);
            wrapped = _scrollback.IsRowWrapped(absRow);
            wrappedColumns = _scrollback.GetWrappedColumns(absRow);
        }
        else
        {
            var row = _viewport[absRow - history];
            cells = row.Cells;
            extended = row.GetExtendedTextMap();
            wrapped = row.IsWrapped;
            wrappedColumns = row.WrappedColumns;
        }
        int start = text.Length;
        int count = Math.Min(cells.Length, wrapped ? wrappedColumns ?? Cols : Cols);
        for (int col = 0; col < count; col++)
        {
            var cell = cells[col];
            var sizing = _textSizingBlocks == null ? null : GetTextSizingAbsolute(col, absRow);
            if (cell.IsWideContinuation || sizing is { IsAnchor: false }) continue;
            if (sizing != null && sizedEnds != null) sizedEnds[(absRow, col)] = col + sizing.Block.Width - 1;
            var mapping = new SearchColumn(col, SearchCellEnd(cells, col) > col);
            AppendSearchCell(cell, extended, col, mapping, text, columns);
        }
        if (text.Length > start) rows.Add(new SearchRow(absRow, start, text.Length - start));
        return wrapped;
    }

    private static void AppendSearchCell(TerminalCell cell, SmallMap<string>? extended, int col,
        SearchColumn mapping, StringBuilder text, List<SearchColumn> columns)
    {
        if (cell.HasExtendedText && extended != null && extended.TryGet(col, out string? grapheme) && grapheme != null)
        {
            text.Append(grapheme);
            for (int i = 0; i < grapheme.Length; i++) columns.Add(mapping);
        }
        else
        {
            text.Append(cell.Character);
            columns.Add(mapping);
        }
    }

    private static bool SearchLogicalLine(SearchQuery query, string text,
        List<SearchColumn> columns, List<SearchRow> rows, List<SearchMatch> matches)
    {
        SearchMatchSpan[]? physicalSpans = null;
        if (query.Regex is { } regex)
        {
            try
            {
                foreach (Match match in regex.Matches(text))
                    if (match.Length > 0) matches.Add(CreateSearchMatch(match.Index, match.Length, columns, rows, query.SizedEnds, ref physicalSpans));
            }
            catch (RegexMatchTimeoutException) { return false; }
        }
        else if (query.LongLiteral is { } linear)
        {
            linear.AppendMatches(text, columns, rows, matches, query.SizedEnds, ref physicalSpans);
        }
        else
        {
            var comparison = query.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            int index = text.IndexOf(query.Text, comparison);
            while (index >= 0)
            {
                matches.Add(CreateSearchMatch(index, query.Text.Length, columns, rows, query.SizedEnds, ref physicalSpans));
                index = text.IndexOf(query.Text, index + 1, comparison);
            }
        }
        return true;
    }

    private static int SearchRowAt(List<SearchRow> rows, int textIndex)
    {
        int low = 0, high = rows.Count - 1;
        while (low < high)
        {
            int mid = low + (high - low + 1) / 2;
            if (rows[mid].TextStart <= textIndex) low = mid;
            else high = mid - 1;
        }
        return low;
    }

    private static SearchMatch CreateSearchMatch(int index, int length, List<SearchColumn> columns, List<SearchRow> rows,
        Dictionary<(int Row, int Col), int>? sizedEnds, ref SearchMatchSpan[]? physicalSpans)
    {
        int End(int row, SearchColumn column) => sizedEnds?.GetValueOrDefault((row, column.Start), column.End) ?? column.End;
        int end = index + length - 1;
        int first = SearchRowAt(rows, index), last = SearchRowAt(rows, end);
        if (first == last) return new SearchMatch(rows[first].AbsRow, columns[index].Start, End(rows[last].AbsRow, columns[end]));
        if (physicalSpans == null)
        {
            physicalSpans = new SearchMatchSpan[rows.Count];
            for (int row = 0; row < rows.Count; row++)
            {
                var source = rows[row];
                physicalSpans[row] = new SearchMatchSpan(source.AbsRow, columns[source.TextStart].Start,
                    End(source.AbsRow, columns[source.TextStart + source.TextLength - 1]));
            }
        }
        return new SearchMatch(physicalSpans, first, last, columns[index].Start, End(rows[last].AbsRow, columns[end]));
    }
}
