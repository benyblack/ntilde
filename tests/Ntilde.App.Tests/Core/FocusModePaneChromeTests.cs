using System;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Ntilde.Controls;
using Ntilde.Tests.Infra;
using static Ntilde.Tests.Core.WindowTestSupport;

namespace Ntilde.Tests.Core;

/// <summary>Focus mode, task 3: each pane hides its status bar, scrollbar, MD toggle and side panels.</summary>
public sealed class FocusModePaneChromeTests : IClassFixture<TestAppDataRoot>, IDisposable
{
    public void Dispose() => TestMainWindowFactory.DisposeCreatedWindows();

    [Theory]
    [InlineData(true, false, true, false, true)]
    [InlineData(true, false, true, true, false)]
    [InlineData(false, false, true, false, false)]
    [InlineData(true, true, true, false, false)]
    [InlineData(true, false, false, false, false)]
    public void Remote_files_visibility_rule(bool isOpen, bool altScreen, bool supported, bool hiddenByFocus, bool expected)
    {
        Assert.Equal(expected, TerminalPane.ShouldShowRemoteFilesSidebar(isOpen, altScreen, supported, hiddenByFocus));
    }

    [Fact]
    public void A_remote_files_toggle_while_hidden_by_focus_reveals_instead_of_closing()
    {
        Assert.Equal((true, false), TerminalPane.ResolveRemoteFilesToggle(true, true));
        Assert.Equal((false, false), TerminalPane.ResolveRemoteFilesToggle(true, false));
        Assert.Equal((true, false), TerminalPane.ResolveRemoteFilesToggle(false, false));
    }

    // Final review M3: while focus mode hides an open panel, a click on the entry reveals it, so the entry must not
    // say "Hide".
    [Theory]
    [InlineData(false, false, "Remote Files")]
    [InlineData(true, false, "Hide Remote Files")]
    [InlineData(true, true, "Remote Files")]
    public void The_remote_files_entry_says_what_a_click_does(bool isOpen, bool hiddenByFocus, string expected)
    {
        Assert.Equal(expected, TerminalPane.RemoteFilesMenuHeader(isOpen, hiddenByFocus));

        // Opening from closed goes through OpenRemoteFilesSidebarAsync, which shows the panel.
        bool shownAfterClick = isOpen
            ? TerminalPane.ResolveRemoteFilesToggle(isOpen, hiddenByFocus) is (true, false)
            : true;
        Assert.Equal(expected == "Remote Files", shownAfterClick);
    }

    // PR #595 and #607 reviews: an open is a request to see the panel, but it can take a while to connect, and what
    // the user does meanwhile is later and wins - focus mode entered hides the panel, a reveal after that shows it.

    /// <summary>Lists nothing until the test lets the listing finish: an open that is still connecting.</summary>
    private sealed class PendingDirectoryBrowser : Ntilde.Services.Ssh.IRemoteDirectoryBrowserService
    {
        public TaskCompletionSource<Ntilde.Models.RemoteSidebarListingResult> Listing { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<Ntilde.Models.RemoteSidebarListingResult> ListDirectoryAsync(
            Guid profileId, Guid sessionId, string remotePath, System.Threading.CancellationToken cancellationToken) => Listing.Task;

        public void Finish() => Listing.SetResult(
            Ntilde.Models.RemoteSidebarListingResult.Success("~", Array.Empty<Ntilde.Models.RemoteSidebarEntry>()));
    }

    private static TerminalPane NativeSshPane(PendingDirectoryBrowser browser)
    {
        var pane = new TerminalPane(new Ntilde.Shell.TerminalProfile
        {
            Name = "Native SSH",
            Type = Ntilde.Shell.ConnectionType.SSH,
            SshBackendKind = Ntilde.Platform.Ssh.Models.SshBackendKind.Native,
            SshHost = "server.example",
            SshUser = "nova",
        });
        pane.ConfigureRemoteFilesSidebarForTest(browser);
        return pane;
    }

