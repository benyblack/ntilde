using System;
using System.Threading;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Ntilde.Settings;

namespace Ntilde.Tests.Core;

public sealed class SettingsNavigationTests
{
    [AvaloniaFact]
    public void RowId_IsAnAttachedPropertyAnyControlCanCarry()
    {
        var block = new TextBlock();

        SettingsNavigation.SetRowId(block, "title-bar");

        Assert.Equal("title-bar", SettingsNavigation.GetRowId(block));
    }

    [AvaloniaFact]
    public void Flash_HighlightsWithThePaletteBrush_WithoutReplacingTheControlsOwnBackground()
    {
        var dim = new SolidColorBrush(Colors.Blue);
        var row = new Border { Background = Brushes.Red };
        var host = new Window { Content = row };
        host.Resources["NtBlueDim"] = dim;
        try
        {
            host.Show();

            SettingsNavigation.Flash(row);

            Assert.Same(dim, row.Background);
            Assert.Same(Brushes.Red, row.GetBaseValue(Border.BackgroundProperty).Value);
        }
        finally
        {
            host.Close();
        }
    }

    [AvaloniaFact]
    public void Flash_OnAControlWithoutABackground_DoesNothing()
    {
        var image = new Image();
        var host = new Window { Content = image };
        host.Resources["NtBlueDim"] = new SolidColorBrush(Colors.Blue);
        try
        {
            host.Show();
            Assert.Null(SettingsNavigation.BackgroundPropertyOf(image));
            Assert.Null(image.Transitions);

            Assert.Null(Record.Exception(() => SettingsNavigation.Flash(image)));

            // Nothing was held and no fade was scheduled: the control is exactly as it was,
            // including after the flash window would have ended.
            Pump(SettingsNavigation.FlashDuration + TimeSpan.FromMilliseconds(300));
            Assert.Null(image.Transitions);
        }
        finally
        {
            host.Close();
        }
    }

    [AvaloniaFact]
    public void FindPaletteBrush_ReturnsTheInstanceTheWindowHolds()
    {
        var brush = new SolidColorBrush(Colors.Green);
        var child = new Border();
        var host = new Window { Content = child };
        host.Resources["NtPanel"] = brush;

        Assert.Same(brush, child.FindPaletteBrush("NtPanel"));
    }

    [AvaloniaFact]
    public void Flash_EndsByItself_RestoringTheBaseBackgroundAndTransitions()
    {
        var dim = new SolidColorBrush(Colors.Blue);
        var row = new Border { Background = Brushes.Red };
        var host = new Window { Content = row };
        host.Resources["NtBlueDim"] = dim;
        try
        {
            host.Show();
            Transitions? before = row.Transitions;

            SettingsNavigation.Flash(row);
            Assert.Same(dim, row.Background);
            Pump(SettingsNavigation.FlashDuration + TimeSpan.FromMilliseconds(300));

            Assert.Same(Brushes.Red, row.Background);
            Assert.Same(before, row.Transitions);
        }
        finally
        {
            host.Close();
        }
    }

    [AvaloniaFact]
    public void Flash_StartedInsideTheFadeWindowOfAnother_LeavesNoStaleTransition()
    {
        var dim = new SolidColorBrush(Colors.Blue);
        var row = new Border { Background = Brushes.Red };
        var host = new Window { Content = row };
        host.Resources["NtBlueDim"] = dim;
        try
        {
            host.Show();
            Transitions? before = row.Transitions;

            SettingsNavigation.Flash(row);
            // Wait until the first flash's fade has begun (its temporary Transitions is installed).
            PumpUntil(() => row.Transitions is not null, TimeSpan.FromSeconds(3));
            Assert.NotNull(row.Transitions);

            SettingsNavigation.Flash(row);
            Pump(SettingsNavigation.FlashDuration + TimeSpan.FromMilliseconds(500));

            Assert.Same(Brushes.Red, row.Background);
            Assert.Same(before, row.Transitions);
        }
        finally
        {
            host.Close();
        }
    }

    // Runs the real dispatcher loop (timers only fire inside it, not in RunJobs) for the duration.
    private static void Pump(TimeSpan duration)
    {
        using var cts = new CancellationTokenSource(duration);
        Dispatcher.UIThread.MainLoop(cts.Token);
    }

    private static void PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        DateTime end = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < end)
        {
            Pump(TimeSpan.FromMilliseconds(50));
        }
    }
}