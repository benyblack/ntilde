using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ntilde.Controls;
using Ntilde.Settings;
using Ntilde.Settings.Pages;
using Ntilde.Shell.Shortcuts;

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

            // The clicked result's container went away with the results; focus must not stay on it.
            Assert.Same(Box(window), window.FocusManager!.GetFocusedElement());
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

    [AvaloniaFact]
    public void ChoosingAPageInTheSidebar_ClearsTheSearch_AndShowsThePage()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();
        try
        {
            window.Show();
            Box(window).Text = "opacity";

            window.FindControl<ListBox>("SideNav")!.SelectedItem = window.NavItem(SettingsPage.Terminal);
            Dispatcher.UIThread.RunJobs();

            AssertPageShown(window, SettingsPage.Terminal);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The page already selected in the sidebar gets no selection change, so the click itself must end the search.</summary>
    [AvaloniaFact]
    public void ClickingTheCurrentPageInTheSidebar_ClearsTheSearch()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow(new SettingsTarget(SettingsPage.Terminal));
        try
        {
            window.Show();
            Box(window).Text = "opacity";
            Dispatcher.UIThread.RunJobs();
            Point at = window.NavItem(SettingsPage.Terminal).TranslatePoint(new Point(20, 10), window)!.Value;

            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            AssertPageShown(window, SettingsPage.Terminal);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ArrowKeysInTheSidebar_ClearTheSearch()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow(new SettingsTarget(SettingsPage.Shortcuts));
        try
        {
            window.Show();
            Box(window).Text = "opacity";
            window.NavItem(SettingsPage.Shortcuts).Focus();

            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            Dispatcher.UIThread.RunJobs();

            AssertPageShown(window, SettingsPage.CommandAssist);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A Save stopped by a page's validation shows that page and its message, not the results it was typed over.</summary>
    [AvaloniaFact]
    public void ARejectedSave_ShowsTheProblemPage_InPlaceOfTheResults()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();
        var app = ShortcutCatalog.GetEntries()
            .Where(e => e.Scope == ShortcutScope.App && !string.IsNullOrEmpty(e.DefaultBinding))
            .ToList();
        window.Session.ShortcutDraft[app[1].CommandId] = app[0].DefaultBinding;
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            window.Show();
            Box(window).Focus();
            window.KeyTextInput("opacity");

            window.KeyPress(Key.S, Primary, PhysicalKey.S, "s");
            Dispatcher.UIThread.RunJobs();

            Assert.False(closed);
            AssertPageShown(window, SettingsPage.Shortcuts);
            Assert.True(window.GetPage<ShortcutsPage>().FindControl<TextBlock>("ShortcutValidationMessage")!.IsEffectivelyVisible);
        }
        finally
        {
            if (window.IsVisible)
            {
                window.Close();
            }
        }
    }

    [AvaloniaFact]
    public void EachResult_HasAnAccessibleName()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();
        try
        {
            window.Show();
            Box(window).Text = "opacity";
            Dispatcher.UIThread.RunJobs();

            AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(Results(window).ContainerFromIndex(0)!);

            Assert.Equal("Window opacity — Appearance › Window", peer.GetName());
            Assert.Equal("Applies when the window is focused.", peer.GetHelpText());
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Controller ruling: at the default 880 × 620 all ten pages fit under the search box without scrolling.</summary>
    [AvaloniaFact]
    public void AtTheDefaultSize_EveryPageFitsInTheSidebar()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new Size(880, 620), window.ClientSize);

            ScrollViewer scroller = window.FindControl<ListBox>("SideNav")!.GetVisualDescendants().OfType<ScrollViewer>().First();

            // The items presenter, the list's padding included. Its desired height is what the pages
            // need; Extent would not do, as it never reports less than the viewport.
            double need = ((Control)scroller.Content!).DesiredSize.Height;
            Assert.True(need <= scroller.Viewport.Height, $"The sidebar scrolls: its pages need {need} px, it has {scroller.Viewport.Height} px.");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void AResult_SaysWhereItLives_InSentenceCase()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();
        try
        {
            window.Show();
            Box(window).Text = "image opacity";
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("Appearance › Background image", LocationShown(window, 0));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ResultLocation_KeepsAcronymsUpperCase_AndNamesAPageAsAPage()
    {
        Assert.Equal("SSH › SSH agent", Ntilde.SettingsWindow.ResultLocation(Row(SettingsPage.Ssh, "SSH", "SSH AGENT")));
        Assert.Equal("General › Progress (OSC 9;4)", Ntilde.SettingsWindow.ResultLocation(Row(SettingsPage.General, "General", "PROGRESS (OSC 9;4)")));
        Assert.Equal("Terminal", Ntilde.SettingsWindow.ResultLocation(Row(SettingsPage.Terminal, "Terminal", null)));
        Assert.Equal("Page", Ntilde.SettingsWindow.ResultLocation(new SettingsSearchEntry(SettingsPage.Terminal, "Terminal", null, null, "Terminal", null, null)));

        static SettingsSearchEntry Row(SettingsPage page, string pageTitle, string? group)
            => new(page, pageTitle, group, "row", "Row", null, null);
    }

    /// <summary>A page entry has no row to reveal: Enter opens the page itself, and the result does not print the page name twice.</summary>
    [AvaloniaFact]
    public void APageResult_SaysPage_AndEnterOpensThePage()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();
        try
        {
            window.Show();
            Box(window).Focus();
            window.KeyTextInput("backup");
            Dispatcher.UIThread.RunJobs();
            SettingsSearchResult page = Shown(window).Single(r => r.Entry.Page == SettingsPage.Backup && r.Entry.RowId is null);
            Results(window).SelectedItem = page;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Page", LocationShown(window, Results(window).SelectedIndex));

            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Dispatcher.UIThread.RunJobs();

            AssertPageShown(window, SettingsPage.Backup);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void EnterInTheResultsList_JumpsToTheSelectedResult()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();
        try
        {
            window.Show();
            Box(window).Text = "scrollback";
            Dispatcher.UIThread.RunJobs();
            Results(window).ContainerFromIndex(0)!.Focus();

            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Dispatcher.UIThread.RunJobs();

            AssertPageShown(window, SettingsPage.Terminal);
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertPageShown(Ntilde.SettingsWindow window, SettingsPage page)
    {
        Assert.Equal(page, window.CurrentPage);
        Assert.Equal(string.Empty, Box(window).Text);
        Assert.True(window.FindControl<Panel>("PageHost")!.IsVisible);
        Assert.False(window.FindControl<Control>("SearchResultsView")!.IsVisible);
        Assert.True(window.GetPage(page).View.IsVisible);
    }

    /// <summary>The first line of a result: where the setting lives.</summary>
    private static string? LocationShown(Ntilde.SettingsWindow window, int index)
        => Results(window).ContainerFromIndex(index)!.GetVisualDescendants().OfType<TextBlock>().First(t => t is not MatchHighlightTextBlock).Text;
}
