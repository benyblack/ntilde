using Avalonia.Headless.XUnit;
using Ntilde.Settings;

namespace Ntilde.Tests.Core;

/// <summary>
/// Where each MainWindow entry point opens Settings, and the remembered last page. The targets are
/// plain methods because the entry points themselves end in Window.ShowDialog, which never returns
/// in the headless host (see MainWindowShellExitTests).
/// </summary>
public sealed class MainWindowSettingsTargetTests : IDisposable, IClassFixture<TestAppDataRoot>
{
    public void Dispose() => TestMainWindowFactory.DisposeCreatedWindows();

    [AvaloniaFact] // touching MainWindow's statics from a plain [Fact] can poison the headless run
    public void EntryPoints_OpenTheirPages()
    {
        Assert.Equal(new SettingsTarget(SettingsPage.Profiles), Ntilde.MainWindow.ManageProfilesSettingsTarget());
        Assert.Equal(new SettingsTarget(SettingsPage.Backup), Ntilde.MainWindow.BackupSettingsTarget());
        Assert.Equal(new SettingsTarget(SettingsPage.TabsAndTitleBar), Ntilde.MainWindow.CustomizeTitleBarSettingsTarget());
    }

    [AvaloniaFact]
    public void DefaultTarget_IsTheRegistryDefault_UntilSettingsHasClosedOnce()
    {
        var window = TestMainWindowFactory.Create();

        Assert.Equal(new SettingsTarget(SettingsPageRegistry.DefaultPage), window.DefaultSettingsTarget());
    }

    [AvaloniaFact]
    public void DefaultTarget_IsThePageSettingsLastClosedOn_WhicheverWayItClosed()
    {
        var window = TestMainWindowFactory.Create();
        var snapshot = new Ntilde.MainWindow.PreviewSnapshot(1.0, "Acrylic", "", 0.5, "UniformToFill", "Consolas", 14, "Default", 1.0);

        window.ApplySettingsWindowResult(new Ntilde.SettingsWindow(new SettingsTarget(SettingsPage.Shortcuts)), saved: false, snapshot);
        Assert.Equal(SettingsPage.Shortcuts, window.DefaultSettingsTarget().Page);

        window.ApplySettingsWindowResult(new Ntilde.SettingsWindow(new SettingsTarget(SettingsPage.Ssh)), saved: true, snapshot);
        Assert.Equal(SettingsPage.Ssh, window.DefaultSettingsTarget().Page);
    }
}
