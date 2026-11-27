using System.Collections.Generic;

namespace Ntilde.VT;

internal readonly record struct PromptTextSpan(int Start, int End);

public partial class TerminalRow
{
    // Null means the layout's authored prefix. EL paint, cursor padding and
    // reflow can leave gaps in that prefix. These ranges follow prompt text
    // without changing what reflow keeps on screen or in a snapshot.
    internal PromptTextSpan[]? PromptTextSpans { get; set; }

    internal int PromptTextColumns => PromptTextSpans is { } spans
        ? (spans.Length > 0 ? spans[^1].End : 0)
        : WrittenColumns;

    internal void NoteLineErasure(int from, int to, bool trackPromptText)
    {
        if (trackPromptText || PromptTextSpans != null)
        {
            var retained = new List<PromptTextSpan>();
            var spans = PromptTextSpans ?? [new(0, WrittenColumns)];
            foreach (var span in spans)
            {
                int left = System.Math.Min(from, span.End);
                int right = System.Math.Max(to, span.Start);
                if (span.Start < left) retained.Add(new(span.Start, left));
                if (right < span.End) retained.Add(new(right, span.End));
            }
            PromptTextSpans = retained.ToArray();
        }
        NoteLayoutColumnsWritten(to);
    }

    private void NotePromptTextWritten(PromptTextSpan[] spans, int start, int end)
    {
        if (end <= start) return;
        foreach (var span in spans)
            if (span.Start <= start && span.End >= end) return;

        var merged = new List<PromptTextSpan>(spans.Length + 1);
        int index = 0;
        while (index < spans.Length && spans[index].End < start) merged.Add(spans[index++]);
        while (index < spans.Length && spans[index].Start <= end)
        {
            start = System.Math.Min(start, spans[index].Start);
            end = System.Math.Max(end, spans[index++].End);
        }
        merged.Add(new(start, end));
        while (index < spans.Length) merged.Add(spans[index++]);
        PromptTextSpans = merged.Count == 1 && start == 0 && end == WrittenColumns ? null : merged.ToArray();
    }
}

// Maps surviving text through the same logical stream offsets as the cells.
// Separate ranges keep erased gaps distinguishable even after two output
// rows join into a single wider row and later split again.
internal sealed class PromptTextReflow
{
    private readonly List<PromptTextSpan> _spans = new();
    private int _first;

    public void Append(TerminalRow row, int targetStart, int sourceStart, int columns)
    {
        if (row.PromptTextSpans is not { } spans)
        {
            AppendClipped(0, row.WrittenColumns, targetStart, sourceStart, columns);
            return;
        }

        foreach (var span in spans)
            AppendClipped(span.Start, span.End, targetStart, sourceStart, columns);
    }

    private void AppendClipped(int start, int end, int targetStart, int sourceStart, int columns)
    {
        start = System.Math.Max(start, sourceStart) - sourceStart + targetStart;
        end = System.Math.Min(end, sourceStart + columns) - sourceStart + targetStart;
        if (end <= start) return;
        if (_spans.Count > 0 && _spans[^1].End == start)
            _spans[^1] = _spans[^1] with { End = end };
        else
            _spans.Add(new(start, end));
    }

    public PromptTextSpan[]? ReadRow(int start, int columns)
    {
        if (columns == 0) return null;
        while (_first < _spans.Count && _spans[_first].End <= start) _first++;

        int end = start + columns;
        if (_first == _spans.Count || _spans[_first].Start >= end) return [];
        if (_spans[_first].Start <= start && _spans[_first].End >= end) return null;

        var result = new List<PromptTextSpan>();
        for (int i = _first; i < _spans.Count && _spans[i].Start < end; i++)
        {
            var span = _spans[i];
            result.Add(new(System.Math.Max(start, span.Start) - start, System.Math.Min(end, span.End) - start));
        }
        return result.ToArray();
    }

    public void Clear()
    {
        _spans.Clear();
        _first = 0;
    }
}
