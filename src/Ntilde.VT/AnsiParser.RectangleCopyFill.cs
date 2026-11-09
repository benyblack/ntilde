namespace Ntilde.VT;

public partial class AnsiParser
{
    /// <summary>
    /// DECFRA, <c>CSI Pch ; Pt ; Pl ; Pb ; Pr $ x</c>. Admits only that exact identity with at most five plain parameters.
    /// Pch must be 32..126 (looked up in the in-use GL set) or 160..255 (the Latin-1 right half; Ntilde has no other GR set),
    /// otherwise the whole command is ignored, as is a rectangle whose clipped borders are reversed. Page margins do not clip it.
    /// </summary>
    private void FillRectangularArea(in CsiSequence csi)
    {
        if (csi.Leader != '\0' || !csi.Intermediates.SequenceEqual("$") || csi.Args.Length > 5
            || csi.Separators.Contains(':') || _buffer.Rows < 1 || _buffer.Cols < 1) return;
        int code = csi.Args.Length > 0 ? csi.Args[0] : 0;
        char fill;
        if (code >= 32 && code <= 126) fill = _charsets[_gl] == Charset.DecSpecialGraphics ? MapDecSpecialGraphics((char)code) : (char)code;
        else if (code >= 160 && code <= 255) fill = (char)code;
        else return;
        ReadOnlySpan<int> borders = csi.Args.Length > 1 ? csi.Args[1..] : default;
        int origin = _buffer.Modes.IsOriginMode ? _buffer.ScrollTop : 0;
        int top = RectangleCoordinate(borders, 0, 1, _buffer.Rows, origin);
        int left = RectangleCoordinate(borders, 1, 1, _buffer.Cols, 0);
        int bottom = RectangleCoordinate(borders, 2, _buffer.Rows, _buffer.Rows, origin);
        int right = RectangleCoordinate(borders, 3, _buffer.Cols, _buffer.Cols, 0);
        _buffer.FillRectangularArea(fill, left, top, right, bottom);
    }

    /// <summary>
    /// DECCRA, <c>CSI Pts ; Pls ; Pbs ; Prs ; Pps ; Ptd ; Pld ; Ppd $ v</c>. Admits only that exact identity with at most eight plain
    /// parameters. Only page 1 exists: an explicit source or destination page other than 0/1 ignores the whole command instead of
    /// being treated as the active page. All coordinates are origin-mode aware (rows) and clamped to the page; a reversed source
    /// rectangle is ignored. The destination is clipped to the page.
    /// </summary>
    private void CopyRectangularArea(in CsiSequence csi)
    {
        if (csi.Leader != '\0' || !csi.Intermediates.SequenceEqual("$") || csi.Args.Length > 8
            || csi.Separators.Contains(':') || _buffer.Rows < 1 || _buffer.Cols < 1) return;
        ReadOnlySpan<int> args = csi.Args;
        if ((args.Length > 4 && args[4] > 1) || (args.Length > 7 && args[7] > 1)) return;
        int origin = _buffer.Modes.IsOriginMode ? _buffer.ScrollTop : 0;
        int top = RectangleCoordinate(args, 0, 1, _buffer.Rows, origin);
        int left = RectangleCoordinate(args, 1, 1, _buffer.Cols, 0);
        int bottom = RectangleCoordinate(args, 2, _buffer.Rows, _buffer.Rows, origin);
        int right = RectangleCoordinate(args, 3, _buffer.Cols, _buffer.Cols, 0);
        int destinationTop = RectangleCoordinate(args, 5, 1, _buffer.Rows, origin);
        int destinationLeft = RectangleCoordinate(args, 6, 1, _buffer.Cols, 0);
        _buffer.CopyRectangularArea(left, top, right, bottom, destinationLeft, destinationTop);
    }
}
