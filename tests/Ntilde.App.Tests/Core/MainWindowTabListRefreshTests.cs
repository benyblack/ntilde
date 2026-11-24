using System.Collections.Specialized;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Ntilde.Shell;
using Ntilde.Shell.TitleBar;
using Ntilde.Tests.Controls;

namespace Ntilde.Tests.Core;

public sealed class MainWindowTabListRefreshTests : IDisposable, IClassFixture<TestAppDataRoot>
{
    public MainWindowTabListRefreshTests(TestAppDataRoot appData) => _ = appData;

    public void Dispose() => TestMainWindowFactory.DisposeCreatedWindows();

    [AvaloniaTheory]
    [InlineData("Pinned")]
    [InlineData("Hidden")]
    [Trait("Category", "Regression")]
    public void Visual_refreshes_leave_the_open_dropdown_items_and_layout_in_place(string placement)
    {
        var (window, tabs, flyout) = OpenTabList(placement);
        var entries = flyout.Items.ToArray();
        var first = Assert.IsType<MenuItem>(entries[0]);
        first.Focus();
        Assert.True(first.IsFocused);
        var bounds = first.Bounds;
        var position = first.PointToScreen(default);
        Assert.True(bounds.Width > 0);
        int collectionChanges = 0;
        ((INotifyCollectionChanged)flyout.Items).CollectionChanged += (_, _) => collectionChanges++;

        for (int i = 0; i < 10; i++)
        {
            window.UpdateTabVisuals();
            Dispatcher.UIThread.RunJobs();
        }

        Assert.True(flyout.IsOpen);
        Assert.Equal(0, collectionChanges);
        Assert.Equal(entries, flyout.Items.ToArray());
        Assert.True(first.IsFocused);
        Assert.Equal(bounds, first.Bounds);
        Assert.Equal(position, first.PointToScreen(default));
        Assert.Same(tabs.SelectedItem, tabs.Items[0]);
        flyout.Hide();
    }

    [AvaloniaFact]
    [Trait("Category", "Regression")]
    public void An_open_dropdown_updates_titles_selection_and_commands_without_replacing_items()
    {
        var (window, tabs, flyout) = OpenTabList("Pinned");
        var entries = flyout.Items.ToArray();
        var firstTab = (TabItem)tabs.Items[0]!;
        var secondTab = (TabItem)tabs.Items[1]!;
        var first = Assert.IsType<MenuItem>(entries[0]);
        var second = Assert.IsType<MenuItem>(entries[1]);
        var close = flyout.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Close Current Tab"));
        var pin = flyout.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Pin Tab"));
        var protect = flyout.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Protect Tab"));
        int collectionChanges = 0;
        ((INotifyCollectionChanged)flyout.Items).CollectionChanged += (_, _) => collectionChanges++;

        SetTabState(window, firstTab, "UserTitle", "Updated title");
        SetTabState(window, firstTab, "IsPinned", true);
        SetTabState(window, firstTab, "IsProtected", true);
        window.UpdateTabVisuals();
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("Updated title", Assert.IsType<string>(first.Header));
        Assert.False(close.IsEnabled);
        Assert.Equal("Unpin Tab", pin.Header);
        Assert.Equal("Unprotect Tab", protect.Header);

        tabs.SelectedItem = secondTab;
        window.UpdateTabVisuals();
        Dispatcher.UIThread.RunJobs();

        Assert.False(first.IsChecked);
        Assert.True(second.IsChecked);
        Assert.True(close.IsEnabled);
        Assert.Equal("Pin Tab", pin.Header);
        Assert.Equal("Protect Tab", protect.Header);
        Assert.Equal(0, collectionChanges);
        Assert.Equal(entries, flyout.Items.ToArray());
        Assert.True(flyout.IsOpen);

        first.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Same(firstTab, tabs.SelectedItem);
        Assert.False(flyout.IsOpen);
    }

