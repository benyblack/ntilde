namespace Ntilde.VT;

/// <summary>
/// The VT100 line attribute: how a whole row is presented. A row that is not <see cref="SingleWidth"/> shows each
/// character two cells wide and so holds half the page's columns. The values are persisted in state transfer:
/// append, never reorder.
/// </summary>
public enum LineSize : byte
{
    /// <summary>DECSWL (<c>ESC # 5</c>): single width, single height.</summary>
    SingleWidth = 0,

    /// <summary>DECDWL (<c>ESC # 6</c>): double width, single height.</summary>
    DoubleWidth = 1,

    /// <summary>DECDHL (<c>ESC # 3</c>): the top half of a double-width, double-height line.</summary>
    DoubleHeightTop = 2,

    /// <summary>DECDHL (<c>ESC # 4</c>): the bottom half of a double-width, double-height line.</summary>
    DoubleHeightBottom = 3,
}
