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
