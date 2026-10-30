namespace Ntilde.Replay;

/// <summary>Physical cell metrics carried by recordings; contains no terminal or UI types.</summary>
public sealed record RecordingCellMetrics(float WidthPx, float HeightPx)
{
    public static RecordingCellMetrics? FromOptional(float? width, float? height)
    {
        if (width is null && height is null) return null;
        if (width is not float w || height is not float h || !float.IsFinite(w) || !float.IsFinite(h) || w <= 0 || h <= 0)
            throw new InvalidDataException("Recording SIXEL cell metrics must be a finite positive pair.");
        return new RecordingCellMetrics(w, h);
    }
}
