using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.AgentHost;
using Ntilde.Controls;
using Ntilde.Pty;
using Ntilde.Shell;
using Ntilde.Shell.Mux;
using Ntilde.Tests.Controls;

namespace Ntilde.Tests.Core;

/// <summary>The agent changes the actual visible layout, not only a detached VT buffer.</summary>
public sealed class MainWindowAgentResizeTests : IClassFixture<TestAppDataRoot>, IDisposable
{
    public void Dispose() => TestMainWindowFactory.DisposeCreatedWindows();

    [AvaloniaFact]
    public async Task Resize_waits_for_actual_view_buffer_and_session_dispatch()
    {
        using var service = new AgentHostService(new AgentSessionRegistry());
        using var scope = AgentHostService.OverrideInstanceForTesting(service);
        var (window, pane) = CreateWindow();
        int cols = pane.TermView.Cols - 3, rows = pane.TermView.Rows - 2;
        Assert.True(cols > 0 && rows > 0);
        pane.TermView.ReleaseResizeThrottleForTest();

        var task = ((IAgentActionExecutor)window).ResizePaneAsync(pane.PaneId, cols, rows, TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal((rows, cols, true), (result.Value.Rows, result.Value.Cols, result.Value.LayoutSettled));
        Assert.Equal((cols, rows), (pane.TermView.Cols, pane.TermView.Rows));
        var buffer = pane.Buffer!;
        buffer.Lock.EnterReadLock();
        try { Assert.Equal((cols, rows), (buffer.Cols, buffer.Rows)); }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [AvaloniaFact]
    public async Task A_nonresizable_window_is_refused_without_changing_its_size()
    {
        using var service = new AgentHostService(new AgentSessionRegistry());
        using var scope = AgentHostService.OverrideInstanceForTesting(service);
        var (window, pane) = CreateWindow();
        window.CanResize = false;
        var before = window.ClientSize;

        var result = await ((IAgentActionExecutor)window).ResizePaneAsync(pane.PaneId, 60, 20, TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Equal(before, window.ClientSize);
    }

    [AvaloniaFact]
    public async Task A_deferred_resize_session_is_refused_without_changing_the_window()
    {
        using var service = new AgentHostService(new AgentSessionRegistry());
        using var scope = AgentHostService.OverrideInstanceForTesting(service);
        var (window, pane) = CreateWindow();
        pane.TermView.DefersBufferResizeToSession = true;
        var before = window.ClientSize;

        var result = await ((IAgentActionExecutor)window).ResizePaneAsync(pane.PaneId, 60, 20, TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Equal(before, window.ClientSize);
    }

    [AvaloniaFact]
    public async Task An_unknown_pane_cannot_resize_the_current_window()
    {
        using var service = new AgentHostService(new AgentSessionRegistry());
        using var scope = AgentHostService.OverrideInstanceForTesting(service);
        var (window, _) = CreateWindow();
        var before = window.ClientSize;

        Assert.Null(await ((IAgentActionExecutor)window).ResizePaneAsync(Guid.NewGuid(), 60, 20, TestContext.Current.CancellationToken));
        Assert.Equal(before, window.ClientSize);
    }

    [AvaloniaTheory]
    [InlineData(WindowState.Maximized)]
    [InlineData(WindowState.Minimized)]
    [InlineData(WindowState.FullScreen)]
    public async Task Only_a_normal_window_can_use_the_visible_pane_resize(WindowState state)
    {
        using var service = new AgentHostService(new AgentSessionRegistry());
        using var scope = AgentHostService.OverrideInstanceForTesting(service);
        var (window, pane) = CreateWindow();
        window.WindowState = state;
        var before = window.ClientSize;

        Assert.Null(await ((IAgentActionExecutor)window).ResizePaneAsync(pane.PaneId, 60, 20, TestContext.Current.CancellationToken));
        Assert.Equal(before, window.ClientSize);
    }

    [AvaloniaFact]
    public async Task Cancellation_releases_the_resize_lease_so_the_next_request_can_complete()
    {
        using var service = new AgentHostService(new AgentSessionRegistry());
        using var scope = AgentHostService.OverrideInstanceForTesting(service);
        var (window, pane) = CreateWindow();
        int cols = pane.TermView.Cols, rows = pane.TermView.Rows;
        pane.TermView.HoldResizeThrottleForTest();
        using var cancelled = new CancellationTokenSource();
        var first = ((IAgentActionExecutor)window).ResizePaneAsync(pane.PaneId, cols - 3, rows - 2, cancelled.Token);
        Dispatcher.UIThread.RunJobs();
        Assert.False(first.IsCompleted);
        var firstSize = window.ClientSize;

        Assert.Null(await ((IAgentActionExecutor)window).ResizePaneAsync(pane.PaneId, cols - 5, rows - 3, TestContext.Current.CancellationToken));
        Assert.Equal(firstSize, window.ClientSize);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        pane.TermView.ReleaseResizeThrottleForTest();
        var next = ((IAgentActionExecutor)window).ResizePaneAsync(pane.PaneId, cols - 5, rows - 3, TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();
        var result = await next.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal((rows - 3, cols - 5, true), (result.Value.Rows, result.Value.Cols, result.Value.LayoutSettled));
    }

    [AvaloniaFact]
    public async Task Closing_the_window_completes_a_pending_resize_without_a_successful_geometry_claim()
    {
        using var service = new AgentHostService(new AgentSessionRegistry());
        using var scope = AgentHostService.OverrideInstanceForTesting(service);
        var (window, pane) = CreateWindow();
        pane.TermView.HoldResizeThrottleForTest();
        var pending = ((IAgentActionExecutor)window).ResizePaneAsync(pane.PaneId, pane.TermView.Cols - 3, pane.TermView.Rows - 2, TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();
        Assert.False(pending.IsCompleted);

        window.TearDownForTest();
        Dispatcher.UIThread.RunJobs();

        Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task A_timed_out_dispatch_reports_actual_unsettled_buffer_geometry()
    {
        using var service = new AgentHostService(new AgentSessionRegistry());
        using var scope = AgentHostService.OverrideInstanceForTesting(service);
        var (window, pane) = CreateWindow();
        pane.TermView.ReleaseResizeThrottleForTest();
        int cols = pane.TermView.Cols - 3, rows = pane.TermView.Rows - 2;
        int attempts = 0;
        pane.TermView.OnResize += (_, _) =>
        {
            attempts++;
            throw new InvalidOperationException("The session resize callback did not complete.");
        };

        var pending = ((IAgentActionExecutor)window).ResizePaneAsync(pane.PaneId, cols, rows, TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();
        Assert.False(pending.IsCompleted);
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(attempts > 0);
        Assert.NotNull(result);
        Assert.False(result.Value.LayoutSettled);
        Assert.Equal((rows, cols), (result.Value.Rows, result.Value.Cols));
        Assert.Equal((cols, rows), (pane.TermView.Cols, pane.TermView.Rows));
    }

    [AvaloniaFact]
    public async Task An_inactive_tab_is_not_activated_or_resized()
    {
        using var service = new AgentHostService(new AgentSessionRegistry());
        using var scope = AgentHostService.OverrideInstanceForTesting(service);
        var (window, original) = CreateWindow();
        var executor = (IAgentActionExecutor)window;
        var spawned = await executor.SpawnAsync(null);
        Assert.Null(spawned.Error);
        Assert.NotNull(spawned.Result);
        Dispatcher.UIThread.RunJobs();
        var tabs = window.FindControl<TabControl>("Tabs")!;
        var selected = tabs.SelectedItem;
        var before = window.ClientSize;

        Assert.Null(await executor.ResizePaneAsync(original.PaneId, 80, 24, TestContext.Current.CancellationToken));
        Assert.Same(selected, tabs.SelectedItem);
        Assert.Equal(before, window.ClientSize);
    }

    [AvaloniaFact]
    public async Task A_split_tab_cannot_resize_other_panes_through_the_window()
    {
        using var service = new AgentHostService(new AgentSessionRegistry());
        using var scope = AgentHostService.OverrideInstanceForTesting(service);
        var (window, _) = CreateWindow();
        typeof(MainWindow).GetMethod("SplitPane", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(window, [Avalonia.Layout.Orientation.Horizontal]);
        Dispatcher.UIThread.RunJobs();
        var panes = window.AllPanesForTest();
        Assert.Equal(2, panes.Count);
        var before = window.ClientSize;

        foreach (var pane in panes)
            Assert.Null(await ((IAgentActionExecutor)window).ResizePaneAsync(pane.PaneId, 80, 24, TestContext.Current.CancellationToken));
        Assert.Equal(before, window.ClientSize);
    }

    [AvaloniaFact]
    public async Task A_hidden_pane_cannot_change_the_window_geometry()
    {
        using var service = new AgentHostService(new AgentSessionRegistry());
        using var scope = AgentHostService.OverrideInstanceForTesting(service);
        var (window, pane) = CreateWindow();
        int cols = pane.TermView.Cols - 3, rows = pane.TermView.Rows - 2;
        pane.IsVisible = false;
        var before = window.ClientSize;

        Assert.Null(await ((IAgentActionExecutor)window).ResizePaneAsync(pane.PaneId, cols, rows, TestContext.Current.CancellationToken));
        Assert.Equal(before, window.ClientSize);
    }

    private static (MainWindow Window, TerminalPane Pane) CreateWindow()
    {
        var profile = new TerminalProfile { Name = "Agent resize local", Type = ConnectionType.Local };
        var settings = new TerminalSettings
        {
            SessionPersistence = SessionPersistenceMode.Off,
            AgentAccessActEnabled = true,
            CommandAssistShellIntegrationEnabled = false,
            Profiles = [profile],
            DefaultProfileId = profile.Id,
        };
        var window = TestMainWindowFactory.Create(AppServices.BuildForDesigner() with
        {
            Settings = settings,
            SessionFactory = new RecordingSessionFactory(new FakeTerminalSession()),
            CommandAssist = TestCommandAssistServices.Instance,
        });
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, Assert.Single(window.AllPanesForTest()));
    }
}
