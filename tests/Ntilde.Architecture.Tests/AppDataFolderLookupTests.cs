using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Ntilde.Architecture.Tests;

/// <summary>
/// Every app-data root - the App's, Platform's, the mux and agent-host discovery files', the MCP server's backups -
/// is built from <c>Environment.GetFolderPath</c>. Without <see cref="Environment.SpecialFolderOption.DoNotVerify"/>
/// that returns "" on Linux and macOS when the folder does not exist yet (a fresh account with no ~/.local/share),
/// and the root silently became a bare relative "ntilde", resolved against each process's own working directory.
/// The app and the mux daemon start in different directories, so they then disagreed about where each other's
/// endpoint was. Each root creates its directories itself, so asking for the path unverified is always right here.
/// </summary>
public class AppDataFolderLookupTests
{
    private static readonly Regex VerifiedLookup = new(
        @"GetFolderPath\(\s*Environment\.SpecialFolder\.(LocalApplicationData|ApplicationData)\s*\)",
        RegexOptions.CultureInvariant);

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Ntilde.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repository root from test output path.");
    }

    [Fact]
    public void App_data_folders_are_looked_up_without_requiring_them_to_exist()
    {
        string src = Path.Combine(RepoRoot(), "src");
        string bin = $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}";
        string obj = $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}";

        List<string> offenders = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains(bin, StringComparison.Ordinal) && !file.Contains(obj, StringComparison.Ordinal))
            .SelectMany(file => File.ReadAllLines(file)
                .Select((line, index) => (line, index))
                .Where(entry => VerifiedLookup.IsMatch(entry.line))
                .Select(entry => $"{Path.GetRelativePath(src, file)}:{entry.index + 1}"))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Pass Environment.SpecialFolderOption.DoNotVerify when looking up an app-data folder; without it the "
            + "lookup returns \"\" when the folder does not exist yet, and the path built on it turns relative: "
            + string.Join(", ", offenders));
    }
}
