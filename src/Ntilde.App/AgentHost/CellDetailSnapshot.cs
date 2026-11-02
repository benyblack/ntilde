using System.Collections.Generic;
using Ntilde.AgentHost.Contracts;
using Ntilde.VT;

namespace Ntilde.AgentHost;

/// <summary>Opt-in stored attributes; the caller holds the buffer read lock throughout the capture.</summary>
internal static class CellDetailSnapshot
{
    /// <summary>Capture a complete canonical viewport or refuse before returning any partial data.</summary>
    internal static bool TryCapture(TerminalBuffer buffer, out CellDetailRunDto[]? details)
    {
        details = null;
        if ((long)buffer.Rows * buffer.Cols > AgentHostProtocol.MaxCellDetailCells) return false;
        var runs = new List<CellDetailRunDto>();
        CellDetailRunDto? previous = null;
        var count = 0;
        foreach (var row in buffer.ViewportRows)
        {
            for (var col = 0; col < buffer.Cols; col++)
            {
                var value = Project(row, col);
                if (value == previous)
                {
                    count++;
                    continue;
                }
                if (previous != null) runs.Add(previous with { Count = count });
                if (runs.Count >= AgentHostProtocol.MaxCellDetailRuns) return false;
                previous = value;
                count = 1;
            }
        }
        if (previous != null) runs.Add(previous with { Count = count });
        details = runs.ToArray();
        return true;
    }

    /// <summary>Copy stored attributes without resolving defaults, palette colors or renderer effects.</summary>
    private static CellDetailRunDto Project(TerminalRow row, int col)
    {
        var cell = row.Cells[col];
        var underline = cell.IsUnderline ? row.GetUnderline(col) : null;
        return new CellDetailRunDto
        {
            Count = 1,
            Flags = (ushort)(cell.Flags & ~(ushort)TerminalCellFlags.Dirty),
            Fg = cell.FgIndex,
            Bg = cell.BgIndex,
            ForegroundArgb = cell.IsDefaultForeground || cell.IsPaletteForeground ? null : cell.Fg,
            BackgroundArgb = cell.IsDefaultBackground || cell.IsPaletteBackground ? null : cell.Bg,
            UnderlineStyle = cell.IsUnderline ? (byte)(underline?.Style ?? UnderlineStyle.Single) : (byte)0,
            UnderlineColorIndex = underline?.ColorIndex ?? -1,
            UnderlineArgb = underline?.Color?.ToUint(),
        };
    }

}
