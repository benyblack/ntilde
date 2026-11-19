namespace Ntilde.VT;

/// <summary>
/// Decides which physical rows make up one double-size line (DECDWL / DECDHL). The reflow and search both feed it
/// the rows in order, so they cannot disagree about where such a line ends.
/// </summary>
/// <remarks>
/// A resize re-wraps a double-size line into rows that keep its attribute and marks each row but the last as
/// wrapped. A double-width line, or a lone half of a double-height line, carries on in the row below. The two halves
/// of a double-height line are laid out top, bottom, top, bottom, so there each half carries on two rows down, past a
/// row of the other half. A line therefore has one run of rows, or two interleaved ones. A run can hold more than one
/// line of text: a row whose predecessor in the same half did not wrap into it starts a new one.
/// </remarks>
internal struct DoubleSizeLineRows
{
    private int _rows;
    private int _firstIndex;
    private int _next0;
    private int _next1;

    /// <summary>Rows taken so far.</summary>
    public readonly int Count => _rows;

    /// <summary>The attribute of the rows in run 0 and in run 1. Run 1 exists only for a double-height line with both halves.</summary>
    public LineSize Size0 { get; private set; }
    public LineSize Size1 { get; private set; }
    public bool HasSecondRun { get; private set; }

    /// <summary>
    /// Whether the row just taken starts a new line inside its run: it sits between two rows of the other half, which
    /// carries on past it, but the row before it in its own half did not wrap into it (it was erased, say).
    /// </summary>
    public bool LastRowStartsNewLine { get; private set; }

    public static bool IsDoubleHeight(LineSize size) => size is LineSize.DoubleHeightTop or LineSize.DoubleHeightBottom;

    /// <summary>
    /// Offers the next row, which must not be single width. Returns the run it joins (0 or 1), or -1 when it does
    /// not belong to this line: the caller then starts a new line with it.
    /// </summary>
    /// <param name="index">The row's index; consecutive calls pass consecutive indexes.</param>
    /// <param name="wrapped">The row's stored wrap bit.</param>
    /// <param name="below">The attribute of the row at <paramref name="index"/> + 1, single width past the end.</param>
    /// <param name="twoBelow">The attribute of the row at <paramref name="index"/> + 2.</param>
    public int Add(int index, LineSize size, bool wrapped, LineSize below, LineSize twoBelow)
    {
        int run;
        LastRowStartsNewLine = false;
        if (_rows == 0)
        {
            run = 0;
            _firstIndex = index;
            _next0 = _next1 = -1;
            Size0 = size;
            HasSecondRun = false;
        }
        else if (index == _next0) run = 0;
        else if (HasSecondRun && index == _next1) run = 1;
        else if (_rows == 1 && index == _firstIndex + 1 && IsDoubleHeight(size) && size != Size0
            && (Size0 == LineSize.DoubleHeightTop || (Size0 == LineSize.DoubleHeightBottom && _next0 == index + 1)))
        {
            // The bottom half of a double-height line is the row straight after its top half. A top half after a
            // bottom half is another line, unless that bottom half carries on past it: then the two were laid out
            // together and the first top half is gone (cut off with the history above it).
            run = 1;
            Size1 = size;
            HasSecondRun = true;
        }
        else if (HasSecondRun && _next0 == index + 1 && size == Size1)
        {
            run = 1;
            LastRowStartsNewLine = true;
        }
        else if (HasSecondRun && _next1 == index + 1 && size == Size0)
        {
            run = 0;
            LastRowStartsNewLine = true;
        }
        else return -1;

        _rows++;
        int next = -1;
        if (wrapped)
        {
            if (below == size) next = index + 1;
            else if (IsDoubleHeight(size) && IsDoubleHeight(below) && twoBelow == size) next = index + 2;
        }
        if (run == 0) _next0 = next;
        else _next1 = next;
        return run;
    }

    public void Reset() => this = default;
}
