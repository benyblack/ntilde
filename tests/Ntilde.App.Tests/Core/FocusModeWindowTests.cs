using System;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Ntilde.Controls;
using Ntilde.Shell;
using Ntilde.Shell.Shortcuts;
using static Ntilde.Tests.Core.WindowTestSupport;

namespace Ntilde.Tests.Core;

/// <summary>
/// Focus mode, task 1: the per-window flag, its shortcut and palette entry, and the window chrome it
/// hides (the tab header area and the title bar items) behind a slim strip.
/// </summary>
public sealed class FocusModeWindowTests : IClassFixture<TestAppDataRoot>, IDisposable
{
    public void Dispose() => TestMainWindowFactory.DisposeCreatedWindows();

    private static readonly RawInputModifiers CtrlShift = RawInputModifiers.Control | RawInputModifiers.Shift;

    private static TerminalSettings SettingsOf(MainWindow window) =>
        (TerminalSettings)typeof(MainWindow).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    private static void AssertChromeHidden(MainWindow window)
    {
        var tabs = window.FindControl<TabControl>("Tabs")!;
        Assert.False(TemplatePart<Grid>(window, "PART_TabSidebar").IsVisible);
        Assert.Equal(24, TemplatePart<Border>(window, "PART_TitleBandSpacer").Height);

        var strip = window.FindControl<Grid>("FocusStrip")!;
        Assert.True(strip.IsVisible);
        var title = window.FindControl<TextBlock>("FocusStripTitle")!;
        Assert.Equal((string)Invoke(window, "GetTabHeaderText", tabs.SelectedItem!)!, title.Text);

        var host = window.FindControl<StackPanel>("TitleBarItemsHost")!;
        foreach (Control child in host.Children.OfType<Control>().Where(c => c.Name != "AgentObserveIndicator"))
        {
            Assert.False(child.IsVisible, $"{child.GetType().Name} '{child.Name}' should be hidden in focus mode");
        }

        var titleBar = window.FindControl<Grid>("TitleBar")!;
        Assert.Equal(24, titleBar.Height);
        Assert.Equal(0, titleBar.Margin.Top);

        Assert.False(window.FindControl<TextBlock>("TabOverflowBadge")!.IsVisible);
    }

    [AvaloniaFact]
    public void The_shortcut_toggles_focus_mode()
    {
        MainWindow window = NewShownWindow();

        window.KeyPress(Key.Enter, CtrlShift, PhysicalKey.Enter, null);
        Assert.True(window.IsFocusMode);

        window.KeyPress(Key.Enter, CtrlShift, PhysicalKey.Enter, null);
        Assert.False(window.IsFocusMode);
    }

    [AvaloniaFact]
    public void The_palette_lists_toggle_focus_mode_with_its_binding()
    {
        CommandRegistry.Clear();
        MainWindow window = NewShownWindow();

        Invoke(window, "SetupCommandPalette");

        var command = Assert.Single(CommandRegistry.GetCommands(), c => c.Id == "toggle_focus_mode");
        Assert.Equal("View: Toggle Focus Mode", command.FullTitle);
        Assert.Equal(ShortcutPlatform.DefaultBinding("toggle_focus_mode", "Ctrl+Shift+Enter"), command.Shortcut);

        command.Action();
        Assert.True(window.IsFocusMode);
    }

    [AvaloniaFact]
    public void Entering_hides_the_tab_area_and_title_bar_items_and_shows_the_strip()
    {
        MainWindow window = NewShownWindow();

        window.EnterFocusMode();
        Dispatcher.UIThread.RunJobs();

        AssertChromeHidden(window);

        // The overflow badge can be re-parented into the title bar and recomputed from other paths.
        Invoke(window, "UpdateTabOverflowIndicator");
        Assert.False(window.FindControl<TextBlock>("TabOverflowBadge")!.IsVisible);
    }

    [AvaloniaFact]
    public void Leaving_restores_the_horizontal_layout()
    {
        MainWindow window = NewShownWindow();
        var host = window.FindControl<StackPanel>("TitleBarItemsHost")!;
        (string? Name, bool Visible)[] Snapshot() =>
            host.Children.OfType<Control>().Select(c => (c.Name, c.IsVisible)).ToArray();
        var before = Snapshot();

        window.EnterFocusMode();
        Dispatcher.UIThread.RunJobs();
        window.LeaveFocusMode();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(before, Snapshot());
        Assert.True(TemplatePart<Grid>(window, "PART_TabSidebar").IsVisible);
        Assert.Equal(0, TemplatePart<Border>(window, "PART_TitleBandSpacer").Height);
        Assert.False(window.FindControl<Grid>("FocusStrip")!.IsVisible);
        var titleBar = window.FindControl<Grid>("TitleBar")!;
        Assert.Equal(32, titleBar.Height);
        Assert.Equal(4, titleBar.Margin.Top);
    }

    [AvaloniaFact]
    public void Vertical_sidebar_round_trips_without_changing_orientation_or_width()
    {
        MainWindow window = NewShownWindow();
        var settings = SettingsOf(window);
        settings.TabStripOrientation = "Vertical";
        window.ApplyTabLayout();
        Dispatcher.UIThread.RunJobs();
        double widthSetting = settings.VerticalTabStripWidth;

        var sidebar = TemplatePart<Grid>(window, "PART_TabSidebar");
        var spacer = TemplatePart<Border>(window, "PART_TitleBandSpacer");

        window.EnterFocusMode();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(24, spacer.Height);
        Assert.False(sidebar.IsVisible);

        window.LeaveFocusMode();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(36, spacer.Height);
        Assert.True(sidebar.IsVisible);
        Assert.Equal(Dock.Left, DockPanel.GetDock(sidebar));
        var scrollViewer = (ScrollViewer)Invoke(window, "FindTabHeaderScrollViewer")!;
        Assert.Equal(TabStripLayout.ClampSidebarWidth(settings.VerticalTabStripWidth), scrollViewer.Width);
        Assert.Equal("Vertical", settings.TabStripOrientation);
        Assert.Equal(widthSetting, settings.VerticalTabStripWidth);
    }

