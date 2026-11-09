namespace Ntilde.VT;

public partial class AnsiParser
{
    /// <summary>DECSCA: 0 and 2 leave later characters erasable, 1 protects them; any other value, or extra parameters, is ignored.</summary>
    private void SelectCharacterProtection(in CsiSequence csi)
    {
        if (csi.Args.Length > 1 || csi.Separators.Contains(':')) return;
        switch (csi.Arg0)
        {
            case 0:
            case 2:
                _buffer.IsCharacterProtected = false;
                break;
            case 1:
                _buffer.IsCharacterProtected = true;
                break;
        }
    }

    /// <summary>Admits only DECERA's (or, when <paramref name="selective"/>, DECSERA's) exact identity and four plain borders; defaults and origin translation precede page clipping.</summary>
    private void EraseRectangularArea(in CsiSequence csi, bool selective = false)
    {
        if (csi.Leader != '\0' || !csi.Intermediates.SequenceEqual("$") || csi.Args.Length > 4
            || csi.Separators.Contains(':') || _buffer.Rows < 1 || _buffer.Cols < 1) return;
        int origin = _buffer.Modes.IsOriginMode ? _buffer.ScrollTop : 0;
        int top = RectangleCoordinate(csi.Args, 0, 1, _buffer.Rows, origin);
        int left = RectangleCoordinate(csi.Args, 1, 1, _buffer.Cols, 0);
        int bottom = RectangleCoordinate(csi.Args, 2, _buffer.Rows, _buffer.Rows, origin);
        int right = RectangleCoordinate(csi.Args, 3, _buffer.Cols, _buffer.Cols, 0);
        if (selective) _buffer.SelectiveEraseRectangularArea(left, top, right, bottom);
        else _buffer.EraseRectangularArea(left, top, right, bottom);
    }

    /// <summary>Converts a one-based omitted/zero border to a bounded zero-based page coordinate without integer overflow.</summary>
    private static int RectangleCoordinate(ReadOnlySpan<int> args, int index, int fallback, int extent, int origin)
    {
        int value = index < args.Length && args[index] > 0 ? args[index] : fallback;
        return (int)Math.Clamp((long)value - 1 + origin, 0, extent - 1);
    }
}
