using System.Text.Json;
using Ntilde.Tests.Backup;

namespace Ntilde.Tests.Core;

/// <summary>
/// A temporary app-data root for one Settings test. Points NTILDE_APPDATA_ROOT at a fresh
/// <see cref="BackupTestTree"/> for its lifetime, so a <see cref="Ntilde.SettingsWindow"/> built
/// inside it never reads or writes the developer's real profile.
/// </summary>
internal sealed class SettingsTestRoot : IDisposable
{
    private const string RootVariable = "NTILDE_APPDATA_ROOT";

    private readonly string? _previousRoot;

    private SettingsTestRoot(BackupTestTree tree)
    {
        Tree = tree;
        _previousRoot = Environment.GetEnvironmentVariable(RootVariable);
        Environment.SetEnvironmentVariable(RootVariable, tree.Root);
    }

    public BackupTestTree Tree { get; }

    public string Root => Tree.Root;

    public static SettingsTestRoot Empty() => new(BackupTestTree.CreateEmpty());

    public static SettingsTestRoot WithSettings(string settingsJson)
    {
        var root = Empty();
        root.Tree.WriteFile("settings.json", settingsJson);
        return root;
    }

    /// <summary>Copies one of the themes the app ships (src/Ntilde.App/themes) into this root.</summary>
    public void CopyShippedTheme(string fileName)
    {
        string source = Path.Combine(RepoRoot(), "src", "Ntilde.App", "themes", fileName);
        Tree.WriteFile(Path.Combine("themes", fileName), File.ReadAllText(source));
    }

    public JsonDocument ReadSettings() => Tree.ReadJson("settings.json");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(RootVariable, _previousRoot);
        Tree.Dispose();
    }

    internal static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Ntilde.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root from the test output path.");
    }
}
