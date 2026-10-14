using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Ntilde.AgentHost;
using Ntilde.AgentHost.Contracts;
using Ntilde.AppTests.AgentHost;
using Ntilde.Mux;
using Ntilde.Mux.Tests.Support;
using Ntilde.Shell;
using Ntilde.Shell.Mux;
using Ntilde.Tests.Controls; // FakeTerminalSession, RecordingSessionFactory
using Ellipse = Avalonia.Controls.Shapes.Ellipse;

namespace Ntilde.Tests.Core;

/// <summary>
/// The window's side of the agent host's windowless sessions (Phase 5 spec §3): it publishes its source of them
/// whenever it publishes its other agent-host bridges, takes it back before its mux hosts close, and its agent light
/// reports a windowless read, which no pane indicator can show.
/// </summary>
/// <remarks>
/// The window talks to <see cref="AgentHostService.Instance"/>. Each test points that, on this thread, at a service
/// of its own (its own endpoint name, discovery directory, registry and clock), so turning observe on starts nothing
/// another test or a real ntilde could see. <see cref="TestAppDataRoot"/> is taken because the teardown saves the
/// session. The panes spawn into an in-memory daemon, so no real shell starts.
/// </remarks>
public sealed class MainWindowAgentWindowlessTests : IClassFixture<TestAppDataRoot>, IDisposable
{
    private static readonly Color Quiet = Color.Parse("#6B737F");
    private static readonly Color Active = Color.Parse("#4FB0D4");

    private readonly MuxTestHost _mux = new();
    private readonly string _tempDir = AgentHostTestEndpoint.CreateTempDir("mwwl");
    private readonly ManualClock _clock = new();
    private readonly AgentHostService _service;
    private MuxConnectionHost? _host;

    public MainWindowAgentWindowlessTests()
    {
        if (File.Exists(AppPaths.SessionFilePath)) File.Delete(AppPaths.SessionFilePath);
        _service = new AgentHostService(
            new AgentSessionRegistry(), AgentHostTestEndpoint.CreateEndpoint(_tempDir), _tempDir, _tempDir,
            new AgentActivityJournal(), _clock.Now);
    }

    public void Dispose()
    {
        TestMainWindowFactory.DisposeCreatedWindows();
        _service.Dispose();
        _host?.Dispose();
        _mux.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A window with session persistence on (an in-memory daemon behind it) and agent observe on.</summary>
    private MainWindow CreateWindow()
    {
        _host = new MuxConnectionHost(ct => MuxClient.ConnectAsync(_mux.Listener.Connect(), null, ct), "test", null)
        {
            DisposeFlushTimeout = TimeSpan.FromSeconds(1),
        };
        var factory = new MuxTerminalSessionFactory(
            new MuxConnectionHosts(_host, _ => null), new RecordingSessionFactory(new FakeTerminalSession()), null);
        MainWindow window = TestMainWindowFactory.Create(AppServices.BuildForDesigner() with
        {
            CommandAssist = TestCommandAssistServices.Instance,
            SessionFactory = factory,
            Settings = new TerminalSettings { AgentAccessObserveEnabled = true },
        });
        window.Show();
        PumpUntil(() => window.AllPanesForTest().Any(p => p.Session is MuxClientSession { IsAttached: true }), "the first pane attached");
        return window;
    }

    private static void Teardown(MainWindow window)
        => typeof(MainWindow).GetMethod("PerformAppTeardown", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);

    private static void PumpUntil(Func<bool> condition, string because, int ms = 10_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Dispatcher.UIThread.RunJobs();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > ms) Assert.Fail($"Timed out: {because}");
            Thread.Sleep(10);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static Color? Fill(Ellipse dot) => (dot.Fill as ISolidColorBrush)?.Color;

    [AvaloniaFact]
    public void The_window_publishes_its_windowless_sessions_with_its_other_bridges_and_takes_them_back_at_teardown()
    {
        using var _ = AgentHostService.OverrideInstanceForTesting(_service);
        MainWindow window = CreateWindow();

        IWindowlessSessionSource? published = window.WindowlessSessions;
        Assert.NotNull(published);
        Assert.True(_service.IsRunning);
        Assert.Same(published, _service.WindowlessSource); // the constructor's publish

        // Saving Settings publishes everything again (persistence may just have been turned on).
        _service.SetWindowlessSource(null);
        window.ApplyAgentHostSettingsLive();
        Assert.Same(published, _service.WindowlessSource);

        // Taken back before the hosts it asks close, not only by the endpoint's Stop at the very end.
        bool hostClosed = false;
        IWindowlessSessionSource? whenTheHostClosed = published;
        _host!.Closed += _ =>
        {
            hostClosed = true;
            whenTheHostClosed = _service.WindowlessSource;
        };

        Teardown(window);

        Assert.True(hostClosed);
        Assert.Null(whenTheHostClosed);
        Assert.Null(_service.WindowlessSource);
        Assert.False(_service.IsRunning);
    }

    [AvaloniaFact]
    public void A_windowless_read_lights_the_agent_light_until_the_read_decays()
    {
        using var _ = AgentHostService.OverrideInstanceForTesting(_service);
        MainWindow window = CreateWindow();
        try
        {
            var indicator = window.FindControl<Button>("AgentObserveIndicator")!;
            var dot = window.FindControl<Ellipse>("AgentObserveIndicatorDot")!;
            Assert.True(indicator.IsVisible);
            Assert.Equal(Quiet, Fill(dot));

            var source = new StubWindowlessSource();
            var session = source.Add("read by an agent");
            _service.SetWindowlessSource(source);
            string line = $"{{\"v\":{AgentHostProtocol.Version},\"id\":1,\"method\":\"{AgentHostProtocol.Methods.ReadScreen}\",\"params\":{{\"paneId\":\"{session.SessionId}\"}}}}";

            // From the pool, as the endpoint's connection threads call it.
            var response = Task.Run(() => _service.HandleRequestLineAsync(line, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken)
                .GetAwaiter().GetResult();

            Assert.Null(response.Error);
            PumpUntil(() => Fill(dot) == Active, "the light came on for the windowless read");

            _clock.Advance(TimeSpan.FromSeconds(AgentAttentionMachine.ReadDecaySeconds));
            Task.Run(_service.SweepStatuses, TestContext.Current.CancellationToken).GetAwaiter().GetResult(); // the 1 s timer's work
            PumpUntil(() => Fill(dot) == Quiet, "the light went out once the read decayed");
        }
        finally
        {
            Teardown(window);
        }
    }

    /// <summary>A clock the test moves by hand.</summary>
    private sealed class ManualClock
    {
        private long _ticks = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero).UtcTicks;

        public DateTimeOffset Now() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }
}
