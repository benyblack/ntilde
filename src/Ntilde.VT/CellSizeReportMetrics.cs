namespace Ntilde.VT;

/// <summary>Host-observed logical cell, physical/logical scale and font ascent. No guessed headless defaults.</summary>
public sealed record CellSizeReportMetrics(double WidthPoints, double HeightPoints, double Scale, double FontAscentPoints)
{
    /// <summary>Typographic points supplied by the presenting host; null when unavailable.</summary>
    public double? FontSizePoints { get; init; }

    public bool IsValid => Positive(WidthPoints) && Positive(HeightPoints) && Positive(Scale)
        && Positive(FontAscentPoints) && FontAscentPoints <= HeightPoints
        && Positive(WidthPoints * Scale) && Positive(HeightPoints * Scale)
        && HeightPoints * Scale < int.MaxValue && WidthPoints * Scale < int.MaxValue;

    private static bool Positive(double value) => double.IsFinite(value) && value > 0;
}
