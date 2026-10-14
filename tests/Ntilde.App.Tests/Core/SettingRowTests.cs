using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ntilde.Settings;

namespace Ntilde.Tests.Core;

public sealed class SettingRowTests
{
    /// <summary>Hosts <paramref name="content"/> the way a Settings page is hosted: the palette and NtStyles in reach.</summary>
    private static Window Host(Control content)
    {
        var host = new Window { Width = 640, Height = 400, Content = content };
        host.Resources.MergedDictionaries.Add(new NtPalette());
        host.Styles.Add(new NtStyles());
        host.Show();
        Dispatcher.UIThread.RunJobs();
        return host;
    }

    private static T Part<T>(SettingRow row, string name) where T : Control
        => row.GetVisualDescendants().OfType<T>().Single(control => control.Name == name && ReferenceEquals(control.TemplatedParent, row)); // not the hosted content's own PART_ContentPresenter

    [AvaloniaFact]
    public void ShowsTitleAndDescription_WithTheContentBesideThem()
    {
        var toggle = new CheckBox();
        var row = new SettingRow { Title = "Window opacity", Description = "Applies when focused.", Content = toggle };
        var host = Host(row);
        try
        {
            var texts = row.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            Assert.Contains("Window opacity", texts);
            Assert.Contains("Applies when focused.", texts);
            Assert.Same(row, toggle.GetLogicalParent());
            Assert.Equal(1, Grid.GetColumn(Part<ContentPresenter>(row, "PART_ContentPresenter")));
        }
        finally
        {
            host.Close();
        }
    }

    [AvaloniaFact]
    public void Details_StayHiddenUntilMore_AndMoreBecomesLess()
    {
        var row = new SettingRow { Title = "Native SSH backend", Description = "Short.", Details = "The long part." };
        var host = Host(row);
        try
        {
            var details = Part<TextBlock>(row, "PART_Details");
            var more = Part<Button>(row, "PART_MoreToggle");
            Assert.False(details.IsVisible);
            Assert.Equal("More", more.Content);

            more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.True(row.IsExpanded);
            Assert.True(details.IsVisible);
            Assert.Equal("Less", more.Content);
        }
        finally
        {
            host.Close();
        }
    }

    [AvaloniaFact]
    public void NoDetails_MeansNoMoreButton()
    {
        var row = new SettingRow { Title = "Font size" };
        var host = Host(row);
        try
        {
            Assert.False(Part<Button>(row, "PART_MoreToggle").IsVisible);
        }
        finally
        {
            host.Close();
        }
    }

    [AvaloniaFact]
    public void Indent_ShiftsTheRow_AndStacked_PutsTheContentBelow()
    {
        var indented = new SettingRow { Title = "Agent replay export", Indent = true };
        var stacked = new SettingRow { Title = "Remote shell integration", Stacked = true, Content = new Button() };
        var host = Host(new StackPanel { Children = { indented, stacked } });
        try
        {
            Assert.Equal(24, Part<Grid>(indented, "PART_Layout").Margin.Left);
            var presenter = Part<ContentPresenter>(stacked, "PART_ContentPresenter");
            Assert.Equal(1, Grid.GetRow(presenter));
            Assert.Equal(0, Grid.GetColumn(presenter));
        }
        finally
        {
            host.Close();
        }
    }

    [AvaloniaFact]
    public void RowId_IsTheNavigationAttachedProperty_AndStatusShowsWhenSet()
    {
        var row = new SettingRow { Title = "Remember commands", RowId = "remember-commands" };
        var host = Host(row);
        try
        {
            Assert.Equal("remember-commands", SettingsNavigation.GetRowId(row));
            Assert.False(Part<TextBlock>(row, "PART_Status").IsVisible);

            row.Status = "Command history cleared.";
            Dispatcher.UIThread.RunJobs();

            Assert.True(Part<TextBlock>(row, "PART_Status").IsVisible);
        }
        finally
        {
            host.Close();
        }
    }

    [AvaloniaFact]
    public void DrawsItsHairlineWithThePaletteBrushInstance()
    {
        var row = new SettingRow { Title = "Blur effect" };
        var host = Host(row);
        try
        {
            Assert.Same(host.FindResource("NtHairline"), row.BorderBrush);
        }
        finally
        {
            host.Close();
        }
    }

    [AvaloniaFact]
    public void Group_ShowsItsHeader_AndIsItsRowsLogicalParent()
    {
        var first = new SettingRow { Title = "Window opacity" };
        var group = new SettingGroup { Header = "WINDOW", Items = { first } };
        var host = Host(group);
        try
        {
            Assert.Contains(group.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "WINDOW" && t.Classes.Contains("SectionHeader"));
            Assert.Same(group, first.FindLogicalAncestorOfType<SettingGroup>());
        }
        finally
        {
            host.Close();
        }
    }

    /// <summary>3.1: the flash reaches the screen only if the template binds Background onto its root Border.</summary>
    [AvaloniaFact]
    public void Flash_PaintsTheTemplatesRootBorder()
    {
        var row = new SettingRow { Title = "Window opacity" };
        var host = Host(row);
        try
        {
            SettingsNavigation.Flash(row);
            Dispatcher.UIThread.RunJobs();

            var root = row.GetVisualChildren().OfType<Border>().First();
            Assert.Same(host.FindResource("NtBlueDim"), root.Background);
        }
        finally
        {
            host.Close();
        }
    }
}
