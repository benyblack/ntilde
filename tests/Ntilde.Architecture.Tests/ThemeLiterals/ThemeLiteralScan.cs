using System.Globalization;
using System.Text;

namespace Ntilde.Architecture.Tests.ThemeLiterals;

internal static class ThemeLiteralScan
{
    public const string BaselinePath = "tests/Ntilde.Architecture.Tests/ThemeLiteralBaseline.txt";

    private const string BaselineHeader =
        "# Hardcoded colors outside the theme token layer, per file (see ThemeLiteralGuardTests).\n" +
        "# This file only ever shrinks: migrate a surface to ThemeKeys tokens, then lower its count.\n" +
        "# Regenerate from the guard's failure message; never raise a count by hand.\n";

    // Two guard tests read the scan; a semantic compile of the whole App runs once per test process.
    private static readonly Lazy<IReadOnlyList<ColorLiteralFinding>> Cached = new(Scan);

    /// <summary>Every finding in shipped XAML and App C#, excluding the token layer.</summary>
    public static IReadOnlyList<ColorLiteralFinding> ScanRepository() => Cached.Value;

    private static List<ColorLiteralFinding> Scan()
    {
        var findings = new List<ColorLiteralFinding>();
        foreach (var file in ThemeGuardFiles.ShippedXaml())
        {
            findings.AddRange(XamlColorLiteralScanner.Scan(file, File.ReadAllText(ThemeGuardFiles.Absolute(file))));
        }

        // The whole App compiles together so symbols resolve; only non-token-layer files report.
        var sources = ThemeGuardFiles.AppSources()
            .Select(file => (Path: file, Source: File.ReadAllText(ThemeGuardFiles.Absolute(file))))
            .ToList();
        var appFiles = sources.Select(s => s.Path).ToHashSet(StringComparer.Ordinal);
        findings.AddRange(CSharpColorLiteralScanner.Scan(sources, path => appFiles.Contains(path) && !ThemeLiteralPolicy.IsTokenLayer(path)));
        return findings;
    }

    public static Dictionary<string, int> ParseBaseline(IEnumerable<string> lines)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
            counts[parts[1]] = int.Parse(parts[0], CultureInfo.InvariantCulture);
        }

        return counts;
    }

    public static string FormatBaseline(IReadOnlyDictionary<string, int> counts)
    {
        var text = new StringBuilder(BaselineHeader);
        foreach (var (file, count) in counts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            text.Append(count.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(file).Append('\n');
        }

        return text.ToString();
    }
}
