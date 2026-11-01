using System.Text.Json;
using Ntilde.Conformance;

namespace Ntilde.Tests;

public sealed class VtReportCliTests
{
    [Fact]
    public void IsSupportedCliMode_ReturnsFalse_WhenFlagIsMissing()
    {
        Assert.False(VtReportCommand.IsSupportedCliMode(["--help"]));
    }

    [Fact]
    public void IsSupportedCliMode_ReturnsTrue_ForVtReport()
    {
        Assert.True(VtReportCommand.IsSupportedCliMode(["--vt-report"]));
    }

    [Fact]
    public void Execute_PrintsHumanReadableSummary_ByDefault()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exitCode = VtReportCommand.Execute(["--vt-report"], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());

        string output = stdout.ToString();
        Assert.Contains("Ntilde VT Report", output);
        Assert.Contains("Matrix:", output);
        Assert.Contains("Supported:", output);
        Assert.Contains("Validation:", output);
        Assert.Contains("--vt-report --json", output);
    }

    [Fact]
    public void Execute_PrintsEmbeddedJson_WhenJsonFlagIsPresent()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exitCode = VtReportCommand.Execute(["--vt-report", "--json"], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());

        string output = stdout.ToString().Trim();
        using JsonDocument document = JsonDocument.Parse(output);
        Assert.True(document.RootElement.TryGetProperty("summary", out _));
        Assert.True(document.RootElement.TryGetProperty("rows", out _));
        Assert.True(document.RootElement.TryGetProperty("warnings", out _));
    }

    [Fact]
    public void TryRun_ReturnsFalse_WhenFlagIsMissing()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        bool handled = VtReportCli.TryRun(["--help"], stdout, stderr, out int exitCode);

        Assert.False(handled);
        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public void ShippedArtifact_MatchesFreshToolOutput()
    {
        string repoRoot = FindRepositoryRoot();
        string artifactPath = Path.Combine(repoRoot, "src", "Ntilde.App", "Resources", "vt-conformance-report.json");
        string expected = VtConformanceReportTool.Serialize(
            VtConformanceReportTool.Generate(repoRoot, Path.Combine(repoRoot, "docs", "vt_coverage_matrix.md")));
        string actual = File.ReadAllText(artifactPath);

        Assert.Equal(expected, actual);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Ntilde.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test output path.");
    }
}
