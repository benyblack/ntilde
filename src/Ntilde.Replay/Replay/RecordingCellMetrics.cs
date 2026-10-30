namespace Ntilde.Replay;

/// <summary>Physical cell metrics carried by recordings; contains no terminal or UI types.</summary>
public sealed record RecordingCellMetrics
{
    public float WidthPx { get; }
    public float HeightPx { get; }

    public RecordingCellMetrics(float widthPx, float heightPx)
    {
        if (!float.IsFinite(widthPx) || !float.IsFinite(heightPx) || widthPx <= 0 || heightPx <= 0)
            throw new InvalidDataException("Recording SIXEL cell metrics must be finite and positive.");
        WidthPx = widthPx;
        HeightPx = heightPx;
    }

    public static RecordingCellMetrics? FromOptional(float? width, float? height)
    {
        if (width is null && height is null) return null;
        if (width is not float w || height is not float h || !float.IsFinite(w) || !float.IsFinite(h) || w <= 0 || h <= 0)
            throw new InvalidDataException("Recording SIXEL cell metrics must be a finite positive pair.");
        return new RecordingCellMetrics(w, h);
    }
}
