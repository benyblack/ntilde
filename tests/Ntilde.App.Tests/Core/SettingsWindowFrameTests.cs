using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ntilde.Settings;
using Ntilde.Settings.Pages;
using Ntilde.Shell.Shortcuts;

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
        using (var unchanged = root.ReadSettings())
        {
            Assert.Equal(14, unchanged.RootElement.GetProperty("FontSize").GetInt32());
        }

        window.Session.ShortcutDraft.Remove(app[1].CommandId);
        ClickSave(window);

        using var saved = root.ReadSettings();
        Assert.Equal(19, saved.RootElement.GetProperty("FontSize").GetInt32());
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
