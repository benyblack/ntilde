using System;

namespace Ntilde.VT
{
    public readonly record struct SearchMatchSpan(int AbsRow, int StartCol, int EndCol);

    /// <summary>One logical match, with one or more inclusive physical cell spans.</summary>
    public readonly struct SearchMatch
    {
        private readonly SearchMatchSpan[]? _spans;
        // A single result stores its absolute row directly; cross-row results index a shared map.
        private readonly int _first;
        private readonly int _last;
        private readonly int _start;
        private readonly int _end;
        public int AbsRow => _spans is null ? _first : _spans[_first].AbsRow;
        public int StartCol => _start;
        // Original properties describe the first physical span.
        public int EndCol => _spans is null || _first == _last ? _end : _spans[_first].EndCol;
        public int SpanCount => _spans is null ? 1 : _last - _first + 1;
        public int LastAbsRow => _spans is null ? _first : _spans[_last].AbsRow;

        public SearchMatch(int row, int start, int end)
        {
            _spans = null;
            _first = _last = row;
            _start = start;
            _end = end;
        }

        // All cross-row results on a logical line share one immutable physical-row map.
        internal SearchMatch(SearchMatchSpan[] spans, int first, int last, int startCol, int endCol)
        {
            _spans = spans;
            _first = first;
            _last = last;
            _start = startCol;
            _end = endCol;
        }

        internal int FirstSpanAtOrAfter(int absRow)
        {
            int low = 0, high = SpanCount;
            while (low < high)
            {
                int mid = low + (high - low) / 2;
                int row = _spans is null ? _first : _spans[_first + mid].AbsRow;
                if (row < absRow) low = mid + 1;
                else high = mid;
            }
            return low;
        }

        public SearchMatchSpan GetSpan(int index)
        {
            if ((uint)index >= (uint)SpanCount) throw new ArgumentOutOfRangeException(nameof(index));
            if (_spans is null) return new SearchMatchSpan(_first, _start, _end);
            var span = _spans[_first + index];
            return new SearchMatchSpan(span.AbsRow, index == 0 ? _start : span.StartCol,
                index == SpanCount - 1 ? _end : span.EndCol);
        }
    }
}
