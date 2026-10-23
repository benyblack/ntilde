using System.Globalization;
using Ntilde.Architecture.Tests.ThemeLiterals;

namespace Ntilde.Architecture.Tests;

/// <summary>
/// Gating ratchet. Hardcoded colors outside the token layer may only disappear. Other sessions
/// commit to main while the theme migration runs, so this blocks new literals from day one, not
/// only after the last surface is migrated.
/// </summary>
public class ThemeLiteralGuardTests
{
    private const string Remedy =
        "Use a theme token instead of a color literal: {DynamicResource NtX} in XAML, ThemeKeys.X in C# " +
        "(src/Ntilde.App/Shell/Theming/ThemeKeys.cs; docs/ThemeSystem.md, \"UI color tokens\"). " +
        "A color fixed on purpose (platform convention, user-chosen identity, debug-only) goes in " +
        "ThemeLiteralPolicy.Permanent with a reason.";

    [Fact]
    public void Color_literals_outside_the_token_layer_only_ever_shrink()
    {
        var findings = ThemeLiteralScan.ScanRepository().Where(f => !ThemeLiteralPolicy.IsPermanentlyAllowed(f)).ToList();
        var actual = findings.GroupBy(f => f.File).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var baseline = ThemeLiteralScan.ParseBaseline(File.ReadAllLines(ThemeGuardFiles.Absolute(ThemeLiteralScan.BaselinePath)));

        var grown = actual.Where(kv => kv.Value > baseline.GetValueOrDefault(kv.Key)).Select(kv => kv.Key).ToList();
        var shrunk = baseline.Where(kv => actual.GetValueOrDefault(kv.Key) < kv.Value).Select(kv => kv.Key).ToList();

        var message = new System.Text.StringBuilder();
        if (grown.Count > 0)
        {
            message.AppendLine("New hardcoded colors. " + Remedy);
            foreach (var file in grown)
            {
                message.AppendLine(CultureInfo.InvariantCulture, $"  {file}: {actual[file]} (baseline {baseline.GetValueOrDefault(file)})");
                foreach (var finding in findings.Where(f => f.File == file))
                {
                    message.AppendLine(CultureInfo.InvariantCulture, $"    {finding}");
                }
            }
        }

        if (shrunk.Count > 0)
        {
            message.AppendLine("Fewer literals than the baseline records. Lower it: replace " + ThemeLiteralScan.BaselinePath + " with:");
        }

        if (grown.Count > 0 || shrunk.Count > 0)
        {
            message.AppendLine("----- regenerated baseline -----");
            message.Append(ThemeLiteralScan.FormatBaseline(actual));
        }

        Assert.True(grown.Count == 0 && shrunk.Count == 0, message.ToString());
    }

    [Fact]
    public void Every_permanent_allowance_still_matches_something()
    {
        var findings = ThemeLiteralScan.ScanRepository();
        var stale = ThemeLiteralPolicy.Permanent.Where(allowance => !findings.Any(allowance.Matches)).ToList();
        Assert.True(stale.Count == 0,
            "Permanent allowances that match nothing (remove them): " +
            string.Join("; ", stale.Select(a => $"{a.File} {a.Member ?? a.Text}")));
    }

    [Fact]
    public void Baseline_never_lists_a_token_layer_file()
    {
        var baseline = ThemeLiteralScan.ParseBaseline(File.ReadAllLines(ThemeGuardFiles.Absolute(ThemeLiteralScan.BaselinePath)));
        Assert.DoesNotContain(baseline.Keys, ThemeLiteralPolicy.IsTokenLayer);
    }
}