    private static Task OpenRemoteFiles(TerminalPane pane) => (Task)typeof(TerminalPane)
        .GetMethod("OpenRemoteFilesSidebarAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(pane, [Guid.NewGuid(), Guid.NewGuid()])!;

    private static Task ToggleRemoteFiles(TerminalPane pane) => (Task)typeof(TerminalPane)
        .GetMethod("ToggleRemoteFilesSidebarAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(pane, null)!;

    [AvaloniaFact]
    public async Task Focus_mode_entered_while_remote_files_connects_hides_the_panel()
    {
        var browser = new PendingDirectoryBrowser();
        using var pane = NativeSshPane(browser);

        Task open = OpenRemoteFiles(pane);
        pane.FocusChromeHidden = true;
        browser.Finish();
        await open;

        Assert.False(pane.IsRemoteFilesSidebarVisibleForTest());
    }

    [AvaloniaFact]
    public async Task A_reveal_while_remote_files_connects_is_kept()
    {
        var browser = new PendingDirectoryBrowser();
        using var pane = NativeSshPane(browser);

        Task open = OpenRemoteFiles(pane);
        pane.FocusChromeHidden = true;
        await ToggleRemoteFiles(pane); // the entry reveals the panel focus mode hid
        browser.Finish();
        await open;

        Assert.True(pane.IsRemoteFilesSidebarVisibleForTest());
    }

    [AvaloniaFact]
    public async Task Remote_files_opened_in_focus_mode_shows_the_panel()
    {
        var browser = new PendingDirectoryBrowser();
        using var pane = NativeSshPane(browser);
        pane.FocusChromeHidden = true;

        Task open = OpenRemoteFiles(pane);
        browser.Finish();
        await open;

        Assert.True(pane.IsRemoteFilesSidebarVisibleForTest());
    }

    private static void MakeActable(TerminalPane pane)
    {
        RegistrationFor(pane).IsAgentActable = true;
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Focus_mode_hides_and_restores_the_status_bar_and_scrollbar()
    {
        MainWindow window = NewShownWindow();
        var pane = window.AllPanesForTest()[0];
        MakeActable(pane);
        var statusBar = pane.FindControl<Control>("StatusBar")!;
        var scrollBar = pane.FindControl<Control>("TermScrollBar")!;
        Assert.True(statusBar.IsVisible);
        Assert.True(scrollBar.IsVisible);

        window.EnterFocusMode();
        Assert.False(statusBar.IsVisible);
        Assert.False(scrollBar.IsVisible);

        window.LeaveFocusMode();
        Assert.True(statusBar.IsVisible);
        Assert.True(scrollBar.IsVisible);
    }

    [AvaloniaFact]
    public void Focus_mode_hides_the_markdown_toggle_and_brings_it_back()
    {
        MainWindow window = NewShownWindow();
        var pane = window.AllPanesForTest()[0];
        var toggle = pane.FindControl<Control>("AgentOutputToggle")!;

        pane.OnMarkdownPresenceChanged(true);
        Assert.True(toggle.IsVisible);

        window.EnterFocusMode();
        Assert.False(toggle.IsVisible);

        pane.OnMarkdownPresenceChanged(true);
        Assert.False(toggle.IsVisible);

        window.LeaveFocusMode();
        Assert.True(toggle.IsVisible);
    }

    [AvaloniaFact]
    public void Every_pane_of_a_split_and_a_zoom_stashed_sibling_gets_the_flag()
    {
        MainWindow window = NewShownWindow();
        var tab = AddSplitTab(window, out var a, out _);
        window.FindControl<TabControl>("Tabs")!.SelectedItem = tab;
        var entered = (bool)typeof(MainWindow)
            .GetMethod("EnterPaneZoom", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object[] { tab, a, true })!;
        Assert.True(entered);

        window.EnterFocusMode();
        var panes = window.AllPanesForTest();
        Assert.True(panes.Count >= 2);
        Assert.All(panes, p => Assert.True(p.FocusChromeHidden));

        window.LeaveFocusMode();
        Assert.All(window.AllPanesForTest(), p => Assert.False(p.FocusChromeHidden));
    }

    [AvaloniaFact]
    public void The_zoomed_pane_itself_gets_the_flag()
    {
        MainWindow window = NewShownWindow();
        var tab = AddSplitTab(window, out var a, out _);
        window.FindControl<TabControl>("Tabs")!.SelectedItem = tab;
        var entered = (bool)typeof(MainWindow)
            .GetMethod("EnterPaneZoom", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object[] { tab, a, true })!;
        Assert.True(entered);

        window.EnterFocusMode();
        Assert.True(a.FocusChromeHidden);

        window.LeaveFocusMode();
        Assert.False(a.FocusChromeHidden);
    }

    [AvaloniaFact]
    public void A_pane_created_in_focus_mode_starts_hidden()
    {
        MainWindow window = NewShownWindow();
        window.EnterFocusMode();
        var before = window.AllPanesForTest().ToList();

        window.PressShortcut("split_vertical", "Ctrl+Shift+D");
        Dispatcher.UIThread.RunJobs();

        var created = window.AllPanesForTest().Except(before).ToList();
        Assert.NotEmpty(created);
        Assert.All(created, p => Assert.True(p.FocusChromeHidden));
    }
}
