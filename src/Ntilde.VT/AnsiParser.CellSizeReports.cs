using System.Globalization;

namespace Ntilde.VT;

public partial class AnsiParser
{
    private CellSizeReportMetrics? _cellSizeReports;

    /// <summary>One atomic host snapshot; absent for hosts that cannot supply all protocol units.</summary>
    public CellSizeReportMetrics? CellSizeReports
    {
        get => System.Threading.Volatile.Read(ref _cellSizeReports);
        set => System.Threading.Volatile.Write(ref _cellSizeReports, value);
    }

    private bool HandleCellSizeReport(string osc, string terminator)
    {
        if (osc is "7770;?" or "7777;?")
        {
            // Only conversion roundoff (at most eight binary64 ULPs) may be
            // normalized. Meaningfully fractional sizes and absent hosts stay silent.
            if (CellSizeReports is { IsValid: true } host && TryIntegerFontPoints(host.FontSizePoints, out int points))
            {
                string code = osc[..4];
                OnResponse?.Invoke(string.Create(CultureInfo.InvariantCulture, $"\x1b]{code};{points:0}{terminator}"));
            }
            return true;
        }
        if (osc is not ("1337;ReportCellSize" or "776;?")) return false;
        if (CellSizeReports is not { IsValid: true } metrics) return true;
        if (osc == "1337;ReportCellSize")
        {
            // iTerm2: height, width in logical points, followed by physical pixels per point.
            OnResponse?.Invoke(string.Create(CultureInfo.InvariantCulture,
                $"\x1b]1337;ReportCellSize={metrics.HeightPoints:G9};{metrics.WidthPoints:G9};{metrics.Scale:G9}\x1b\\"));
        }
        else
        {
            // urxvt 9.29: only '?' queries; width, height and font ascent in pixels. Mirror its terminator.
            int Pixels(double points) => Math.Max(1, (int)Math.Round(points * metrics.Scale, MidpointRounding.AwayFromZero));
            OnResponse?.Invoke(string.Create(CultureInfo.InvariantCulture,
                $"\x1b]776;{Pixels(metrics.WidthPoints)};{Pixels(metrics.HeightPoints)};{Pixels(metrics.FontAscentPoints)}{terminator}"));
        }
        return true;
    }

    private static bool TryIntegerFontPoints(double? value, out int points)
    {
        points = 0;
        if (value is not double size || !double.IsFinite(size)) return false;
        double nearest = Math.Round(size, MidpointRounding.AwayFromZero);
        if (nearest < 1 || nearest > 512) return false;
        // The host conversion multiplies/divides DIP, effective/backing scales
        // and 72/96. Eight ULPs bounds its accumulated binary64 representation
        // error; it is not a decimal rounding tolerance or a half-point policy.
        double ulp = Math.BitIncrement(nearest) - nearest;
        if (Math.Abs(size - nearest) > 8 * ulp) return false;
        points = (int)nearest;
        return true;
    }
}
