using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.Mux;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Mux.Transport;
using Ntilde.Shell;
using Ntilde.Tests.Controls; // FakeTerminalSession, RecordingSessionFactory
using Ntilde.Tests.Core; // TestMainWindowFactory, TestAppDataRoot
using Ntilde.Update;

namespace Ntilde.Tests.Update;

/// <summary>
/// Spec §9: applying a staged update must not leave a daemon of the old build running beside the
/// new one. <see cref="MainWindow.ApplyStagedUpdateAsync"/> probes for a live daemon (never
/// spawning one), asks for confirmation when it has running sessions, and sends <c>shutdown</c>
/// before teardown + apply.
/// </summary>
/// <remarks>
/// <see cref="TestAppDataRoot"/> is taken for its lifetime for the same reason
/// <see cref="Ntilde.Tests.Core.MainWindowMuxLifecycleTests"/> takes it: a real MainWindow's
/// teardown saves the session, which must not land in the developer's own profile.
/// </remarks>
public sealed class UpdateClosesMuxTests : IClassFixture<TestAppDataRoot>, IDisposable
{
    private readonly MuxTestHost _mux = new();

    public UpdateClosesMuxTests()
    {
        if (File.Exists(AppPaths.SessionFilePath)) File.Delete(AppPaths.SessionFilePath);
    }

    public void Dispose()
    {
        TestMainWindowFactory.DisposeCreatedWindows();
        _mux.Dispose();
    }

