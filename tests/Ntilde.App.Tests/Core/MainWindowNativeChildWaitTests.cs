using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.AgentHost;
using Ntilde.AgentHost.Contracts;
using Ntilde.AppTests.Shell;
using Ntilde.Controls;
using Ntilde.Pty;
using Ntilde.Shell;
using Ntilde.Shell.Clipboard;
using Ntilde.Shell.Mux;
using Ntilde.Tests.Controls;

namespace Ntilde.Tests.Core;

[Collection(Ntilde.Tests.Infra.PrivateClipboardProcessScope.Name)]
[Trait("Category", "PtySmoke")]
public sealed class MainWindowNativeChildWaitTests : IClassFixture<TestAppDataRoot>, IDisposable
{
    private readonly PrivateClipboardQaModeTests _startup = new();
    private IDisposable? _mode;
    public void Dispose()
    {
        try { TestMainWindowFactory.DisposeCreatedWindows(); }
        finally { _mode?.Dispose(); _startup.Dispose(); }
    }
    private sealed class FirstSessionFactory(ITerminalSession original) : ITerminalSessionFactory
    {
        private int _created;
        public ITerminalSession Create(TerminalSessionRequest request) => _created++ == 0 ? original : new FakeTerminalSession();
    }

    private (MainWindow Window, TerminalPane Pane, RustPtySession Session) Create(bool qa = true)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "The private native wait source is Unix-only; Windows desktop is deferred.");
        var session = new RustPtySession("/bin/sh", 80, 24, null, null, true,
            new Dictionary<string, string> { ["ENV"] = "", ["BASH_ENV"] = "", ["PS1"] = "", ["PS2"] = "", ["PROMPT_COMMAND"] = "" }, null);
        try
        {
            var (window, pane) = CreateWindow(session, qa);
            return (window, pane, session);
        }
        catch { session.Dispose(); throw; }
    }

    private (MainWindow Window, TerminalPane Pane) CreateWindow(ITerminalSession session, bool qa)
    {
        if (qa) _mode = PrivateClipboardQaMode.OverrideProcessForTesting(_startup.Create());
        var profile = new TerminalProfile { Name = "Private native wait", Type = ConnectionType.Local };
        var settings = new TerminalSettings
        {
            SessionPersistence = SessionPersistenceMode.Off,
            ShellExitPolicy = "Never",
            AgentAccessActEnabled = false,
            CommandAssistShellIntegrationEnabled = false,
            Profiles = [profile],
            DefaultProfileId = profile.Id
        };
        var services = AppServices.BuildForDesigner() with
        {
            Settings = settings,
            SessionFactory = new FirstSessionFactory(session),
            CommandAssist = TestCommandAssistServices.Instance
        };
        var window = TestMainWindowFactory.Create(services); window.Show(); Dispatcher.UIThread.RunJobs();
        return (window, Assert.Single(window.AllPanesForTest()));
    }
    private static ReadNativeChildWaitParams Input(TerminalPane pane, RustPtySession session) => new()
    {
        PaneId = pane.PaneId,
        RunId = PrivateClipboardQaModeTests.Run,
        ExpectedChildPid = session.ReadNativeChildWaitObservation()!.Value.OriginalChildPid,
        ExpectedNativeSessionId = session.Id
    };
    private static Task<NativeChildWaitResult?> Read(MainWindow window, TerminalPane pane, ReadNativeChildWaitParams input, CancellationToken token = default)
        => ((IAgentPaneObserver)window).ReadNativeChildWaitAsync(input, pane.AgentRegistrationForTesting!, token);

    [AvaloniaFact]
    public async Task Actual_UI_bridge_reads_unknown_then_genuine_wait_from_same_exited_Never_pane()
    {
        var (window, pane, session) = Create(); var input = Input(pane, session);
        var initial = await Read(window, pane, input); Assert.NotNull(initial);
        Assert.False(initial.NativeWaitObserved); Assert.Null(initial.ExitCode);
        session.SendInput("exit 7\n"); // Explicit control of this test-owned child, not an endpoint action.
        var elapsed = Stopwatch.StartNew();
        while (session.ReadNativeChildWaitObservation()?.NativeWaitObserved != true && elapsed.Elapsed < TimeSpan.FromSeconds(15))
        {
            // Short on purpose: wait for the existing native watcher, never ask the observation API to wait/reap.
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        Dispatcher.UIThread.RunJobs();
        var result = await Read(window, pane, input); Assert.NotNull(result);
        Assert.True(result.NativeWaitObserved); Assert.Equal(7, result.ExitCode);
        Assert.Equal(session.Id, result.NativeSessionId); Assert.Equal(input.ExpectedChildPid, result.OriginalChildPid);
        Assert.Same(session, pane.Session); Assert.Same(session, pane.AgentRegistrationForTesting!.ObservationSession);
        Assert.Null(pane.PrivateClipboardQa.For(pane));
        Assert.Null(pane.PrivateClipboardQa.ResolveHostClipboard(() => throw new InvalidOperationException("Never resolve host clipboard.")));
    }

    [AvaloniaFact]
    public async Task Maximized_visible_single_pane_retains_passive_native_wait_observation()
    {
        var (window, pane, session) = Create(); var input = Input(pane, session);
        Assert.NotNull(await Read(window, pane, input));
        window.WindowState = WindowState.Maximized; Dispatcher.UIThread.RunJobs();
        Assert.Equal(WindowState.Maximized, window.WindowState); Assert.True(pane.IsEffectivelyVisible);
        var result = await Read(window, pane, input); Assert.NotNull(result);
        Assert.Equal(session.Id, result.NativeSessionId); Assert.Equal(input.ExpectedChildPid, result.OriginalChildPid);
        Assert.False(result.NativeWaitObserved); Assert.Null(result.ExitCode);
    }

    [AvaloniaFact]
    public async Task Ordinary_process_latch_cannot_observe_even_a_real_current_local_session()
    {
        var (window, pane, session) = Create(qa: false);
        Assert.Null(await Read(window, pane, Input(pane, session)));
    }

    [AvaloniaFact]
    public async Task Wrong_original_PID_generation_and_replaced_registration_session_are_refused()
    {
        var (window, pane, session) = Create(); var input = Input(pane, session);
        Assert.NotNull(await Read(window, pane, input));
        Assert.Null(await Read(window, pane, input with { ExpectedChildPid = input.ExpectedChildPid + 1 }));
        Assert.Null(await Read(window, pane, input with { ExpectedNativeSessionId = Guid.NewGuid() }));
        Assert.Null(await Read(window, pane, input with { RunId = new string('f', 32) }));
        pane.AgentRegistrationForTesting!.SetLifecycle(new FakeTerminalSession());
        Assert.Null(await Read(window, pane, input));
    }

    [AvaloniaFact]
    public async Task Hidden_minimized_and_disposed_targets_remain_unavailable()
    {
        var (window, pane, session) = Create(); var input = Input(pane, session);
        window.Hide(); Assert.Null(await Read(window, pane, input));
        window.Show(); Dispatcher.UIThread.RunJobs(); window.WindowState = WindowState.Minimized;
        Assert.Null(await Read(window, pane, input));
        window.WindowState = WindowState.Normal; session.Dispose();
        Assert.Null(await Read(window, pane, input));
    }

    [AvaloniaFact]
    public async Task Already_cancelled_dispatch_cannot_return_a_later_observation()
    {
        var (window, pane, session) = Create(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read(window, pane, Input(pane, session), cancellation.Token));
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(await Read(window, pane, Input(pane, session)));
    }

    [AvaloniaTheory]
    [InlineData("ssh")]
    [InlineData("deferred")]
    [InlineData("inactive")]
    public async Task Remote_deferred_or_inactive_current_target_cannot_disclose_cached_wait(string kind)
    {
        var (window, pane, session) = Create(); var input = Input(pane, session);
        if (kind == "ssh") pane.Profile!.Type = ConnectionType.SSH;
        if (kind == "deferred") pane.TermView.DefersBufferResizeToSession = true;
        if (kind == "inactive") pane.AgentRegistrationForTesting!.UpdateSnapshot("private", "private", "local", false);
        Assert.Null(await Read(window, pane, input));
    }

    [AvaloniaFact]
    public async Task Actual_split_refuses_even_when_the_original_native_pane_is_selected_and_active()
    {
        var (window, pane, session) = Create(); var input = Input(pane, session);
        Assert.NotNull(await Read(window, pane, input));
        typeof(MainWindow).GetMethod("SplitPane", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [Avalonia.Layout.Orientation.Horizontal]);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, window.AllPanesForTest().Count);
        typeof(MainWindow).GetMethod("UpdateActivePane", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [pane]);
        Dispatcher.UIThread.RunJobs();
        Assert.True(pane.IsActivePane); Assert.True(pane.AgentRegistrationForTesting!.IsActive);
        Assert.Null(await Read(window, pane, input));
    }

    private sealed class ClaimedWaitWrapper : ITerminalSession, ITerminalNativeChildWaitObservation
    {
        private readonly FakeTerminalSession _inner = new();
        internal int WaitReads;
        public NativeChildWaitObservation? ReadNativeChildWaitObservation() { WaitReads++; return new(Id, 123, true, 0); }
        public Guid Id => _inner.Id;
        public string ShellCommand => _inner.ShellCommand;
        public string? ShellArguments => null;
        public bool IsProcessRunning => true;
        public bool HasActiveChildProcesses => false;
        public int? ExitCode => 0;
        public bool IsRecording => false;
        public bool IsFlightRecording => false;
        public event Action<string>? OnOutputReceived { add { } remove { } }
        public event Action<int>? OnExit { add { } remove { } }
        public void SendInput(string input) => _inner.SendInput(input);
        public void Resize(int cols, int rows) => _inner.Resize(cols, rows);
        public void StartRecording(string filePath) { }
        public void StopRecording() { }
        public void EnableFlightRecording(long maxTotalBytes) { }
        public void DisableFlightRecording() { }
        public bool TryExportFlightRecording(string filePath, out Ntilde.Replay.FlightExportInfo info) { info = default; return false; }
        public void Dispose() => _inner.Dispose();
    }

    [AvaloniaFact]
    public async Task Wrapped_session_cannot_supply_its_own_asserted_native_wait_fact()
    {
        var wrapper = new ClaimedWaitWrapper(); var (window, pane) = CreateWindow(wrapper, qa: true);
        var input = new ReadNativeChildWaitParams
        {
            PaneId = pane.PaneId,
            RunId = PrivateClipboardQaModeTests.Run,
            ExpectedChildPid = 123,
            ExpectedNativeSessionId = wrapper.Id
        };
        Assert.Null(await Read(window, pane, input)); Assert.Equal(0, wrapper.WaitReads);
    }
}
