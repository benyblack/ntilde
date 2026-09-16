using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.Shell;
using Ntilde.Tests.Backup;

namespace Ntilde.Tests.Core;

/// <summary>
/// Interface scale previews live in every window - except the Settings window that hosts the
/// slider. When it rescaled itself under the pointer the thumb ran away from the cursor and the
/// slider was next to impossible to drag. So the Settings window opens at whatever scale is
/// current and holds it until it closes; the main window and any other open window preview live.
/// </summary>
public sealed class SettingsWindowUiScaleTests
{
    [AvaloniaFact]
    public void SettingsWindow_OpensAtTheCurrentInterfaceScale()
    {
        using var tree = BackupTestTree.CreateEmpty();
        using var _ = OverrideAppDataRoot(tree.Root);
        UiScale.Apply(1.5);
        var settings = new Ntilde.SettingsWindow();
        try
        {
            settings.Show();
            Dispatcher.UIThread.RunJobs();
            settings.UpdateLayout();

            Assert.Equal(1.5, MeasureScale(settings.FindControl<TabControl>("MainTabs")!, settings), precision: 2);
        }
        finally
        {
            settings.Close();
            UiScale.Apply(1.0);
        }
    }

    [AvaloniaFact]
    public void SettingsWindow_HoldsItsOpeningScale_WhileOtherWindowsPreviewLive()
    {
        using var tree = BackupTestTree.CreateEmpty();
        using var _ = OverrideAppDataRoot(tree.Root);
        UiScale.Apply(1.0);
        var settings = new Ntilde.SettingsWindow();
        var probe = new Border { Width = 100, Height = 80 };
        var other = new Window { Width = 400, Height = 300, Content = probe };
        try
        {
            settings.Show();
            other.Show();
            Dispatcher.UIThread.RunJobs();
            settings.UpdateLayout();
            other.UpdateLayout();

            UiScale.Apply(1.5);
            Dispatcher.UIThread.RunJobs();
            settings.UpdateLayout();
            other.UpdateLayout();

            Assert.Equal(1.5, MeasureScale(probe, other), precision: 2);
            Assert.Equal(1.0, MeasureScale(settings.FindControl<TabControl>("MainTabs")!, settings), precision: 2);
        }
        finally
        {
            other.Close();
            settings.Close();
            UiScale.Apply(1.0);
        }
    }

    /// <summary>
    /// Pinning alone shrinks the logical room (Codex P2 on PR #466: at 200% the scale slider
    /// itself was clipped off the right edge). The window is also sized for its pinned scale, so
    /// the 880x620 layout keeps its 880x620 of logical room. Headless screens are large enough
    /// that no clamping applies at 150%.
    /// </summary>
    [AvaloniaFact]
    public void SettingsWindow_IsSizedForItsPinnedScale()
    {
        using var tree = BackupTestTree.CreateEmpty();
        using var _ = OverrideAppDataRoot(tree.Root);
        UiScale.Apply(1.5);
        try
        {
            var settings = new Ntilde.SettingsWindow();

            Assert.Equal(880 * 1.5, settings.Width, precision: 3);
            Assert.Equal(620 * 1.5, settings.Height, precision: 3);
            Assert.Equal(780 * 1.5, settings.MinWidth, precision: 3);
            Assert.Equal(520 * 1.5, settings.MinHeight, precision: 3);
        }
        finally
        {
            UiScale.Apply(1.0);
        }
    }

    /// <summary>Visible width in window coordinates divided by the control's own layout width.</summary>
    private static double MeasureScale(Control control, Window window)
    {
        Point? origin = control.TranslatePoint(new Point(0, 0), window);
        Point? corner = control.TranslatePoint(new Point(control.Bounds.Width, 0), window);
        Assert.NotNull(origin);
        Assert.NotNull(corner);
        Assert.True(control.Bounds.Width > 0, "control must be laid out");
        return (corner!.Value.X - origin!.Value.X) / control.Bounds.Width;
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