    [AvaloniaFact]
    public void Focus_mode_leaves_zoom_selection_and_split_alone()
    {
        MainWindow window = NewShownWindow();
        var tabs = window.FindControl<TabControl>("Tabs")!;
        var tab = AddSplitTab(window, out var first, out _);
        tabs.SelectedItem = tab;
        var content = (Grid)tab.Content!;

        window.EnterFocusMode();
        window.LeaveFocusMode();
        Assert.Same(content, tab.Content);
        Assert.Equal(2, content.Children.OfType<TerminalPane>().Count());

        Assert.True((bool)Invoke(window, "EnterPaneZoom", tab, first, true)!);
        var zoomedId = window.GetZoomedPaneIdForTab(tab);

        window.EnterFocusMode();
        window.LeaveFocusMode();

        Assert.True(window.IsPaneZoomActiveForTab(tab));
        Assert.Equal(zoomedId, window.GetZoomedPaneIdForTab(tab));
        Assert.Same(tab, tabs.SelectedItem);
    }

    [AvaloniaFact]
    public void Toggling_zoom_inside_focus_mode_stays_in_focus_mode()
    {
        MainWindow window = NewShownWindow();
        var tabs = window.FindControl<TabControl>("Tabs")!;
        var tab = AddSplitTab(window, out _, out _);
        tabs.SelectedItem = tab;
        Dispatcher.UIThread.RunJobs();

        window.EnterFocusMode();
        window.KeyPress(Key.Z, CtrlShift, PhysicalKey.Z, null);

        Assert.True(window.IsPaneZoomActiveForTab(tab));
        Assert.True(window.IsFocusMode);
    }

    [AvaloniaFact]
    public void The_strip_title_follows_the_selected_tab()
    {
        MainWindow window = NewShownWindow();
        var tabs = window.FindControl<TabControl>("Tabs")!;
        window.EnterFocusMode();

        var second = AddSplitTab(window, out _, out _);
        tabs.SelectedItem = second;
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.IsFocusMode);
        Assert.Equal((string)Invoke(window, "GetTabHeaderText", second)!, window.FindControl<TextBlock>("FocusStripTitle")!.Text);
    }

    [AvaloniaFact]
    public void Reapplying_settings_while_in_focus_mode_keeps_the_chrome_hidden()
    {
        MainWindow window = NewShownWindow();
        window.EnterFocusMode();

        Invoke(window, "ApplyThemeToUI");
        Invoke(window, "ApplySettingsToAllTabs");
        Invoke(window, "RebuildTitleBar");
        window.ApplyTabLayout();
        Dispatcher.UIThread.RunJobs();

        AssertChromeHidden(window);
    }

    [AvaloniaFact]
    public void Entering_swaps_in_the_slim_decorations_theme_and_leaving_swaps_it_back()
    {
        MainWindow window = NewShownWindow();

        window.EnterFocusMode();
        Assert.Same(window.FindResource("NtildeFocusWindowDecorationsTheme"), window.WindowDecorationsTheme);

        window.LeaveFocusMode();
        Assert.Same(window.FindResource("NtildeWindowDecorationsTheme"), window.WindowDecorationsTheme);
    }

    [AvaloniaFact]
    public void The_strip_menu_exits_focus_mode()
    {
        MainWindow window = NewShownWindow();
        window.EnterFocusMode();

        window.FindControl<MenuItem>("MenuExitFocusMode")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        Assert.False(window.IsFocusMode);
    }

    [AvaloniaFact]
    public void The_title_bar_menu_swaps_customize_for_exit_in_focus_mode()
    {
        MainWindow window = NewShownWindow();
        var customize = window.FindControl<MenuItem>("MenuCustomizeTitleBar")!;
        var exit = window.FindControl<MenuItem>("MenuTitleBarExitFocusMode")!;

        window.EnterFocusMode();
        Assert.False(customize.IsVisible);
        Assert.True(exit.IsVisible);

        exit.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.False(window.IsFocusMode);
        Assert.True(customize.IsVisible);
        Assert.False(exit.IsVisible);
    }

    [Fact]
    public void Double_click_maximize_state_toggles()
    {
        Assert.Equal(WindowState.Normal, MainWindow.ToggledMaximizeState(WindowState.Maximized));
        Assert.Equal(WindowState.Maximized, MainWindow.ToggledMaximizeState(WindowState.Normal));
        Assert.Equal(WindowState.Maximized, MainWindow.ToggledMaximizeState(WindowState.Minimized));
    }

    [AvaloniaFact]
    public void The_strip_title_margin_follows_the_caption_button_reserve()
    {
        MainWindow window = NewShownWindow();
        window.EnterFocusMode();
        var titleBar = window.FindControl<Grid>("TitleBar")!;
        double reserve = titleBar.Margin.Right + 17;

        Invoke(window, "ApplyCaptionButtonReserve", titleBar, reserve);

        Assert.Equal(reserve, titleBar.Margin.Right);
        Assert.Equal(titleBar.Margin.Right + 40, window.FindControl<Grid>("FocusStrip")!.Margin.Right);
    }

    [Fact]
    public void The_drag_band_follows_the_strip()
    {
        Assert.Equal(24, MainWindow.WindowDragBandHeight(true));
        Assert.Equal(36, MainWindow.WindowDragBandHeight(false));
    }
}
