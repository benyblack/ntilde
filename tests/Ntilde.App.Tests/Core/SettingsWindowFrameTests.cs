using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ntilde.Settings;
using Ntilde.Settings.Pages;
using Ntilde.Shell;
using Ntilde.Shell.Shortcuts;
using Ntilde.VT;

namespace Ntilde.Tests.Core;

/// <summary>
/// The frame: registry, sidebar, page switching, deep links and Save's validate-first order.
/// Replaces the old tab-offset tests: with one list built from the registry there are no offsets
/// left to get wrong, so what is pinned here is the registry contract itself.
/// </summary>
public sealed class SettingsWindowFrameTests
{
    [Fact]
    public void Registry_HasExactlyOneDescriptorPerPage()
    {
        foreach (SettingsPage page in Enum.GetValues<SettingsPage>())
        {
            Assert.Single(SettingsPageRegistry.Pages, descriptor => descriptor.Page == page);
        }

        Assert.Equal(Enum.GetValues<SettingsPage>().Length, SettingsPageRegistry.Pages.Count);
    }

    [AvaloniaFact]
    public void Sidebar_ListsGroupsAndPagesInRegistryOrder()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();
        string[] expected =
        [
            "GENERAL", "General",
            "INTERFACE", "Appearance", "Shells & Profiles", "Shortcuts",
            "ASSISTANT", "Command Assist", "Agent Access",
            "CONNECTION", "SSH",
            "DATA", "Backup & Restore",
        ];