    [AvaloniaFact]
    public void Tab_changes_reconcile_the_dropdown_order_and_release_removed_entries()
    {
        var (window, tabs, flyout) = OpenTabList("Pinned");
        var firstTab = (TabItem)tabs.Items[0]!;
        var secondTab = (TabItem)tabs.Items[1]!;
        var first = Assert.IsType<MenuItem>(flyout.Items[0]);
        var second = Assert.IsType<MenuItem>(flyout.Items[1]);

        tabs.Items.Remove(firstTab);
        tabs.Items.Add(firstTab);
        window.UpdateTabVisuals();
        Dispatcher.UIThread.RunJobs();

        Assert.Same(second, flyout.Items[0]);
        // Removing the selected tab refreshes the menu before it is added back. Its old
        // entry must be released, while the tab that stayed open keeps its entry.
        Assert.DoesNotContain(first, flyout.Items);
        first = Assert.IsType<MenuItem>(flyout.Items[1]);
        Assert.StartsWith("1. ", Assert.IsType<string>(second.Header));
        Assert.StartsWith("2. ", Assert.IsType<string>(first.Header));

        tabs.Items.Remove(secondTab);
        window.UpdateTabVisuals();
        Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain(second, flyout.Items);
        Assert.Same(first, flyout.Items[0]);
        var addedTab = new TabItem { Header = new TextBlock { Text = "Added" }, Content = new Border() };
        tabs.Items.Add(addedTab);
        window.UpdateTabVisuals();
        Dispatcher.UIThread.RunJobs();

        var added = Assert.IsType<MenuItem>(flyout.Items[1]);
        added.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Same(addedTab, tabs.SelectedItem);
        Assert.False(flyout.IsOpen);
    }

    [AvaloniaTheory]
    [InlineData("Horizontal")]
    [InlineData("Vertical")]
    public void Tab_tooltip_shows_the_full_reported_path_when_the_basename_stays_the_same(string orientation)
    {
        var session = new FakeTerminalSession();
        var services = AppServices.BuildForDesigner();
        services.Settings!.TabStripOrientation = orientation;
        var window = TestMainWindowFactory.Create(services with { SessionFactory = new RecordingSessionFactory(session) });
        var tabs = window.FindControl<TabControl>("Tabs")!;
        window.Show();
        var tab = Assert.IsType<TabItem>(tabs.SelectedItem);
        var header = Assert.IsAssignableFrom<Control>(tab.Header);
        var pane = Assert.IsType<Ntilde.Controls.TerminalPane>(tab.Content);
        var initialTip = Assert.IsType<string>(ToolTip.GetTip(header));
        Assert.DoesNotContain('\n', initialTip);

        pane.Parser!.Process("\u001b]7;file://localhost/first/project\a");
        Dispatcher.UIThread.RunJobs();
        Assert.EndsWith("\n/first/project", Assert.IsType<string>(ToolTip.GetTip(header)));

        pane.Parser.Process("\u001b]7;file://localhost/second/project\a");
        Dispatcher.UIThread.RunJobs();
        Assert.EndsWith("\n/second/project", Assert.IsType<string>(ToolTip.GetTip(header)));
    }

    private static (MainWindow Window, TabControl Tabs, MenuFlyout Flyout) OpenTabList(string placement)
    {
        var services = AppServices.BuildForDesigner();
        services.Settings!.TitleBarItems[TitleBarCatalog.OpenTabListId] = placement;
        var window = TestMainWindowFactory.Create(services with { SessionFactory = new RecordingSessionFactory(new FakeTerminalSession()) });
        var tabs = window.FindControl<TabControl>("Tabs")!;
        tabs.Items.Add(new TabItem { Header = new TextBlock { Text = "Second" }, Content = new Border() });
        tabs.SelectedIndex = 0;
        window.Show();
        window.UpdateTabVisuals();
        Dispatcher.UIThread.RunJobs();

        typeof(MainWindow).GetMethod("PopulateTabListMenu", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [true, null]);
        Dispatcher.UIThread.RunJobs();

        var host = window.FindControl<StackPanel>("TitleBarItemsHost")!;
        var button = host.Children.OfType<Button>()
            .SingleOrDefault(b => b.Name == TitleBarViewFactory.ButtonName(TitleBarCatalog.OpenTabListId));
        var flyout = (MenuFlyout?)(button?.Flyout ?? typeof(MainWindow)
            .GetField("_tabListFallbackFlyout", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
        Assert.NotNull(flyout);
        Assert.True(flyout.IsOpen);
        return (window, tabs, flyout);
    }

    private static void SetTabState(MainWindow window, TabItem tab, string property, object value)
    {
        var state = typeof(MainWindow).GetMethod("GetOrCreateTabState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [tab])!;
        state.GetType().GetProperty(property)!.SetValue(state, value);
    }
}
