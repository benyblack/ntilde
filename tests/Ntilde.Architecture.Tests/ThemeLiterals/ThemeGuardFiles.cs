namespace Ntilde.Architecture.Tests.ThemeLiterals;

/// <summary>Repo-relative ('/'-separated) file sets the theme guards scan.</summary>
internal static class ThemeGuardFiles
{
    private const string AppSourceRoot = "src/Ntilde.App/";

    // Same walk-up as CompiledBindingTests.RepoRoot(): the test binary's depth below the repo root
    // differs between a local run and CI.
    public static string RepoRoot()
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

    public static string Absolute(string relative) => Path.Combine(RepoRoot(), relative);

    /// <summary>Every .axaml under src/, excluding generated copies in obj/ and bin/.</summary>
    public static IReadOnlyList<string> ShippedXaml() => Enumerate("src", "*.axaml");

    /// <summary>Every .cs of the App project (assembly "Ntilde").</summary>
    public static IReadOnlyList<string> AppSources() => Enumerate(AppSourceRoot.TrimEnd('/'), "*.cs");

    private static List<string> Enumerate(string relativeDirectory, string pattern)
    {
        var root = RepoRoot();
        return Directory.EnumerateFiles(Path.Combine(root, relativeDirectory), pattern, SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(relative => !relative.Contains("/obj/", StringComparison.Ordinal) &&
                               !relative.Contains("/bin/", StringComparison.Ordinal))
            .OrderBy(relative => relative, StringComparer.Ordinal)
            .ToList();
    }
}
