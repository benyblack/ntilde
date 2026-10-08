using System;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.Shell;
using Xunit;

namespace Ntilde.Tests.Core;

/// <summary>
/// A closed tab must not stay reachable from the window. A heap dump of a running app (RAM audit,
/// 2026-10-05) found two closed tabs - each with its whole pane: scrollback, glyph atlas, render
/// caches - still alive a minute after closing, held by
/// <c>MainWindow._tabListFallbackFlyout</c>. The tab-list menu builds one MenuItem per tab whose
/// Click closure captures that TabItem, and PopulateTabListMenu only ever rebuilds the flyout the
/// current anchor resolves to, so a window-owned flyout it stopped targeting kept its old items -
/// and every tab they captured - for the life of the window.
/// </summary>
public sealed class MainWindowTabRetentionTests : IDisposable
{
    public void Dispose() => TestMainWindowFactory.DisposeCreatedWindows();

    [AvaloniaFact]
    public void PopulateTabListMenu_IntoTheTitleBarButton_ReleasesTheFallbackMenusItems()
    {
        var window = TestMainWindowFactory.Create();
        var settings = GetSettings(window);

        // Tab List hidden: no dedicated button, so the menu is built into the fallback flyout.
        settings.TitleBarItems["open_tab_list"] = "Hidden";
        InvokeRebuildTitleBar(window);
        InvokePopulateTabListMenu(window, showFlyout: false, anchorOverride: null);
        var fallback = GetFlyoutField(window, "_tabListFallbackFlyout");
        Assert.NotNull(fallback);
        Assert.True(fallback!.Items.Count > 0, "Precondition: the fallback menu should hold the tab entries.");

        // The button comes back and the menu moves to it. The fallback flyout is never shown again
        // until it is rebuilt, so anything left in it only pins the tabs its closures captured.
        settings.TitleBarItems["open_tab_list"] = "Pinned";
        InvokeRebuildTitleBar(window);
        InvokePopulateTabListMenu(window, showFlyout: false, anchorOverride: null);

        Assert.Empty(fallback.Items);
    }

    [AvaloniaFact]
    public void PopulateTabListMenu_IntoTheTitleBar_ReleasesTheOverflowPillMenusItems()
    {
        var window = TestMainWindowFactory.Create();

        // The vertical overflow pill fills its own window-owned flyout, only when clicked.
        InvokePopulateTabListMenu(window, showFlyout: false, anchorOverride: new Button());
        var pillFlyout = GetFlyoutField(window, "_tabOverflowPillFlyout");
        Assert.NotNull(pillFlyout);
        Assert.True(pillFlyout!.Items.Count > 0, "Precondition: the pill menu should hold the tab entries.");

        // Any title-bar rebuild (every UpdateTabVisuals runs one, including the one after a close)
        // must drop the pill's copy, or tabs closed after the pill's last click stay alive.
        InvokePopulateTabListMenu(window, showFlyout: false, anchorOverride: null);

        Assert.Empty(pillFlyout.Items);
    }

    [AvaloniaFact]
    public void CloseTab_DetachesTheClosedTabsContent()
    {
        var window = TestMainWindowFactory.Create();
        var tabs = window.FindControl<TabControl>("Tabs");
        Assert.NotNull(tabs);

        var closing = new TabItem { Header = new TextBlock { Text = "closing" }, Content = new Border() };
        tabs!.Items.Add(closing);
        Dispatcher.UIThread.RunJobs();

        InvokeCloseTab(window, closing);

        // Anything that still holds the TabItem after close - a stale menu closure, a cached
        // lookup - must not also hold the pane tree that hangs off it.
        Assert.DoesNotContain(closing, tabs.Items);
        Assert.Null(closing.Content);
    }

    private static TerminalSettings GetSettings(Ntilde.MainWindow window)
    {
        var field = typeof(Ntilde.MainWindow).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (TerminalSettings)field!.GetValue(window)!;
    }

    private static MenuFlyout? GetFlyoutField(Ntilde.MainWindow window, string name)
    {
        var field = typeof(Ntilde.MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (MenuFlyout?)field!.GetValue(window);
    }

    private static void InvokeRebuildTitleBar(Ntilde.MainWindow window)
    {
        var method = typeof(Ntilde.MainWindow).GetMethod("RebuildTitleBar", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(window, null);
    }

    private static void InvokePopulateTabListMenu(Ntilde.MainWindow window, bool showFlyout, Control? anchorOverride)
    {
        var method = typeof(Ntilde.MainWindow).GetMethod("PopulateTabListMenu", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(window, [showFlyout, anchorOverride]);
    }

    private static void InvokeCloseTab(Ntilde.MainWindow window, TabItem tab)
    {
        var method = typeof(Ntilde.MainWindow).GetMethod("CloseTab", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(TabItem)]);
        Assert.NotNull(method);
        method!.Invoke(window, [tab]);
    }
}
