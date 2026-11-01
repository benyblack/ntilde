namespace Ntilde.VT.Storage;

/// <summary>Side tables transferred with one row; maps follow the row's ownership.</summary>
internal readonly record struct RowSideTables(SmallMap<string>? ExtendedText,
    SmallMap<Ntilde.VT.Links.Hyperlink>? Hyperlinks, SmallMap<CellUnderline>? Underlines,
    SmallMap<CellTextSizing>? TextSizing);
