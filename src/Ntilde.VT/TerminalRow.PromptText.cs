using System;
using System.Collections.Generic;

namespace Ntilde.VT;

internal readonly record struct PromptTextSpan(int Start, int End)
{
    public static PromptTextSpan[] Clip(PromptTextSpan[] spans, int columns)
    {
        int keep = Array.FindLastIndex(spans, span => span.Start < columns) + 1;
        if (keep == 0) return [];
        var clipped = spans[..keep];
        clipped[^1] = clipped[^1] with { End = Math.Min(clipped[^1].End, columns) };
        return clipped;
    }

    public static PromptTextSpan[]? Merge(PromptTextSpan[] spans, int start, int end, int writtenColumns)
    {
        if (end <= start) return spans;
        foreach (var span in spans)
            if (span.Start <= start && span.End >= end) return spans;

        var merged = new List<PromptTextSpan>(spans.Length + 1);
        int index = 0;
        while (index < spans.Length && spans[index].End < start) merged.Add(spans[index++]);
        while (index < spans.Length && spans[index].Start <= end)
        {
            start = Math.Min(start, spans[index].Start);
            end = Math.Max(end, spans[index++].End);
        }
        merged.Add(new(start, end));
        while (index < spans.Length) merged.Add(spans[index++]);
        return merged.Count == 1 && start == 0 && end == writtenColumns ? null : merged.ToArray();
    }
}

public partial class TerminalRow
{
    // Null means the layout's authored prefix. EL paint, cursor padding and
    // reflow can leave gaps in that prefix. These ranges follow prompt text
    // without changing what reflow keeps on screen or in a snapshot.
    internal PromptTextSpan[]? PromptTextSpans { get; set; }

    internal int PromptTextColumns
    {
        get
        {
            if (PromptTextSpans is not { } spans) return WrittenColumns;
            return spans.Length > 0 ? spans[^1].End : 0;
        }
    }

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
        NoteColumnsWritten(to);
    }

    internal void NoteColumnShift(int start, int edge, int delta, bool trackPromptText)
    {
        PromptTextSpan[]? spans = null;
        if (trackPromptText || PromptTextSpans != null)
        {
            var text = new PromptTextReflow();
            int moved = edge - start - System.Math.Abs(delta);
            text.Append(this, 0, 0, start);
            text.Append(this, start + System.Math.Max(0, delta), start + System.Math.Max(0, -delta), moved);
            text.Append(this, edge, edge, Cells.Length - edge);
            spans = text.ReadRow(0, Cells.Length);
        }

        WrittenColumns = Cells.Length;
        PromptTextSpans = spans;
    }

    internal void NoteGlyphWritten(int start, int end)
    {
        NoteColumnsWritten(end);
        if (PromptTextSpans is { } spans)
            PromptTextSpans = PromptTextSpan.Merge(spans, start, Math.Min(end, Cells.Length), WrittenColumns);
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
