namespace Ntilde.VT;

public partial class AnsiParser
{
    /// <summary>
    /// DECIC (<c>CSI Pn ' }</c>) and DECDC (<c>CSI Pn ' ~</c>): insert or delete <c>Pn</c> columns at the cursor column on every row
    /// of the scrolling region. SL (<c>CSI Pn SP @</c>) and SR (<c>CSI Pn SP A</c>): shift those rows left or right by <c>Pn</c>
    /// columns from the left margin. Each admits only its exact unprefixed identity with at most one plain parameter; an omitted
    /// or zero count is one. See <see cref="TerminalBuffer.EditColumns"/> for the region and cursor rules.
    /// </summary>
    private void EditColumns(in CsiSequence csi, string intermediate, bool atCursor, bool insert)
    {
        if (csi.Leader != '\0' || !csi.Intermediates.SequenceEqual(intermediate) || csi.Args.Length > 1
            || csi.Separators.Contains(':')) return;
        int count = csi.Args.Length > 0 && csi.Args[0] > 0 ? csi.Args[0] : 1;
        _buffer.EditColumns(atCursor, insert, count);
    }
}
