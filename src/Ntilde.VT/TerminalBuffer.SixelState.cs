namespace Ntilde.VT;

public partial class TerminalBuffer
{
    private void ExportSixelState(TerminalStateSnapshot snapshot)
    {
        foreach (var image in _images)
        {
            if (!image.IsSixelImage || image.SixelSource is not { } source) continue;
            int y = image.IsAltScreenImage ? image.CellY : image.CellY - snapshot.ScrollbackRowsDropped;
            int end = image.IsAltScreenImage ? snapshot.Rows : snapshot.ScrollbackRowCount + snapshot.Rows;
            int top = Math.Max(image.ClipTopRows, Math.Max(0, -y));
            int bottom = Math.Max(image.ClipBottomRows, Math.Max(0, y + image.CellHeight - end));
            if (top + bottom >= image.CellHeight || image.CellX >= snapshot.Cols) continue;
            if (source.Data is not { } data) continue;
            (snapshot.Sixels ??= []).Add(new TerminalSixelImageState
            {
                Source = data,
                X = image.CellX,
                Y = y,
                Width = image.CellWidth,
                Height = image.CellHeight,
                Alt = image.IsAltScreenImage,
                ClipTop = top,
                ClipBottom = bottom,
                ClipRight = image.ClipRightColumns,
            });
        }
    }

    private static void ValidateSixelState(TerminalStateSnapshot snapshot)
    {
        if (snapshot.Sixels is not { } states) return;
        for (int i = Math.Max(0, states.Count - SixelSourceBudget.DefaultMaxSources); i < states.Count; i++)
        {
            var state = StateTransferValidation.RequirePresent(states[i], "SIXEL image");
            string data = StateTransferValidation.RequirePresent(state.Source, "SIXEL source");
            if (data.Length <= SixelSourceBudget.DefaultMaxChars && !AnsiParser.IsSixel(data))
                throw StateTransferValidation.Reject("SIXEL source", "invalid DCS header");
        }
        foreach (var state in BoundedSixelStates(snapshot))
        {
            var (width, height) = SixelGeometry.Measure(state.Source);
            if (width <= 0 || height <= 0) throw StateTransferValidation.Reject("SIXEL source", "empty raster");
        }
    }

    private static List<TerminalSixelImageState> BoundedSixelStates(TerminalStateSnapshot snapshot)
    {
        var kept = new List<TerminalSixelImageState>();
        if (snapshot.Sixels is not { } states) return kept;
        int chars = 0;
        for (int i = states.Count - 1; i >= Math.Max(0, states.Count - SixelSourceBudget.DefaultMaxSources); i--)
        {
            var state = states[i];
            if (state.Source.Length > SixelSourceBudget.DefaultMaxChars - chars) continue;
            chars += state.Source.Length;
            kept.Add(state);
        }
        kept.Reverse();
        return kept;
    }

    private void ImportSixelState(TerminalStateSnapshot snapshot)
    {
        foreach (var state in BoundedSixelStates(snapshot))
        {
            int width = Math.Clamp(state.Width, 1, SixelGeometry.DefaultMaxPixelDimension);
            int height = Math.Clamp(state.Height, 1, SixelGeometry.DefaultMaxPixelDimension);
            int top = Math.Clamp(state.ClipTop, 0, height);
            int bottom = Math.Clamp(state.ClipBottom, 0, height - top);
            int right = Math.Clamp(state.ClipRight, 0, width);
            if (top + bottom >= height || right >= width) continue;
            var source = SixelSources.Retain(state.Source);
            int lastRow = state.Alt ? Rows - 1 : _scrollback.Count + Rows - 1;
            _images.Add(new TerminalImage(source, Math.Clamp(state.X, 0, Cols - 1), Math.Clamp(state.Y, 1 - height, lastRow), width, height)
            {
                IsSixelImage = true,
                SixelSource = source,
                IsAltScreenImage = state.Alt,
                ClipTopRows = top,
                ClipBottomRows = bottom,
                ClipRightColumns = right,
            });
        }
    }

    internal void DecodeTransferredSixels(IImageDecoder? decoder)
    {
        if (decoder is null) return;
        Lock.EnterWriteLock();
        try
        {
            foreach (var image in _images)
            {
                if (image.SixelSource is not { } source) continue;
                try
                {
                    if (decoder.DecodeSixel(source, out _, out _) is { } handle) image.ImageHandle = handle;
                }
                catch (Exception ex) { TerminalLogger.Debug($"[SIXEL] Snapshot decode failed: {ex.Message}"); }
            }
        }
        finally { Lock.ExitWriteLock(); }
        Invalidate();
    }
}