        Assert.Equal(expected, SidebarLabels(window));
    }

    [AvaloniaFact]
    public void Sidebar_GroupHeadersAreNeverSelected()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow(new SettingsTarget(SettingsPage.Shortcuts));
        var nav = window.FindControl<ListBox>("SideNav")!;
        int shortcuts = nav.Items.IndexOf(window.NavItem(SettingsPage.Shortcuts));

        nav.SelectedIndex = shortcuts + 1; // the ASSISTANT header, travelling down
        Assert.Equal(SettingsPage.CommandAssist, window.CurrentPage);

        nav.SelectedIndex = nav.Items.IndexOf(window.NavItem(SettingsPage.CommandAssist)) - 1; // the same header, travelling up
        Assert.Equal(SettingsPage.Shortcuts, window.CurrentPage);

        Assert.All(nav.Items.OfType<ListBoxItem>().Where(item => item.Tag is not SettingsPage), header => Assert.False(header.IsEnabled));
        Assert.Same(window.NavItem(SettingsPage.Shortcuts), nav.SelectedItem);
    }

    [AvaloniaFact]
    public void SelectingANavItem_ShowsExactlyThatPage()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();
        var nav = window.FindControl<ListBox>("SideNav")!;

        foreach (SettingsPageDescriptor descriptor in SettingsPageRegistry.Pages)
        {
            nav.SelectedItem = window.NavItem(descriptor.Page);

            Assert.Equal(descriptor.Page, window.CurrentPage);
            foreach (SettingsPageDescriptor other in SettingsPageRegistry.Pages)
            {
                Assert.Equal(other.Page == descriptor.Page, window.GetPage(other.Page).View.IsVisible);
            }
        }
    }

    [AvaloniaFact]
    public void Constructing_WithATarget_OpensThatPage()
    {
        using var root = SettingsTestRoot.Empty();
        foreach (SettingsPage page in Enum.GetValues<SettingsPage>())
        {
            var window = new Ntilde.SettingsWindow(new SettingsTarget(page));

            Assert.Equal(page, window.CurrentPage);
            Assert.Same(window.NavItem(page), window.FindControl<ListBox>("SideNav")!.SelectedItem);
        }
    }

    [AvaloniaFact]
    public void ParameterlessConstructor_OpensTheDefaultPage()
    {
        using var root = SettingsTestRoot.Empty();

        Assert.Equal(SettingsPageRegistry.DefaultPage, new Ntilde.SettingsWindow().CurrentPage);
    }

    /// <summary>
    /// The deep-link effect itself: with the row scrolled out of view first, revealing it brings it
    /// back into the viewport and starts the highlight. (At the default size the title-bar row
    /// already sits inside the first viewport, so without scrolling away this would pass even if
    /// the scroll did nothing.)
    /// </summary>
    [AvaloniaFact]
    public void RevealRow_ScrollsTheRowIntoView_AndHighlightsIt()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow(new SettingsTarget(SettingsPage.Appearance));
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var header = window.GetPage<AppearancePage>().FindControl<TextBlock>("TitleBarSectionHeader")!;
            ScrollViewer scroller = header.GetVisualAncestors().OfType<ScrollViewer>().First();
            double top = header.Bounds.Top, bottom = header.Bounds.Bottom;
            scroller.Offset = new Vector(0, scroller.Extent.Height);
            Dispatcher.UIThread.RunJobs();
            Assert.True(scroller.Offset.Y > bottom + 100, "Setup did not scroll the row out of view.");

            Assert.True(window.RevealRow(SettingsPage.Appearance, SettingsRowIds.TitleBar));
            Dispatcher.UIThread.RunJobs();

            Assert.InRange(scroller.Offset.Y, top - 40, bottom + 40);
            Assert.Same(window.FindResource("NtBlueDim"), header.Background);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Constructing_WithARowTarget_HighlightsTheRowOnceOpen()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow(new SettingsTarget(SettingsPage.Appearance, SettingsRowIds.TitleBar));
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var header = window.GetPage<AppearancePage>().FindControl<TextBlock>("TitleBarSectionHeader")!;
            Assert.Same(window.FindResource("NtBlueDim"), header.Background);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Review focus 4. A duplicate shortcut refuses Save, shows Shortcuts and writes nothing.
    /// Once the user fixes the duplicate, the next Save writes the edit they had made elsewhere.
    /// </summary>
    [AvaloniaFact]
    public void DuplicateShortcut_BlocksSaveWithoutWriting_AndTheFixedSaveKeepsEarlierEdits()
    {
        using var root = SettingsTestRoot.WithSettings("""{"FontSize":14}""");
        var window = new Ntilde.SettingsWindow();
        var app = ShortcutCatalog.GetEntries()
            .Where(e => e.Scope == ShortcutScope.App && !string.IsNullOrEmpty(e.DefaultBinding))
            .ToList();
        window.Session.ShortcutDraft[app[1].CommandId] = app[0].DefaultBinding;
        FontSizeInput(window).Value = 19;

        ClickSave(window);

        Assert.Equal(SettingsPage.Shortcuts, window.CurrentPage);
        Assert.True(window.GetPage<ShortcutsPage>().FindControl<TextBlock>("ShortcutValidationMessage")!.IsVisible);
        Assert.Equal(14, window.Session.Settings.FontSize); // nothing collected before validation
        Assert.True(window.Session.Settings.Keybindings.Count == 0, "The refused Save committed the duplicate draft into Settings.Keybindings.");
        Assert.True(ShortcutBindingResolver.Resolve(ShortcutCatalog.GetDefinitions(), window.Session.Settings.Keybindings).IsValid);
        using (var unchanged = root.ReadSettings())
        {
            Assert.Equal(14, unchanged.RootElement.GetProperty("FontSize").GetInt32());
        }

        window.Session.ShortcutDraft.Remove(app[1].CommandId);
        ClickSave(window);

        using var saved = root.ReadSettings();
        Assert.Equal(19, saved.RootElement.GetProperty("FontSize").GetInt32());
    }

    /// <summary>
    /// "Set as default" writes <c>Session.Settings</c> (ProfilesPage), and Save's Collect reads the
    /// same object, so the chosen profile is what lands on disk.
    /// </summary>
    [AvaloniaFact]
    public void SetAsDefaultOnASecondProfile_IsWhatSaveWrites()
    {
        using var root = SettingsTestRoot.Empty();
        Guid first = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Guid second = Guid.Parse("22222222-2222-2222-2222-222222222222");
        new TerminalSettings
        {
            Profiles =
            [
                new TerminalProfile { Id = first, Name = "First", Command = "cmd.exe" },
                new TerminalProfile { Id = second, Name = "Second", Command = "cmd.exe" },
            ],
            DefaultProfileId = first,
        }.Save();
        var window = new Ntilde.SettingsWindow(new SettingsTarget(SettingsPage.Profiles));
        try
        {
            window.Show();
            var list = window.GetPage<ProfilesPage>().FindControl<ListBox>("ProfilesListBox")!;
            list.SelectedItem = list.Items.OfType<ListBoxItem>().Single(item => ((TerminalProfile)item.Tag!).Id == second);
            Assert.Equal(second, ((TerminalProfile)((ListBoxItem)list.SelectedItem!).Tag!).Id);

            window.GetPage<ProfilesPage>().FindControl<Button>("BtnSetDefault")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ClickSave(window);

            using var saved = root.ReadSettings();
            Assert.Equal(second.ToString(), saved.RootElement.GetProperty("DefaultProfileId").GetString());
        }
        finally
        {
            if (window.IsVisible)
            {
                window.Close();
            }
        }
    }

    /// <summary>
    /// The frame re-raises the preview sink as the window's public events, which MainWindow uses for
    /// live preview and the Cancel revert. Each control must reach its own event, with its own
    /// value: the values differ in type or magnitude so two crossed wires cannot both pass.
    /// </summary>
    [AvaloniaFact]
    public void PreviewControls_ReachTheirOwnPublicEvent_WithTheirValue()
    {
        using var root = SettingsTestRoot.WithSettings("""
            {"FontSize":14,"WindowOpacity":1.0,"UiScale":1.0,"BlurEffect":"Acrylic","BackgroundImagePath":"",
             "BackgroundImageOpacity":0.5,"BackgroundImageStretch":"UniformToFill","ThemeName":"Default"}
            """);
        root.CopyShippedTheme("GitHubLight.json");
        var window = new Ntilde.SettingsWindow();
        var page = window.GetPage<AppearancePage>();
        var log = new List<string>();
        window.OnFontChanged += v => log.Add($"font:{v}");
        window.OnFontSizeChanged += v => log.Add($"fontsize:{v}");
        window.OnOpacityChanged += v => log.Add($"opacity:{v}");
        window.OnUiScaleChanged += v => log.Add($"uiscale:{v}");
        window.OnBlurChanged += v => log.Add($"blur:{v}");
        window.OnBgImageChanged += (p, o, s) => log.Add($"bg:{p}|{o}|{s}");
        window.OnThemeChanged += v => log.Add($"theme:{v}");
        int historyCleared = 0;
        window.OnCommandAssistHistoryCleared += () => historyCleared++;

        // Each step changes one control and expects exactly the event(s) listed, in order.
        void Step(Action change, params string[] expected)
        {
            log.Clear();
            change();
            Assert.Equal(expected, log);
        }

        var fontList = page.FindControl<ComboBox>("FontList")!;
        ComboBoxItem otherFont = fontList.Items.OfType<ComboBoxItem>().First(i => i != fontList.SelectedItem);
        Step(() => fontList.SelectedItem = otherFont, $"font:{otherFont.Content}");

        Step(() => page.FindControl<NumericUpDown>("FontSizeInput")!.Value = 18, "fontsize:18");

        var opacity = page.FindControl<Slider>("WindowOpacitySlider")!;
        Step(() => opacity.Value = 0.6, $"opacity:{0.6}");

        var uiScale = page.FindControl<Slider>("UiScaleSlider")!;
        Step(() => uiScale.Value = 1.5, $"uiscale:{UiScale.Clamp(1.5)}");

        var blur = page.FindControl<ComboBox>("BlurList")!;
        Step(() => blur.SelectedItem = blur.Items.OfType<ComboBoxItem>().Single(i => (string?)i.Content == "Mica"), "blur:Mica");

        // The three background-image controls share one event carrying all three values.
        Step(() => page.FindControl<TextBox>("BgImagePathInput")!.Text = "/pic.png", "bg:/pic.png|0.5|UniformToFill");
        Step(() => page.FindControl<Slider>("BgImageOpacitySlider")!.Value = 0.25, "bg:/pic.png|0.25|UniformToFill");
        var stretch = page.FindControl<ComboBox>("BgImageStretchList")!;
        Step(() => stretch.SelectedItem = stretch.Items.OfType<ComboBoxItem>().Single(i => (string?)i.Content == "Fill"), "bg:/pic.png|0.25|Fill");

        var themes = page.FindControl<ComboBox>("ThemeList")!;
        Step(() => themes.SelectedItem = themes.Items.OfType<ComboBoxItem>().Single(i => (string?)i.Content == "GitHub Light"), "theme:GitHub Light");

        window.Session.RaiseHistoryCleared();
        Assert.Equal(1, historyCleared);
    }

    /// <summary>Where the font size lives. PR 3 moves it to the Terminal page.</summary>
    internal static NumericUpDown FontSizeInput(Ntilde.SettingsWindow window)
        => window.GetPage<AppearancePage>().FindControl<NumericUpDown>("FontSizeInput")!;

    internal static void ClickSave(Ntilde.SettingsWindow window)
        => window.FindControl<Button>("BtnSave")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    internal static List<string> SidebarLabels(Ntilde.SettingsWindow window)
        => window.FindControl<ListBox>("SideNav")!.Items.OfType<ListBoxItem>()
            .Select(item => item.Content switch
            {
                TextBlock header => header.Text!,
                Grid row => row.Children.OfType<TextBlock>().Last().Text!,
                _ => "?",
            })
            .ToList();
}
