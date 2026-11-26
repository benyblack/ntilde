using Ntilde.Rendering;
using Ntilde.VT;
using SkiaSharp;

namespace Ntilde.Shell;

public sealed partial class TerminalDrawOperation
{
    /// <summary>Paints source pixels at presenter-native extent; capture scaling never substitutes for presenter scale.</summary>
    private SKBitmap? DrawNativeSixel(SKCanvas canvas, RenderImageSnapshot image, int visualY,
        float[] colEdges, float[] rowEdges, float paddingLeft, float paddingTop,
        SKPaint imagePaint, SKPaint background, ref int remainingDecodes, ref bool pendingDecode)
    {
        var intent = image.NativeRaster!;
        float x = LineSizeX(GetColEdge(colEdges, image.CellX, paddingLeft), image.LineSize);
        float y = GetRowEdge(rowEdges, visualY, paddingTop);
        canvas.Save();
        try
        {
            canvas.ClipRect(new SKRect(paddingLeft, 0, (float)Bounds.Width, (float)Bounds.Height));
            // Clearing retains its historical integer envelope, separately from native pixel extent.
            if (image.CellWidth > image.ClipRightColumns && image.CellHeight > (long)image.ClipTopRows + image.ClipBottomRows)
            {
                canvas.DrawRect(new SKRect(x,
                    GetRowEdge(rowEdges, visualY + image.ClipTopRows, paddingTop),
                    LineSizeX(GetColEdge(colEdges, image.CellX + image.CellWidth - image.ClipRightColumns, paddingLeft), image.LineSize),
                    GetRowEdge(rowEdges, visualY + image.CellHeight - image.ClipBottomRows, paddingTop)), background);
                IncrementRectDrawCall();
            }
            var crop = intent.Crop;
            if (crop.Right <= crop.Left || crop.Bottom <= crop.Top) return null;
            SKBitmap? bitmap = image.ImageHandle as SKBitmap;
            if (image.ImageHandle is NativeSixelImage owner)
            {
                bitmap = owner.GetBitmap(ref remainingDecodes, out bool pending);
                pendingDecode |= pending;
            }
            if (bitmap is null)
            {
                if (_isolatedSnapshot) NativeRasterUnavailable = true;
                return null;
            }
            if (bitmap.Width != intent.CanvasWidth || bitmap.Height != intent.CanvasHeight || _nativeRasterPresenter is null)
            {
                NativeRasterUnavailable = true;
                return null;
            }
            double p = _nativeRasterPresenter.DevicePixelsPerDip;
            var destination = new SKRect((float)(x + crop.Left / p), (float)(y + crop.Top / p),
                (float)(x + crop.Right / p), (float)(y + crop.Bottom / p));
            if (!float.IsFinite(destination.Left) || !float.IsFinite(destination.Top) ||
                !float.IsFinite(destination.Right) || !float.IsFinite(destination.Bottom) ||
                destination.Right <= destination.Left || destination.Bottom <= destination.Top)
            {
                NativeRasterUnavailable = true;
                return null;
            }
            var source = new SKRect((float)crop.Left, (float)crop.Top, (float)crop.Right, (float)crop.Bottom);
            canvas.DrawBitmap(bitmap, source, destination, imagePaint);
            IncrementOtherDrawCall();
            return bitmap;
        }
        finally { canvas.Restore(); }
    }
}
