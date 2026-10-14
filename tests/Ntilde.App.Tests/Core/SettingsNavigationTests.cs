using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
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

            // Nothing was held and no fade was scheduled: the control is exactly as it was.
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
}
