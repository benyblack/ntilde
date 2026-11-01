using Ntilde.VT.Storage;

namespace Ntilde.VT;

public partial class TerminalRow
{
    private SmallMap<CellTextSizing>? _textSizing;

    internal CellTextSizing? GetTextSizing(int col)
        => _textSizing?.TryGet(col, out CellTextSizing? value) == true ? value : null;

    internal void SetTextSizing(int col, CellTextSizing? value)
    {
        if (value == null)
        {
            _textSizing?.Remove(col);
            if (_textSizing?.Count == 0) _textSizing = null;
            return;
        }
        (_textSizing ??= new SmallMap<CellTextSizing>()).Set(col, value);
        NoteColumnsWritten(col + 1);
    }

    internal void ClearTextSizing() => _textSizing = null;
    internal SmallMap<CellTextSizing>? GetTextSizingMap() => _textSizing;
}
