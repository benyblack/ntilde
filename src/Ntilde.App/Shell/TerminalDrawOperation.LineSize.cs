using Ntilde.VT;
using SkiaSharp;

namespace Ntilde.Shell;

/// <summary>VT100 double-width and double-height lines (DECDWL, DECDHL): the geometry the row painter and the overlays share.</summary>
public sealed partial class TerminalDrawOperation
{
    /// <summary>Set while a row is drawn through the line-size transform; see <see cref="BeginLineSizeTransform"/>.</summary>
    private bool _suppressGlyphAtlas;

    /// <summary>
    /// Saves the canvas and installs the transform a double-size row is drawn through, clipped to the row's band so the
    /// half of a double-height glyph that belongs to the neighbouring row never reaches it. Returns false, leaving the
    /// canvas alone, for a single-width row. The caller restores the canvas when it returns true.
    /// </summary>
    private bool BeginLineSizeTransform(SKCanvas canvas, LineSize lineSize, float rowTop, float rowHeight)
    {
        if (lineSize == LineSize.SingleWidth) return false;

        FlushBatches(canvas);
        float originX = FromDevicePx(_pixelGrid.OriginXPx);
        bool doubleHeight = lineSize is LineSize.DoubleHeightTop or LineSize.DoubleHeightBottom;
        canvas.Save();
        canvas.ClipRect(new SKRect(float.MinValue / 4, rowTop, float.MaxValue / 4, rowTop + rowHeight));
        // The bottom half is the lower half of the doubled glyph: the doubled row starts one row height higher.
        canvas.Translate(originX, lineSize == LineSize.DoubleHeightBottom ? rowTop - rowHeight : rowTop);
        canvas.Scale(2f, doubleHeight ? 2f : 1f);
        canvas.Translate(-originX, -rowTop);
        return true;
    }

    /// <summary>Where a single-width x lands on a row with <paramref name="lineSize"/>.</summary>
    private float LineSizeX(float x, LineSize lineSize)
    {
        if (lineSize == LineSize.SingleWidth) return x;
        float originX = FromDevicePx(_pixelGrid.OriginXPx);
        return originX + ((x - originX) * 2f);
    }

    private static LineSize LineSizeOfVisualRow(TerminalRenderSnapshot snapshot, int visualRow)
        => snapshot.RowsData.Array != null && visualRow >= 0 && visualRow < snapshot.RowsData.Length
            ? snapshot.RowsData.Array[visualRow].LineSize
            : LineSize.SingleWidth;
}
