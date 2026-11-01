namespace Ntilde.VT.Storage;

public sealed partial class TerminalPage
{
    private SmallMap<CellTextSizing>?[]? _textSizing;

    internal void SetTextSizingFromMap(int rowIndex, SmallMap<CellTextSizing>? source)
    {
        if (source == null || source.Count == 0) return;
        _textSizing ??= new SmallMap<CellTextSizing>?[RowsInPage];
        _textSizing[rowIndex] = source;
    }

    internal SmallMap<CellTextSizing>? GetTextSizingMap(int rowIndex) => _textSizing?[rowIndex];
}
