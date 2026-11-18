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
            // Mintty reports an unsigned integer point size. Do not round a fractional
            // host size into a false restorable value, or fabricate headless defaults.
            if (CellSizeReports is { IsValid: true, FontSizePoints: double points }
                && double.IsFinite(points) && points >= 1 && points <= 512 && double.IsInteger(points))
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
}
