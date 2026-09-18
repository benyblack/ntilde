using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Ntilde.Tests.Backup;
using Xunit;

namespace Ntilde.Tests.Core;

/// <summary>
/// <see cref="Ntilde.SettingsWindow.SelectAgentAccessPage"/>, the navigation the title-bar
/// screen-inference light uses. Same rule as <see cref="Ntilde.SettingsWindow.SelectBackupPage"/>:
/// the tab is located by its header, never by a position, so a reordered or inserted tab cannot
/// silently send the click to the wrong page or stop the key-status refresh.
/// </summary>
public sealed class SettingsWindowSelectAgentAccessPageTests
{
    [AvaloniaFact]
    public void SelectAgentAccessPage_SelectsTheAgentAccessTab()
    {
        using var tree = BackupTestTree.CreateEmpty();
        using var _ = OverrideAppDataRoot(tree.Root);
        var window = new Ntilde.SettingsWindow(0);

        var tabs = window.FindControl<TabControl>("MainTabs")!;
        Assert.Equal(0, tabs.SelectedIndex);

        window.SelectAgentAccessPage();

        Assert.Equal("Agent Access", ((TabItem)tabs.Items[tabs.SelectedIndex]!).Header);
    }

    [AvaloniaFact]
    public void SelectAgentAccessPage_StillSelectsAgentAccess_IfATabIsInsertedAheadOfIt()
    {
        using var tree = BackupTestTree.CreateEmpty();
        using var _ = OverrideAppDataRoot(tree.Root);
        var window = new Ntilde.SettingsWindow(0);
        var tabs = window.FindControl<TabControl>("MainTabs")!;
        tabs.Items.Insert(0, new TabItem { Header = "Inserted" });

        window.SelectAgentAccessPage();

        Assert.Equal("Agent Access", ((TabItem)tabs.Items[tabs.SelectedIndex]!).Header);
    }

    private static IDisposable OverrideAppDataRoot(string root)
    {
        string? previous = Environment.GetEnvironmentVariable("NTILDE_APPDATA_ROOT");
        Environment.SetEnvironmentVariable("NTILDE_APPDATA_ROOT", root);
        return new RestoreEnvVar(previous);
    }

    private sealed class RestoreEnvVar(string? previous) : IDisposable
    {
        public void Dispose() => Environment.SetEnvironmentVariable("NTILDE_APPDATA_ROOT", previous);
    }
}
