using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
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

    private static Button ClearButton(Ntilde.SettingsWindow window) => Assert.IsType<Button>(window.FindControl<Button>("SearchClearButton"));

    /// <summary>Esc clears the search, but a mouse user needs something to see and click (#528).</summary>
    [AvaloniaFact]
    public void TheClearButton_ShowsOnlyWhileTheBoxHasText()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Button clear = ClearButton(window);
            Assert.False(clear.IsVisible);

            Box(window).Text = "blur";
            Dispatcher.UIThread.RunJobs();
            Assert.True(clear.IsVisible);

            Box(window).Text = string.Empty;
            Dispatcher.UIThread.RunJobs();
            Assert.False(clear.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ClickingTheClearButton_ClearsTheSearch_TheSamePageIsBack_AndFocusStaysInTheBox()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow(new SettingsTarget(SettingsPage.Shortcuts));
        try
        {
            window.Show();
            Box(window).Focus();
            window.KeyTextInput("blur");
            Dispatcher.UIThread.RunJobs();
            Button clear = ClearButton(window);
            Point at = clear.TranslatePoint(new Point(clear.Bounds.Width / 2, clear.Bounds.Height / 2), window)!.Value;

            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(string.Empty, Box(window).Text);
            Assert.Equal(SettingsPage.Shortcuts, window.CurrentPage);
            Assert.True(window.GetPage(SettingsPage.Shortcuts).View.IsVisible);
            Assert.True(Box(window).IsFocused, "focus left the search box");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void TheClearButton_IsNamedForScreenReaders_AndStaysOutOfTheTabOrder()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();
        try
        {
            window.Show();
            Box(window).Text = "blur";
            Dispatcher.UIThread.RunJobs();
            Button clear = ClearButton(window);

            Assert.Equal("Clear search", ControlAutomationPeer.CreatePeerForElement(clear).GetName());
            Assert.Equal("Clear search (Esc)", ToolTip.GetTip(clear));
            Assert.False(clear.Focusable); // Esc is the keyboard's way out
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The results list recycles its row containers, so a container named for one result can be reused for another: each
    /// must be named for the result it shows now, or a screen reader reads the old one (#528).
    /// </summary>
    [AvaloniaFact]
    public void RecycledResultContainers_AreNamedForTheResultTheyShowNow()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();
        try
        {
            window.Show();
            ListBox list = Results(window);
            Box(window).Text = "er"; // many results, so the list scrolls and realizes containers as it goes
            Dispatcher.UIThread.RunJobs();
            list.ScrollIntoView(Shown(window).Count - 1);
            Dispatcher.UIThread.RunJobs();
            List<Control> before = list.GetRealizedContainers().ToList();

            Box(window).Text = "opacity"; // a new query fills the list again, reusing containers
            Dispatcher.UIThread.RunJobs();

            List<Control> after = list.GetRealizedContainers().ToList();
            Assert.Contains(after, before.Contains); // setup: at least one container was reused
            Assert.All(after, container =>
            {
                var result = (SettingsSearchResult)list.Items[list.IndexFromContainer(container)]!;
                Assert.Equal($"{result.Entry.Title} — {Ntilde.SettingsWindow.ResultLocation(result.Entry)}", AutomationProperties.GetName(container));
            });
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

            int last = Shown(window).Count - 1;
            for (int i = 0; i <= last + 1; i++)
            {
                window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            }

            Assert.Equal(last, Results(window).SelectedIndex);
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

            // Not the clicked result's container, which went away with the results: the setting.
            AssertFocusIsIn(window, window.GetPage<TerminalPage>().FindControl<NumericUpDown>("FontSizeInput")!);
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

    /// <summary>
    /// Esc ends a search wherever focus is, not only in the box or the results: on the selected sidebar item nothing
    /// else wants it, and the results stayed open (Greptile on #526). Focus stays where it was.
    /// </summary>
    [AvaloniaFact]
    public void Escape_OnTheSelectedSidebarItem_ClearsTheSearch()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow(new SettingsTarget(SettingsPage.Shortcuts));
        try
        {
            window.Show();
            Box(window).Text = "opacity";
            window.NavItem(SettingsPage.Shortcuts).Focus();

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Dispatcher.UIThread.RunJobs();

            AssertPageShown(window, SettingsPage.Shortcuts);
            Assert.Same(window.NavItem(SettingsPage.Shortcuts), window.FocusManager!.GetFocusedElement());
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

    /// <summary>The pages write their group headers as they read; the page still shows them in capitals.</summary>
    [AvaloniaFact]
    public void APagesGroupHeader_IsWrittenAsItReads_AndShownInCapitals()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow(new SettingsTarget(SettingsPage.Appearance));
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            SettingGroup group = Assert.Single(
                window.GetPage<AppearancePage>().GetLogicalDescendants().OfType<SettingGroup>(), g => g.Header == "Background image");
            Assert.Contains(group.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "BACKGROUND IMAGE" && t.Classes.Contains("SectionHeader"));
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

    /// <summary>A group header is written as it reads, so a result shows it as is - an acronym needs no list (#528).</summary>
    [AvaloniaFact]
    public void ResultLocation_WritesTheGroupAsAuthored_AndNamesAPageAsAPage()
    {
        Assert.Equal("SSH › SSH agent", Ntilde.SettingsWindow.ResultLocation(Row(SettingsPage.Ssh, "SSH", "SSH agent")));
        Assert.Equal("General › Progress (OSC 9;4)", Ntilde.SettingsWindow.ResultLocation(Row(SettingsPage.General, "General", "Progress (OSC 9;4)")));
        Assert.Equal("Shells & Profiles › Remote WSL hosts", Ntilde.SettingsWindow.ResultLocation(Row(SettingsPage.Profiles, "Shells & Profiles", "Remote WSL hosts")));
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

    /// <summary>
    /// A page result has no row to take focus, so the page's sidebar item takes it: a screen reader says where the
    /// jump landed, and Tab goes on into the page. It used to stay in the emptied search box.
    /// </summary>
    [AvaloniaFact]
    public void EnterOnAPageResult_FocusesThePagesSidebarItem()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();
        try
        {
            window.Show();
            Box(window).Focus();
            window.KeyTextInput("backup");
            Dispatcher.UIThread.RunJobs();
            Results(window).SelectedItem = Shown(window).Single(r => r.Entry.Page == SettingsPage.Backup && r.Entry.RowId is null);
            Dispatcher.UIThread.RunJobs();

            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Dispatcher.UIThread.RunJobs();

            var focused = window.FocusManager!.GetFocusedElement() as Control;
            Assert.Same(window.NavItem(SettingsPage.Backup), focused);
            Assert.True(focused!.Classes.Contains(":focus-visible"), "a keyboard jump draws the focus ring");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ClickingAPageResult_FocusesThePagesSidebarItem_WithoutAFocusRing()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();
        try
        {
            window.Show();
            Box(window).Text = "backup";
            Dispatcher.UIThread.RunJobs();
            int index = Shown(window).FindIndex(r => r.Entry.Page == SettingsPage.Backup && r.Entry.RowId is null);
            Results(window).ScrollIntoView(index);
            Dispatcher.UIThread.RunJobs();
            Point at = Results(window).ContainerFromIndex(index)!.TranslatePoint(new Point(10, 10), window)!.Value;

            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            var focused = window.FocusManager!.GetFocusedElement() as Control;
            Assert.Same(window.NavItem(SettingsPage.Backup), focused);
            Assert.False(focused!.Classes.Contains(":focus-visible"));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Focus stays in the box while the results change, so the count is read out by a live region, politely, between keystrokes.</summary>
    [AvaloniaFact]
    public void TheResultCountAndTheNoResultsText_ArePoliteLiveRegions()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();

        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(window.FindControl<TextBlock>("SearchResultsHeading")!));
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(window.FindControl<TextBlock>("SearchNoResults")!));
    }

    /// <summary>
    /// ↑/↓ in the box moves the selection but not focus, so a screen reader would not hear the result: the
    /// announcer, an invisible polite live region, takes its name.
    /// </summary>
    [AvaloniaFact]
    public void ArrowingThroughResults_PutsTheSelectedResultInTheAnnouncer()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();
        try
        {
            window.Show();
            Box(window).Focus();
            window.KeyTextInput("font");
            Dispatcher.UIThread.RunJobs();
            Assert.True(Results(window).ItemCount > 1, "precondition: more than one result");

            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            Dispatcher.UIThread.RunJobs();

            var announcer = window.FindControl<TextBlock>("SearchSelectionAnnouncer")!;
            var selected = (SettingsSearchResult)Results(window).SelectedItem!;
            Assert.Equal($"{selected.Entry.Title} — {Ntilde.SettingsWindow.ResultLocation(selected.Entry)}", announcer.Text);
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(announcer));
            Assert.Equal(0, announcer.Opacity);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A new query or the end of the search empties the announcer, so the next arrow is read even when it lands on the same name.</summary>
    [AvaloniaFact]
    public void EndingTheSearch_EmptiesTheAnnouncer()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();
        try
        {
            window.Show();
            Box(window).Focus();
            window.KeyTextInput("font");
            Dispatcher.UIThread.RunJobs();
            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            Dispatcher.UIThread.RunJobs();
            var announcer = window.FindControl<TextBlock>("SearchSelectionAnnouncer")!;
            Assert.False(string.IsNullOrEmpty(announcer.Text), "precondition: the arrow filled the announcer");

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Dispatcher.UIThread.RunJobs();

            Assert.True(string.IsNullOrEmpty(announcer.Text));
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

    /// <summary>The chord goes to the focused binding editor, which may be recording Ctrl+F as a binding.</summary>
    [AvaloniaFact]
    public void PrimaryF_InAShortcutEditor_IsLeftToTheEditor()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow(new SettingsTarget(SettingsPage.Shortcuts));
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            TextBox editor = SettingsPageTests.BindingEditorFor(window.GetPage<ShortcutsPage>(), "Settings");
            editor.Focus();

            window.KeyPress(Key.F, Primary, PhysicalKey.F, "f");
            Dispatcher.UIThread.RunJobs();

            Assert.False(Box(window).IsFocused);
            Assert.True(editor.IsFocused);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Command Assist rewrites its descriptions on a rebind, and a search finds the new text.</summary>
    [AvaloniaFact]
    public void ARebind_IsSearchable_ByItsNewChord()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();
        Assert.Empty(window.SearchIndex.Search("Ctrl+Alt+H"));

        window.Session.ShortcutDraft["command_assist_history"] = "Ctrl+Alt+H";
        window.Session.RaiseShortcutsChanged();
        Box(window).Text = "Ctrl+Alt+H";
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(Shown(window), r => r.Entry.RowId == "remember-commands");
    }

    /// <summary>
    /// The index is read from the rows when a search starts, so a row a page rewrote is found by its new text whether or
    /// not anything told the window - search no longer depends on subscribing to ShortcutsChanged after the pages (#528).
    /// </summary>
    [AvaloniaFact]
    public void ARowRewrittenWithNoEvent_IsFoundByItsNewText_WhenASearchStarts()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();
        SettingRow row = window.GetPage<TerminalPage>().GetLogicalDescendants().OfType<SettingRow>().Single(r => r.RowId == "scrollback");

        row.Description = "Lines kept for zanzibar";
        Box(window).Text = "zanzibar";
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(Shown(window), r => r.Entry.RowId == "scrollback");
    }

    /// <summary>An Import or Restore reloads every page, and search reads the reloaded text.</summary>
    [AvaloniaFact]
    public async Task AnImportOrRestore_IsSearchable_ByTheChordItBrought()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();
        Assert.Empty(window.SearchIndex.Search("Ctrl+Alt+H"));
        root.Tree.WriteFile("settings.json", """{"Keybindings":{"command_assist_history":"Ctrl+Alt+H"}}""");

        await window.ReloadSettingsAfterExternalChangeAsync();

        Assert.Contains(window.SearchIndex.Search("Ctrl+Alt+H"), r => r.Entry.RowId == "remember-commands");
    }

    /// <summary>After a jump the setting has keyboard focus, so Tab and a screen reader carry on from it, not from the emptied box.</summary>
    [AvaloniaFact]
    public void Enter_MovesKeyboardFocusToTheSetting()
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

            AssertFocusIsIn(window, window.GetPage<TerminalPage>().FindControl<NumericUpDown>("ScrollbackInput")!);

            // A keyboard jump focuses as Tab would, so the focus ring shows where the user landed.
            var focused = (StyledElement)window.FocusManager!.GetFocusedElement()!;
            Assert.Contains(":focus-visible", focused.Classes);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Only a search moves focus: a deep link (Settings opened at a row, a rejected Save) leaves it where it was.</summary>
    [AvaloniaFact]
    public void ADeepLink_LeavesFocusAlone()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow(new SettingsTarget(SettingsPage.Shortcuts));
        try
        {
            window.Show();
            Box(window).Focus();

            window.NavigateTo(new SettingsTarget(SettingsPage.Terminal, "scrollback"));
            Dispatcher.UIThread.RunJobs();

            Assert.Same(Box(window), window.FocusManager!.GetFocusedElement());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData("background opacity", "background-image-opacity")]
    [InlineData("size font", "font-size")]
    [InlineData("font size", "font-size")]
    [InlineData("font ligatures", "ligatures")]
    [InlineData("tab orientation", "tab-orientation")]
    [InlineData("window blur", "blur-effect")]
    public void AMultiWordQuery_FindsTheRow_WhateverTheWordOrder(string query, string rowId)
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();

        Assert.Equal(rowId, window.SearchIndex.Search(query).FirstOrDefault()?.Entry.RowId);
    }

    /// <summary>A page whose settings are actions, or lists rather than rows, is found by what it does.</summary>
    [AvaloniaTheory]
    [InlineData("import", SettingsPage.Backup)]
    [InlineData("export", SettingsPage.Backup)]
    [InlineData("snapshot", SettingsPage.Backup)]
    [InlineData("wsl", SettingsPage.Profiles)]
    [InlineData("keybindings", SettingsPage.Shortcuts)]
    public void APage_IsFoundByItsKeywords(string query, SettingsPage page)
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();

        Assert.Contains(window.SearchIndex.Search(query), r => r.Entry.Page == page && r.Entry.RowId is null);
    }

    /// <summary>The text behind "More" is searched too; a jump to a row found only there opens it, so the match is on screen.</summary>
    [AvaloniaFact]
    public void AWordOnlyInARowsDetails_FindsTheRow_AndTheJumpOpensTheDetails()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();
        try
        {
            window.Show();
            Box(window).Focus();
            window.KeyTextInput("password");
            Dispatcher.UIThread.RunJobs();
            Results(window).SelectedItem = Shown(window).Single(r => r.Entry.RowId == "remember-commands");

            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Dispatcher.UIThread.RunJobs();

            SettingRow row = window.GetPage<CommandAssistPage>().GetLogicalDescendants().OfType<SettingRow>().Single(r => r.RowId == "remember-commands");
            Assert.True(row.IsExpanded);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// New results start with the first one selected, so the list must be back at the top to show
    /// it - whether the old results were scrolled by the arrows (the selection moved) or the wheel
    /// (it did not).
    /// </summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void NewResults_ScrollBackToTheTop(bool byArrows)
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();
        try
        {
            window.Show();
            Box(window).Focus();
            window.KeyTextInput("e");
            Dispatcher.UIThread.RunJobs();
            ScrollViewer scroller = Results(window).GetVisualDescendants().OfType<ScrollViewer>().First();
            if (byArrows)
            {
                for (int i = 0; i < 30; i++)
                {
                    window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
                }
            }
            else
            {
                scroller.Offset = new Vector(0, 1500);
            }

            Dispatcher.UIThread.RunJobs();
            Assert.True(scroller.Offset.Y > 0, "The list did not scroll; the test proves nothing.");

            window.KeyTextInput("r");
            Dispatcher.UIThread.RunJobs();

            Assert.True(scroller.Extent.Height > 2 * scroller.Viewport.Height, $"{Shown(window).Count} results for “er” barely scroll; the test proves nothing.");
            Assert.Equal(0, Results(window).SelectedIndex);
            Control? first = Results(window).ContainerFromIndex(0);
            Assert.NotNull(first);
            Point top = first.TranslatePoint(default, scroller)!.Value;
            Assert.True(top.Y >= 0 && top.Y < scroller.Viewport.Height, $"The first result is at {top.Y}, outside the {scroller.Viewport.Height} px list.");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void EscapeInTheResultsList_ClearsTheSearch_AndFocusesTheBox()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow(new SettingsTarget(SettingsPage.Shortcuts));
        try
        {
            window.Show();
            Box(window).Text = "opacity";
            Dispatcher.UIThread.RunJobs();
            Results(window).ContainerFromIndex(0)!.Focus();

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Dispatcher.UIThread.RunJobs();

            AssertPageShown(window, SettingsPage.Shortcuts);
            Assert.True(Box(window).IsFocused);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// A row without a RowId is found but cannot be jumped to, and of two rows sharing one on a page
    /// a jump reaches only the first. The only entries without a RowId are the pages themselves.
    /// </summary>
    [AvaloniaFact]
    public void EveryIndexedRow_HasARowId_UniqueOnItsPage()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();
        IReadOnlyList<SettingsSearchEntry> entries = window.SearchIndex.Entries;

        Assert.Equal(SettingsPageRegistry.Pages.Select(d => d.Title), entries.Where(e => e.RowId is null).Select(e => e.Title));
        string[] duplicates = entries
            .Where(e => e.RowId is not null)
            .GroupBy(e => (e.Page, e.RowId))
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key.Page}/{g.Key.RowId}")
            .ToArray();
        Assert.Empty(duplicates);
    }

    /// <summary>
    /// A Ctrl/Cmd+click toggles the selection off the result it lands on, so the jump must follow
    /// the clicked result, not the selection.
    /// </summary>
    [AvaloniaFact]
    public void ClickingAResult_JumpsToTheResultClicked_NotTheSelection()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow(new SettingsTarget(SettingsPage.Shortcuts));
        try
        {
            window.Show();
            Box(window).Text = "scrollback";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, Results(window).SelectedIndex);
            Point at = Results(window).ContainerFromIndex(0)!.TranslatePoint(new Point(10, 10), window)!.Value;

            window.MouseDown(at, MouseButton.Left, Primary);
            window.MouseUp(at, MouseButton.Left, Primary);
            Dispatcher.UIThread.RunJobs();

            AssertPageShown(window, SettingsPage.Terminal);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The match is drawn by weight in the title's own colour (accent-coloured text was hard to read
    /// on the selected result), and the selected result carries the sidebar's accent edge.
    /// </summary>
    [AvaloniaFact]
    public void TheMatch_IsBold_AndTheSelectedResultHasTheSidebarsAccentEdge()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();
        try
        {
            window.Show();
            Box(window).Text = "opacity";
            Dispatcher.UIThread.RunJobs();
            var selected = (ListBoxItem)Results(window).ContainerFromIndex(0)!;

            MatchHighlightTextBlock title = selected.GetVisualDescendants().OfType<MatchHighlightTextBlock>().Single();
            var match = title.Inlines!.OfType<Run>().Single(run => run.Text == "opacity");
            Assert.Equal(FontWeight.Bold, match.FontWeight);
            Assert.Same(window.FindResource("NtFg"), title.Foreground);
            Assert.Same(title.Foreground, match.Foreground); // inherited, not an accent of its own

            ContentPresenter presenter = selected.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
            Assert.Same(window.FindResource("NtBlue"), presenter.BorderBrush);
            Assert.Equal(new Thickness(2, 0, 0, 0), presenter.BorderThickness);
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertFocusIsIn(Ntilde.SettingsWindow window, Control control)
    {
        var focused = window.FocusManager!.GetFocusedElement() as Visual;
        Assert.True(
            focused is not null && (ReferenceEquals(focused, control) || control.IsVisualAncestorOf(focused)),
            $"Focus is on {focused?.GetType().Name ?? "nothing"}, not in {control.Name}.");
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
