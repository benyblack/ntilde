using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Ntilde.Shell;
using Ntilde.Tests.Backup;

namespace Ntilde.Tests.Shell;

/// <summary>
/// OpenSSH launches the askpass helper as a separate process with its own tiny Application, so
/// nothing the main App.axaml sets up reaches it: no Window theme carrying the scale transform,
/// and UiScale.Current at its default. Codex on PR #466: the authentication dialog therefore
/// stayed at 100% for a user who saved 150%. The helper now installs the same window theme the
/// main app uses and applies the saved scale before it opens its window.
/// </summary>
public sealed class SshAskPassUiScaleTests
{
    [AvaloniaFact]
    public void InstallWindowTheme_ProvidesTheTransformAndTheWindowTheme()
    {
        var app = new Application();

        UiScale.InstallWindowTheme(app);

        Assert.True(app.Resources.TryGetResource(UiScale.TransformResourceKey, null, out object? transform));
        Assert.IsType<ScaleTransform>(transform);
        Assert.True(app.Resources.TryGetResource(typeof(Window), null, out object? theme));
        Assert.IsType<ControlTheme>(theme);
    }

    [AvaloniaFact]
    public void AskPassApplication_InstallsTheWindowTheme()
    {
        var app = new SshAskPassCommand.AskPassApplication(new SshAskPassCommand.AskPassState("prompt", new TerminalProfile()));

        app.Initialize();

        Assert.True(app.Resources.TryGetResource(UiScale.TransformResourceKey, null, out object? transform));
        Assert.IsType<ScaleTransform>(transform);
        Assert.True(app.Resources.TryGetResource(typeof(Window), null, out _));
    }

    [AvaloniaFact]
    public void AskPassWindow_IsSizedForTheCurrentScale()
    {
        UiScale.Apply(1.5);
        try
        {
            var window = new SshAskPassCommand.AskPassWindow(
                new SshAskPassCommand.AskPassState("prompt", new TerminalProfile()),
                shutdown: () => { });

            Assert.Equal(520 * 1.5, window.Width, precision: 6);
            Assert.Equal(240 * 1.5, window.Height, precision: 6);
        }
        finally
        {
            UiScale.Apply(1.0);
        }
    }

    [Fact]
    public void ResolveSavedUiScale_ReadsTheUsersSetting()
    {
        using var tree = BackupTestTree.CreateEmpty();
        using var _ = OverrideAppDataRoot(tree.Root);
        File.WriteAllText(Path.Combine(tree.Root, "settings.json"), """{ "UiScale": 1.5 }""");

        Assert.Equal(1.5, SshAskPassCommand.ResolveSavedUiScale());
    }

    [Fact]
    public void ResolveSavedUiScale_FallsBackToDefault_WhenSettingsAreMissing()
    {
        using var tree = BackupTestTree.CreateEmpty();
        using var _ = OverrideAppDataRoot(tree.Root);

        Assert.Equal(UiScale.Default, SshAskPassCommand.ResolveSavedUiScale());
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