    private static void PumpUntil(Func<bool> condition, string because, int ms = 10_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > ms) Assert.Fail($"Timed out: {because}");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
    }

    /// <summary>
    /// Runs <see cref="MainWindow.ApplyStagedUpdateAsync"/> to completion. The test thread here is
    /// not the Avalonia UI thread (this headless harness has no message loop of its own), so
    /// starting the task and pumping <see cref="Dispatcher.UIThread"/> jobs until it finishes -
    /// rather than blocking on it - is what keeps any UI-thread-affine continuation unstuck.
    /// </summary>
    private static Task RunToCompletion(MainWindow window)
    {
        Task task = window.ApplyStagedUpdateAsync();
        PumpUntil(() => task.IsCompleted, "ApplyStagedUpdateAsync finished");
        return task; // rethrows if it faulted
    }

    private MainWindow CreateWindow()
    {
        MainWindow window = TestMainWindowFactory.Create(AppServices.BuildForDesigner() with
        {
            CommandAssist = TestCommandAssistServices.Instance,
            SessionFactory = new RecordingSessionFactory(new FakeTerminalSession()),
        });
        window.Show();
        return window;
    }

    private static FakeApplyUpdateService StageUpdate(MainWindow window)
    {
        var service = new FakeApplyUpdateService();
        var coordinator = new UpdateCoordinator(service, () => true, _ => { }, _ => { });
        Assert.Equal(UpdateCheckOutcome.UpdateReady,
            Task.Run(() => coordinator.RunManualCheckAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken)
                .GetAwaiter().GetResult());
        window.UpdateCoordinatorForTest = coordinator;
        return service;
    }

    private sealed class FakeApplyUpdateService : IUpdateService
    {
        public bool IsSupported => true;
        public int ApplyCount { get; private set; }
        public Task<UpdateAvailability> CheckAndDownloadAsync(CancellationToken ct) =>
            Task.FromResult(new UpdateAvailability(true, "99.0.0"));
        public void ApplyAndRestart() => ApplyCount++;
    }

    [AvaloniaFact]
    public void No_daemon_applies_without_asking()
    {
        MainWindow window = CreateWindow();
        FakeApplyUpdateService service = StageUpdate(window);
        int confirmCalls = 0;
        window.MuxProbeForUpdate = _ => Task.FromResult<MuxClient?>(null);
        window.ConfirmSessionLossForUpdate = _ => { confirmCalls++; return Task.FromResult(true); };

        RunToCompletion(window).GetAwaiter().GetResult();

        Assert.Equal(0, confirmCalls);
        Assert.Equal(1, service.ApplyCount);
    }

    [AvaloniaFact]
    public void Declining_keeps_the_daemon_and_does_not_apply()
    {
        MainWindow window = CreateWindow();
        FakeApplyUpdateService service = StageUpdate(window);
        Guid sessionId = Task.Run(() =>
        {
            using MuxClient c = MuxClient.ConnectAsync(_mux.Listener.Connect(), null, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
            return MuxTestHost.SpawnAsync(c).GetAwaiter().GetResult();
        }).GetAwaiter().GetResult();
        Assert.NotEqual(Guid.Empty, sessionId);
        bool shutdownRaised = false;
        _mux.Server.ShutdownRequested += () => shutdownRaised = true;
        window.MuxProbeForUpdate = ct => MuxClient.ConnectAsync(_mux.Listener.Connect(), null, ct)!;
        window.ConfirmSessionLossForUpdate = message =>
        {
            Assert.Equal("1 multiplexed session will be closed by the update.", message);
            return Task.FromResult(false);
        };

        RunToCompletion(window).GetAwaiter().GetResult();

        Assert.Equal(0, service.ApplyCount);
        Assert.False(shutdownRaised);
        Assert.Contains(sessionId, _mux.Server.GetSessionIds());
    }

    [AvaloniaFact]
    public void Confirming_shuts_the_daemon_down_then_applies()
    {
        MainWindow window = CreateWindow();
        FakeApplyUpdateService service = StageUpdate(window);
        Task.Run(() =>
        {
            using MuxClient c = MuxClient.ConnectAsync(_mux.Listener.Connect(), null, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
            return MuxTestHost.SpawnAsync(c).GetAwaiter().GetResult();
        }).GetAwaiter().GetResult();
        bool shutdownRaised = false;
        _mux.Server.ShutdownRequested += () => shutdownRaised = true;
        window.MuxProbeForUpdate = ct => MuxClient.ConnectAsync(_mux.Listener.Connect(), null, ct)!;
        window.ConfirmSessionLossForUpdate = _ => Task.FromResult(true);

        RunToCompletion(window).GetAwaiter().GetResult();

        Assert.True(shutdownRaised);
        Assert.Equal(1, service.ApplyCount);
    }

    /// <summary>
    /// A daemon that answers the probe but errors the <c>listSessions</c> request leaves the
    /// session count unknown - there is nothing to confirm - but it is still a daemon of the old
    /// build that must not survive the update, so <c>shutdown</c> is still sent. Scripted with a
    /// hand-driven server end (<see cref="RawMuxConnection"/> over <see cref="InMemoryDuplexPipe"/>)
    /// rather than <see cref="MuxTestHost"/>, which has no way to make a real
    /// <c>MuxServer</c> fail one specific request.
    /// </summary>
    [AvaloniaFact]
    public void Listing_sessions_failure_still_attempts_shutdown_then_applies()
    {
        MainWindow window = CreateWindow();
        FakeApplyUpdateService service = StageUpdate(window);
        (Stream clientEnd, Stream serverEnd) = InMemoryDuplexPipe.Create(1 << 20);
        var raw = new RawMuxConnection(serverEnd);
        bool shutdownReceived = false;

        Task serverTask = Task.Run(async () =>
        {
            MuxRequest hello = await ReadRequestAsync(raw);
            Assert.Equal(MuxMethods.Hello, hello.Method);
            raw.Send(MuxFrames.Response(new MuxResponse
            {
                Id = hello.Id,
                Result = MuxFrames.ToElement(new WelcomeResult { Version = 1 }, MuxJsonContext.Default.WelcomeResult),
            }));

            MuxRequest list = await ReadRequestAsync(raw);
            Assert.Equal(MuxMethods.ListSessions, list.Method);
            raw.Send(MuxFrames.Response(new MuxResponse
            {
                Id = list.Id,
                Error = new MuxError { Code = MuxErrorCodes.Internal, Message = "scripted listSessions failure" },
            }));

            MuxRequest shutdown = await ReadRequestAsync(raw);
            Assert.Equal(MuxMethods.Shutdown, shutdown.Method);
            shutdownReceived = true;
            raw.Send(MuxFrames.Response(new MuxResponse
            {
                Id = shutdown.Id,
                Result = MuxFrames.ToElement(new MuxEmpty(), MuxJsonContext.Default.MuxEmpty),
            }));
        }, TestContext.Current.CancellationToken);

        window.MuxProbeForUpdate = ct => MuxClient.ConnectAsync(clientEnd, null, ct)!;
        window.ConfirmSessionLossForUpdate = _ =>
            throw new InvalidOperationException("must not be asked: the session count is unknown when listing failed");

        RunToCompletion(window).GetAwaiter().GetResult();
        serverTask.GetAwaiter().GetResult(); // observes the scripted server's own assertions too

        Assert.True(shutdownReceived);
        Assert.Equal(1, service.ApplyCount);

        raw.Dispose();
        clientEnd.Dispose();
    }

    private static async Task<MuxRequest> ReadRequestAsync(RawMuxConnection raw)
    {
        using MuxInboundFrame frame = await raw.ReadAsync() ?? throw new EndOfStreamException();
        Assert.Equal(MuxFrameKind.Request, frame.Kind);
        return MuxFrames.ParseJson(frame.Payload, MuxJsonContext.Default.MuxRequest);
    }

    /// <summary>
    /// The toast button, the palette command and About's button can all reach
    /// <see cref="MainWindow.ApplyStagedUpdate"/>; nothing else stops a second click landing while
    /// the first call is still probing the daemon. The probe is gated on a
    /// <see cref="TaskCompletionSource{TResult}"/> so the second call is guaranteed to start while
    /// the first is still in flight, deterministically (no timing race).
    /// </summary>
    [AvaloniaFact]
    public void A_second_call_while_one_is_running_is_a_no_op()
    {
        MainWindow window = CreateWindow();
        FakeApplyUpdateService service = StageUpdate(window);
        Task.Run(() =>
        {
            using MuxClient c = MuxClient.ConnectAsync(_mux.Listener.Connect(), null, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
            return MuxTestHost.SpawnAsync(c).GetAwaiter().GetResult();
        }).GetAwaiter().GetResult();
        var gate = new TaskCompletionSource<MuxClient?>();
        int confirmCalls = 0;
        window.MuxProbeForUpdate = _ => gate.Task;
        window.ConfirmSessionLossForUpdate = _ => { Interlocked.Increment(ref confirmCalls); return Task.FromResult(true); };

        Task first = window.ApplyStagedUpdateAsync();
        Task second = window.ApplyStagedUpdateAsync();

        Assert.True(second.IsCompleted, "a second call while the first is still probing must be a synchronous no-op");
        Assert.False(first.IsCompleted, "the first call is still awaiting the gated probe");

        MuxClient released = Task.Run(() => MuxClient.ConnectAsync(_mux.Listener.Connect(), null, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken)
            .GetAwaiter().GetResult();
        gate.SetResult(released);
        PumpUntil(() => first.IsCompleted, "the first call finished");
        first.GetAwaiter().GetResult(); // rethrows if it faulted

        Assert.Equal(1, confirmCalls);
        Assert.Equal(1, service.ApplyCount);
    }

    [AvaloniaFact]
    public void A_failed_update_apply_leaves_the_teardown_runnable_for_the_later_close()
    {
        MainWindow window = CreateWindow();
        var coordinator = new UpdateCoordinator(new ThrowingApplyUpdateService(), () => true, _ => { }, _ => { });
        Assert.Equal(UpdateCheckOutcome.UpdateReady,
            Task.Run(() => coordinator.RunManualCheckAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken)
                .GetAwaiter().GetResult());
        window.UpdateCoordinatorForTest = coordinator;
        window.MuxProbeForUpdate = _ => Task.FromResult<MuxClient?>(null);

        RunToCompletion(window).GetAwaiter().GetResult();

        Assert.True(File.Exists(AppPaths.SessionFilePath), "the apply's own teardown saved the session");
        File.Delete(AppPaths.SessionFilePath);

        // The user closes the window as the failure toast asks: the session is saved again.
        typeof(MainWindow).GetMethod("PerformAppTeardown", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(window, null);
        Assert.True(File.Exists(AppPaths.SessionFilePath), "the close after a failed update saved the session again");
    }

    private sealed class ThrowingApplyUpdateService : IUpdateService
    {
        public bool IsSupported => true;
        public Task<UpdateAvailability> CheckAndDownloadAsync(CancellationToken ct) => Task.FromResult(new UpdateAvailability(true, "99.0.0"));
        public void ApplyAndRestart() => throw new IOException("Update.exe is locked");
    }
}
