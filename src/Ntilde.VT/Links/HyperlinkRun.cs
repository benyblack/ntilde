namespace Ntilde.VT.Links
{
    /// <summary>A run of cells on one row that carry the same OSC 8 anchor: columns [StartCol, EndCol], inclusive.</summary>
    public readonly record struct HyperlinkRun(int AbsRow, int StartCol, int EndCol);
}
