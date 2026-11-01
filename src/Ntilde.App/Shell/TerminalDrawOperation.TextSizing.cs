using Ntilde.VT;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace Ntilde.Shell;

public sealed partial class TerminalDrawOperation
{
    // Paint the same immutable block through each occupied row's clip. A cached row can be
    // repainted independently, including when its anchor has scrolled above the viewport.
    private readonly struct SizedTextRowPaint
    {
        public float RowTop { get; init; }
        public float Baseline { get; init; }
        public float RowHeight { get; init; }
        public required SKPaint Paint { get; init; }
        public SKColor ThemeFg { get; init; }
        public SKColor ThemeBg { get; init; }
        public RenderThemeSnapshot Theme { get; init; }
        public byte Alpha { get; init; }
        public required SKTypeface Primary { get; init; }
        public bool DrawBackgrounds { get; init; }
        public bool HasVisibleText { get; init; }
        public SKColor? ForegroundOverride { get; init; }
        public SKColor? OverlayFill { get; init; }
    }

    private int DrawSizedTextRow(SKCanvas canvas, RenderCellSnapshot cell, CellTextSizing piece,
        int col, SizedTextRowPaint context)
    {
        float rowTop = context.RowTop, baseline = context.Baseline, rowHeight = context.RowHeight;
        var paint = context.Paint;
        var primary = context.Primary;
        var block = piece.Block;
        float left = FromDevicePx(_pixelGrid.XForCol(col));
        float right = FromDevicePx(_pixelGrid.XForCol(col + block.Width));
        var cb = ResolveCellBackground(cell, context.ThemeBg, context.Alpha, context.Theme);
        var cf = ResolveCellForeground(cell, context.ThemeFg, context.Alpha, context.Theme);
        var bg = cell.IsInverse ? cf : cb;
        var fg = EnforceMinimumContrast(cell.IsInverse ? cb : cf, bg, context.OverlayFill);
        if (cell.IsFaint) fg = BlendTowards(fg, bg, 0.5f);
        if (context.ForegroundOverride is { } overridden) fg = overridden;
        FlushBatches(canvas);
        canvas.Save();
        try
        {
            canvas.ClipRect(new SKRect(left, rowTop, right, rowTop + rowHeight));
            if (context.DrawBackgrounds && bg != context.ThemeBg && bg.Alpha != 0)
            {
                _bgFillPaint.Color = bg;
                canvas.DrawRect(left, rowTop, right - left, rowHeight, _bgFillPaint);
            }
            float fraction = block.Options.Fraction;
            float heightScale = block.Height * fraction;
            float width = (right - left) * fraction;
            float height = rowHeight * heightScale;
            float blockTop = rowTop - piece.RowOffset * rowHeight;
            float y = blockTop + AlignedSizingOffset(block.Height * rowHeight - height, block.Options.VerticalAlignment);
            int missing = FindFirstMissingGlyphCodePoint(block.Text, primary);
            var resources = GetOrCreateShapingResources(missing == 0 ? primary : ResolveTypefaceForCodePoint(missing, primary));
            float naturalWidth = resources.Shaper.Shape(block.Text, resources.Font).Width;
            if (!(naturalWidth > 0)) return block.Width;
            float horizontalScale = Math.Min(heightScale, (right - left) / naturalWidth);
            float occupiedWidth = Math.Max(width, naturalWidth * horizontalScale);
            float x = left + AlignedSizingOffset(Math.Max(0, right - left - occupiedWidth), block.Options.HorizontalAlignment);
            canvas.Translate(x, y);
            // Fraction scales the font once. Compress only text that exceeds the full block.
            canvas.Scale(horizontalScale, heightScale);
            float localBaseline = baseline - rowTop;
            if (cell.IsItalic)
            {
                canvas.Translate(0, localBaseline);
                canvas.Skew(-0.22f, 0);
                canvas.Translate(0, -localBaseline);
            }
            paint.Color = fg;
            canvas.DrawShapedText(resources.Shaper, block.Text, 0, localBaseline, resources.Font, paint);
            IncrementShapedTextRun();
            IncrementTextDrawCall();
            DrawSizedDecorations(canvas, cell, (naturalWidth, (left - x) / horizontalScale, (right - left) / horizontalScale),
                localBaseline, rowHeight, fg, context);
            return block.Width;
        }
        finally { canvas.Restore(); }
    }

    private static float AlignedSizingOffset(float slack, int alignment)
        => alignment switch { 1 => slack, 2 => slack / 2, _ => 0 };

    private void DrawSizedDecorations(SKCanvas canvas, RenderCellSnapshot cell,
        (float TextWidth, float BlockLeft, float BlockWidth) geometry,
        float baseline, float rowHeight, SKColor fg, SizedTextRowPaint context)
    {
        _decoStrokePaint.Color = fg;
        _decoStrokePaint.IsAntialias = true;
        _decoStrokePaint.Style = SKPaintStyle.Stroke;
        float stroke = Math.Max(1f, rowHeight * 0.06f);
        _decoStrokePaint.StrokeWidth = stroke;
        if (cell.IsUnderline && (cell.Underline != null || context.HasVisibleText))
        {
            if (cell.Underline is { HasColor: true } underline)
                _decoStrokePaint.Color = ResolveUnderlineColor(underline, context.Theme);
            float start = cell.Underline == null ? 0 : geometry.BlockLeft;
            float width = cell.Underline == null ? geometry.TextWidth : geometry.BlockWidth;
            DrawUnderline(canvas, cell.Underline?.Style ?? UnderlineStyle.Single, start, start + width,
                Math.Min(rowHeight - stroke, baseline + stroke), rowHeight, stroke);
        }
        if (cell.IsStrikethrough)
        {
            _decoStrokePaint.Color = fg;
            canvas.DrawLine(0, baseline - rowHeight * 0.3f, geometry.TextWidth, baseline - rowHeight * 0.3f, _decoStrokePaint);
        }
    }
}
