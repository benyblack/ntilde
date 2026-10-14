using Avalonia.Headless.XUnit;

namespace Ntilde.Tests.Core;

/// <summary>
/// <see cref="SettingsTestRoot"/> promises an empty root. AppPaths seeds the shipped themes into
/// whichever root is current the first time it runs in a process, so without the constructor's sweep
/// the first root of a targeted run would hold them and later ones would not.
/// </summary>
public sealed class SettingsTestRootTests
{
    [AvaloniaFact]
    public void EmptyRoot_HasNoThemes_EvenAfterAWindowIsBuilt()
    {
        using var root = SettingsTestRoot.Empty();

        var window = new Ntilde.SettingsWindow();
        window.Close();

        string themes = Path.Combine(root.Root, "themes");
        string[] files = Directory.Exists(themes) ? Directory.GetFiles(themes, "*", SearchOption.AllDirectories) : [];
        Assert.Empty(files);
    }

    [AvaloniaFact]
    public void CopyShippedTheme_LandsExactlyThatOneFile()
    {
        using var root = SettingsTestRoot.Empty();
        root.CopyShippedTheme("GitHubLight.json");

        var window = new Ntilde.SettingsWindow();
        window.Close();

        string[] files = Directory.GetFiles(Path.Combine(root.Root, "themes"), "*", SearchOption.AllDirectories);
        Assert.Equal("GitHubLight.json", Path.GetFileName(Assert.Single(files)));
    }
}
