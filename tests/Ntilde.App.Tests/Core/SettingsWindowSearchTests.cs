using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Ntilde.Settings;
using Ntilde.Settings.Pages;

namespace Ntilde.Tests.Core;

/// <summary>Search in the Settings window (spec §5).</summary>
public sealed class SettingsWindowSearchTests
{
    private static RawInputModifiers Primary => OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

    private static TextBox Box(Ntilde.SettingsWindow window) => window.FindControl<TextBox>("SettingsSearchBox")!;

    private static ListBox Results(Ntilde.SettingsWindow window) => window.FindControl<ListBox>("SearchResultsList")!;

    private static List<SettingsSearchResult> Shown(Ntilde.SettingsWindow window)
        => Results(window).ItemsSource!.Cast<SettingsSearchResult>().ToList();

    [AvaloniaFact]
    public void Typing_ShowsResultsInPlaceOfThePage()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();

        Box(window).Text = "opacity";

        Assert.False(window.FindControl<Panel>("PageHost")!.IsVisible);
        Assert.True(window.FindControl<Control>("SearchResultsView")!.IsVisible);
        Assert.Equal($"{Shown(window).Count} results for “opacity”", window.FindControl<TextBlock>("SearchResultsHeading")!.Text);
    }

    [AvaloniaFact]
    public void Opacity_FindsWindowAndImageOpacity_OnAppearance_First()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();

        Box(window).Text = "opacity";

        string[] firstTwo = Shown(window).Take(2).Select(r => r.Entry.Title).ToArray();
        Assert.Equal(new[] { "Window opacity", "Image opacity" }, firstTwo);
        Assert.All(Shown(window).Take(2), r => Assert.Equal(SettingsPage.Appearance, r.Entry.Page));
    }

    [AvaloniaFact]
    public void Enter_JumpsToTheRow_ClearsTheSearch_AndHighlightsTheRow()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow(new SettingsTarget(SettingsPage.Shortcuts));
        try
        {
            window.Show();
            Box(window).Focus();
            window.KeyTextInput("scrollback");
            Dispatcher.UIThread.RunJobs();

            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(SettingsPage.Terminal, window.CurrentPage);
            Assert.Equal(string.Empty, Box(window).Text);
            Assert.True(window.FindControl<Panel>("PageHost")!.IsVisible);
            SettingRow row = window.GetPage<TerminalPage>().GetLogicalDescendants().OfType<SettingRow>().Single(r => r.RowId == "scrollback");
            Assert.Same(window.FindResource("NtBlueDim"), row.Background);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Escape_ClearsTheSearch_AndTheSamePageIsBack()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow(new SettingsTarget(SettingsPage.Shortcuts));
        try
        {
            window.Show();
            Box(window).Focus();
            window.KeyTextInput("blur");

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(string.Empty, Box(window).Text);
            Assert.Equal(SettingsPage.Shortcuts, window.CurrentPage);
            Assert.True(window.GetPage(SettingsPage.Shortcuts).View.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void NoMatches_SaysSo()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();

        Box(window).Text = "zzqx";

        var none = window.FindControl<TextBlock>("SearchNoResults")!;
        Assert.True(none.IsVisible);
        Assert.Equal("No settings match “zzqx”.", none.Text);
        Assert.False(Results(window).IsVisible);
    }

    [AvaloniaFact]
    public void ArrowKeys_MoveThroughTheResults_AndStopAtTheEnds()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();
        try
        {
            window.Show();
            Box(window).Focus();
            window.KeyTextInput("opacity");
            Assert.Equal(0, Results(window).SelectedIndex);

            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            Assert.Equal(1, Results(window).SelectedIndex);
            window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
            window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
            Assert.Equal(0, Results(window).SelectedIndex);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ClickingAResult_JumpsToIt()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();
        try
        {
            window.Show();
            Box(window).Text = "font size";
            Dispatcher.UIThread.RunJobs();
            Control first = Results(window).ContainerFromIndex(0)!;
            Point at = first.TranslatePoint(new Point(10, 10), window)!.Value;

            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(SettingsPage.Terminal, window.CurrentPage);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The list fills the content area, so a click below the last result lands on the list itself, not on a result.</summary>
    [AvaloniaFact]
    public void ClickingBelowTheResults_KeepsTheSearch()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow(new SettingsTarget(SettingsPage.Shortcuts));
        try
        {
            window.Show();
            Box(window).Text = "opacity";
            Dispatcher.UIThread.RunJobs();
            Point at = Results(window).TranslatePoint(new Point(200, 350), window)!.Value;

            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(SettingsPage.Shortcuts, window.CurrentPage);
            Assert.Equal("opacity", Box(window).Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void PrimaryF_FocusesTheSearchBox()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();
        try
        {
            window.Show();

            window.KeyPress(Key.F, Primary, PhysicalKey.F, "f");
            Dispatcher.UIThread.RunJobs();

            Assert.True(Box(window).IsFocused);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void BindingsProfilesAndTheThemeEditor_AreNotIndexed()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();

        List<SettingsSearchEntry> entries = window.SearchIndex.Entries.ToList();

        Assert.Equal(new[] { "Shells & Profiles" }, entries.Where(e => e.Page == SettingsPage.Profiles).Select(e => e.Title));
        Assert.Equal(new[] { "Shortcuts", "Recording a shortcut" }, entries.Where(e => e.Page == SettingsPage.Shortcuts).Select(e => e.Title));
        Assert.DoesNotContain(entries, e => e.Title == "Theme name");
    }
}
