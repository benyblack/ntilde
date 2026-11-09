namespace Ntilde.VT;

/// <summary>
/// Marker value for a DECSCA-protected character. Protection is a yes/no property of a cell, so every
/// protected column of every row shares this one instance; the row's sparse map only needs the keys.
/// </summary>
public sealed class CellProtection
{
    public static readonly CellProtection Instance = new();

    private CellProtection() { }
}
