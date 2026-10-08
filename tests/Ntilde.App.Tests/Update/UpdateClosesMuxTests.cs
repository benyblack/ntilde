using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.Mux;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Mux.Transport;
using Ntilde.Shell;
using Ntilde.Shell.Mux;
using Ntilde.Tests.Controls; // FakeTerminalSession, RecordingSessionFactory
using Ntilde.Tests.Core; // TestMainWindowFactory, TestAppDataRoot
using Ntilde.Update;

namespace Ntilde.Tests.Update;

/// <summary>
/// Spec §9: applying a staged update must not leave a daemon of the old build running beside the
/// new one. <see cref="MainWindow.ApplyStagedUpdateAsync"/> probes for a live daemon (never
/// spawning one), asks for confirmation when it has running sessions, and sends <c>shutdown</c>
/// before teardown + apply - unless the update keeps it (Phase 5 R10): the new build speaks its
/// protocol (the release notes' marker) and the apply cannot kill it (its image is outside the
/// install root). A daemon whose descriptor cannot be read is never kept.
/// </summary>
/// <remarks>
/// <see cref="TestAppDataRoot"/> is taken for its lifetime for the same reason
/// <see cref="Ntilde.Tests.Core.MainWindowMuxLifecycleTests"/> takes it: a real MainWindow's
/// teardown saves the session, which must not land in the developer's own profile. Inside it no
/// descriptor exists, so a test that does not script one takes the path that closes the sessions.
/// </remarks>
public sealed class UpdateClosesMuxTests : IClassFixture<TestAppDataRoot>, IDisposable
{
    private const string CompatibleNotes = "Fixes and features.\n\n<!-- ntilde-mux-protocol: 1-2 -->";
    private const string IncompatibleNotes = "<!-- ntilde-mux-protocol: 3-4 -->";
    private const string ClosesOneSession = "1 multiplexed session will be closed by the update (the new version cannot keep it).";

    private static readonly string InstallRoot = Path.Combine(Path.GetTempPath(), "ntilde-update-tests", "NtildeApp");

    private static readonly MuxEndpointDescriptor Daemon = new() { Endpoint = "test", Pid = 4242, ProcessName = "Ntilde", MinVersion = 1, MaxVersion = 2 };

    private readonly MuxTestHost _mux = new();
    private readonly List<MuxConnectionHost> _hosts = [];

    public UpdateClosesMuxTests()
    {
        if (File.Exists(AppPaths.SessionFilePath)) File.Delete(AppPaths.SessionFilePath);
    }

    public void Dispose()
    {
        TestMainWindowFactory.DisposeCreatedWindows();
        foreach (MuxConnectionHost host in _hosts) host.Dispose();
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

    /// <summary>A window whose panes are the test daemon's shells (persistence on), probing that daemon for the update.</summary>
    private MainWindow CreateMuxWindow()
    {
        var host = new MuxConnectionHost(ct => MuxClient.ConnectAsync(_mux.Listener.Connect(), null, ct), "test", null);
        _hosts.Add(host);
        var factory = new MuxTerminalSessionFactory(
            new MuxConnectionHosts(host, _ => null), new RecordingSessionFactory(new FakeTerminalSession()), null);
        MainWindow window = TestMainWindowFactory.Create(AppServices.BuildForDesigner() with
        {
            CommandAssist = TestCommandAssistServices.Instance,
            SessionFactory = factory,
            Settings = new TerminalSettings { SessionPersistence = SessionPersistenceMode.KeepOnClose },
        });
        window.MuxProbeForUpdate = async ct => await _mux.ConnectClientAsync();
        window.Show();
        PumpUntil(() => LocalSession(window) is { IsAttached: true }, "the first pane attached");
        // The attach posts a coalesced session save (spec §9); let it run, so none is pending when the test starts.
        PumpUntil(() => File.Exists(AppPaths.SessionFilePath), "the attach's session save ran");
        return window;
    }

    private static MuxClientSession? LocalSession(MainWindow window) =>
        window.AllPanesForTest().Select(p => p.Session).OfType<MuxClientSession>().FirstOrDefault();

    private static FakeApplyUpdateService StageUpdate(MainWindow window, string? releaseNotes = null)
    {
        var service = new FakeApplyUpdateService { StagedReleaseNotes = releaseNotes };
        var coordinator = new UpdateCoordinator(service, () => true, _ => { }, _ => { });
        Assert.Equal(UpdateCheckOutcome.UpdateReady,
            Task.Run(() => coordinator.RunManualCheckAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken)
                .GetAwaiter().GetResult());
        window.SetUpdateCoordinatorForTest(coordinator);
        return service;
    }

    private sealed class FakeApplyUpdateService : IUpdateService
    {
        public bool IsSupported => true;
        public int ApplyCount { get; private set; }
        public string? StagedReleaseNotes { get; init; }
        public Task<UpdateAvailability> CheckAndDownloadAsync(CancellationToken ct) =>
            Task.FromResult(new UpdateAvailability(true, "99.0.0"));
        public void ApplyAndRestart() => ApplyCount++;
    }

    /// <summary>
    /// R10: the new build speaks the daemon's protocol, and the daemon runs from its own copy outside the install root,
    /// so the apply does not kill it. Nothing is asked and no <c>shutdown</c> is sent: the teardown detaches as any close
    /// does, and saves the session naming the shell, which the new build reattaches.
    /// </summary>
    [AvaloniaFact]
    public void A_compatible_daemon_outside_the_install_root_is_kept_without_asking()
    {
        MainWindow window = CreateMuxWindow();
        Guid own = LocalSession(window)!.Id;
        FakeApplyUpdateService service = StageUpdate(window, CompatibleNotes);
        int confirms = 0;
        int shutdowns = 0;
        _mux.Server.ShutdownRequested += () => Interlocked.Increment(ref shutdowns);
        window.ConfirmSessionLossForUpdate = _ => { confirms++; return Task.FromResult(true); };
        window.MuxReadDescriptorForUpdate = () => Daemon;
        window.MuxInstallRootForUpdate = () => InstallRoot;
        window.MuxDaemonImagePathForUpdate = _ => Path.Combine(Path.GetTempPath(), "ntilde-update-tests", "ntilde", "bin", "0.12.0", "Ntilde.exe");

        RunToCompletion(window).GetAwaiter().GetResult();

        Assert.Equal(0, confirms);
        Assert.Equal(1, service.ApplyCount);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, Volatile.Read(ref shutdowns));
        Assert.Contains(own, _mux.Server.GetSessionIds());
        Assert.False(_mux.Mux(own).IsExited);
        Assert.Contains(own.ToString(), File.ReadAllText(AppPaths.SessionFilePath)); // named for the new build to reattach
    }

    /// <summary>
    /// The new build cannot speak the daemon's protocol: today's question (worded for why), then <c>shutdown</c>. The
    /// session is saved before the shutdown is sent, naming none of the shells about to die, so the next launch starts
    /// fresh ones quietly instead of reporting the sessions the user just agreed to close as lost; and the teardown that
    /// follows does not save over it with the panes the shutdown has since ended.
    /// </summary>
    [AvaloniaFact]
    public void An_incompatible_daemon_is_closed_after_asking_and_the_session_is_saved_before_the_shutdown()
    {
        MainWindow window = CreateMuxWindow();
        Guid own = LocalSession(window)!.Id;
        FakeApplyUpdateService service = StageUpdate(window, IncompatibleNotes);
        var steps = new List<string>();
        string? savedAtShutdown = null;
        var shutdownSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mux.Server.ShutdownRequested += () =>
        {
            savedAtShutdown = File.Exists(AppPaths.SessionFilePath) ? File.ReadAllText(AppPaths.SessionFilePath) : null;
            // Removed once recorded: a save after the shutdown would bring it back.
            if (savedAtShutdown is not null) File.Delete(AppPaths.SessionFilePath);
            lock (steps) steps.Add("shutdown");
            shutdownSeen.TrySetResult(true);
        };
        string? asked = null;
        window.ConfirmSessionLossForUpdate = message =>
        {
            lock (steps) steps.Add("confirm");
            asked = message;
            return Task.FromResult(true);
        };
        window.MuxReadDescriptorForUpdate = () => Daemon;
        window.MuxInstallRootForUpdate = () => null; // not a Velopack install: the protocol ranges alone decide
        // The teardown cannot run before the shutdown was seen: the apply waits for the daemon's exit first.
        window.MuxWaitForDaemonExitForUpdate = _ => shutdownSeen.Task;

        RunToCompletion(window).GetAwaiter().GetResult();

        lock (steps) Assert.Equal(["confirm", "shutdown"], steps);
        Assert.NotNull(savedAtShutdown); // saved before the shutdown was sent
        Assert.DoesNotContain(own.ToString(), savedAtShutdown);
        Assert.Equal(1, service.ApplyCount);
        Assert.False(File.Exists(AppPaths.SessionFilePath), "the teardown saved over the session saved before the shutdown");
        Assert.Equal(ClosesOneSession, asked);
    }

    /// <summary>
    /// The coalesced save each attach posts (spec §9) can run after the update saved the session for its shutdown. It
    /// leaves out the shells already ended, as every other save does, so it never names them again.
    /// </summary>
    [AvaloniaFact]
    public void A_save_posted_by_an_attach_leaves_out_the_shells_already_ended()
    {
        MainWindow window = CreateMuxWindow();
        var pane = window.AllPanesForTest().Single(p => p.Session is MuxClientSession);
        Guid own = ((MuxClientSession)pane.Session!).Id;
        const System.Reflection.BindingFlags Private = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        ((HashSet<Guid>)typeof(MainWindow).GetField("_localSessionsEndedOnClose", Private)!.GetValue(window)!).Add(own);
        File.Delete(AppPaths.SessionFilePath);

        typeof(MainWindow).GetMethod("OnPanePersistentSessionAttached", Private)!.Invoke(window, [pane]);
        PumpUntil(() => File.Exists(AppPaths.SessionFilePath), "the posted save ran");

        Assert.DoesNotContain(own.ToString(), File.ReadAllText(AppPaths.SessionFilePath));
    }

    /// <summary>
    /// R10: a daemon whose image is inside the install root - one a pre-Phase-5 build started from <c>current\</c> -
    /// or whose image cannot be read is killed by the apply whatever the protocol says: today's question and shutdown.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_compatible_daemon_the_apply_would_kill_is_closed_after_asking(bool imageKnown)
    {
        MainWindow window = CreateMuxWindow();
        FakeApplyUpdateService service = StageUpdate(window, CompatibleNotes);
        int shutdowns = 0;
        _mux.Server.ShutdownRequested += () => Interlocked.Increment(ref shutdowns);
        string? asked = null;
        window.ConfirmSessionLossForUpdate = message => { asked = message; return Task.FromResult(true); };
        window.MuxReadDescriptorForUpdate = () => Daemon;
        window.MuxInstallRootForUpdate = () => InstallRoot;
        window.MuxDaemonImagePathForUpdate = _ => imageKnown ? Path.Combine(InstallRoot, "current", "Ntilde.exe") : null;
        window.MuxWaitForDaemonExitForUpdate = _ => Task.FromResult(true);

        RunToCompletion(window).GetAwaiter().GetResult();

        Assert.Equal(ClosesOneSession, asked);
        PumpUntil(() => Volatile.Read(ref shutdowns) == 1, "the daemon received shutdown");
        Assert.Equal(1, service.ApplyCount);
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
        // Recorded, not asserted, in the seam: a throw there counts as a decline and would pass unseen.
        string? asked = null;
        window.ConfirmSessionLossForUpdate = message =>
        {
            asked = message;
            return Task.FromResult(false);
        };

        RunToCompletion(window).GetAwaiter().GetResult();

        Assert.Equal(ClosesOneSession, asked);
        Assert.Equal(0, service.ApplyCount);
        Assert.False(shutdownRaised);
        Assert.Contains(sessionId, _mux.Server.GetSessionIds());
    }

    /// <summary>
    /// A confirmation dialog that itself throws (e.g. it fails to construct) must be treated as a
    /// decline, not as a fault that escapes the fire-and-forget <c>ApplyStagedUpdateAsync</c> and
    /// becomes an unobserved task exception - "yes" must never be inferred from a question that
    /// could not be asked.
    /// </summary>
    [AvaloniaFact]
    public void A_throwing_confirmation_counts_as_a_decline()
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
        window.ConfirmSessionLossForUpdate = _ => throw new InvalidOperationException("scripted confirmation failure");

        Task task = RunToCompletion(window); // must not fault: a throwing confirmation is a decline

        Assert.False(task.IsFaulted);
        Assert.Equal(0, service.ApplyCount);
        Assert.False(shutdownRaised);
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
    /// Final-fix item 5: after <c>shutdown</c>, the apply waits for the daemon process named by the
    /// descriptor to be gone (the apply replaces the executable it runs from), without blocking the
    /// UI thread. The wait is gated here so the ordering is deterministic. The update's build cannot
    /// speak the daemon's protocol, so it is shut down (R10).
    /// </summary>
    [AvaloniaFact]
    public void The_apply_waits_for_the_daemon_to_exit_after_shutdown()
    {
        MainWindow window = CreateWindow();
        FakeApplyUpdateService service = StageUpdate(window, IncompatibleNotes);
        bool shutdownRaised = false;
        _mux.Server.ShutdownRequested += () => shutdownRaised = true;
        var descriptor = new MuxEndpointDescriptor { Endpoint = "test", Pid = 4242, ProcessName = "ntilde", MinVersion = 1, MaxVersion = 1 };
        var exited = new TaskCompletionSource<bool>();
        MuxEndpointDescriptor? waitedFor = null;
        window.MuxProbeForUpdate = ct => MuxClient.ConnectAsync(_mux.Listener.Connect(), null, ct)!;
        window.MuxReadDescriptorForUpdate = () => descriptor;
        window.MuxWaitForDaemonExitForUpdate = d => { waitedFor = d; return exited.Task; };

        Task task = window.ApplyStagedUpdateAsync();
        PumpUntil(() => waitedFor is not null, "the apply started waiting for the daemon to exit");

        Assert.True(shutdownRaised, "shutdown was sent before the wait");
        Assert.Same(descriptor, waitedFor);
        Dispatcher.UIThread.RunJobs(); // the UI thread is free while the wait is pending
        Assert.False(task.IsCompleted);
        Assert.Equal(0, service.ApplyCount);

        exited.SetResult(true);
        PumpUntil(() => task.IsCompleted, "ApplyStagedUpdateAsync finished");
        task.GetAwaiter().GetResult();
        Assert.Equal(1, service.ApplyCount);
    }

    [AvaloniaFact]
    public void No_descriptor_skips_the_exit_wait()
    {
        MainWindow window = CreateWindow();
        FakeApplyUpdateService service = StageUpdate(window);
        int waits = 0;
        window.MuxProbeForUpdate = ct => MuxClient.ConnectAsync(_mux.Listener.Connect(), null, ct)!;
        window.MuxReadDescriptorForUpdate = () => null;
        window.MuxWaitForDaemonExitForUpdate = _ => { waits++; return Task.FromResult(true); };

        RunToCompletion(window).GetAwaiter().GetResult();

        Assert.Equal(0, waits);
        Assert.Equal(1, service.ApplyCount);
    }

    /// <summary>Final-fix item 11: the update's question is worded for the update, not for closing a pane.</summary>
    [AvaloniaFact]
    public void The_default_update_confirmation_is_worded_for_the_update()
    {
        MainWindow window = CreateWindow();

        Task<bool> answer = window.ConfirmSessionLossForUpdate("2 multiplexed sessions will be closed by the update.");
        Dispatcher.UIThread.RunJobs();

        Avalonia.Controls.Window dialog = Assert.Single(window.OwnedWindows);
        Assert.Equal("Apply Update", dialog.Title);
        var buttons = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(dialog)
            .OfType<Avalonia.Controls.Button>().Select(b => b.Content as string).ToList();
        Assert.Contains("Close sessions and update", buttons);
        Assert.Contains("Cancel", buttons);
        Assert.DoesNotContain("Close Pane", buttons);

        dialog.Close();
        PumpUntil(() => answer.IsCompleted, "the dialog closed");
        Assert.False(answer.Result); // closed without confirming
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

    /// <summary>
    /// Also when the daemon was shut down first: that path saves the session before the shutdown and its teardown does
    /// not save again, but only that once - the close after the failed apply saves as any close does.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_failed_update_apply_leaves_the_teardown_runnable_for_the_later_close(bool daemonShutDown)
    {
        MainWindow window = CreateWindow();
        var coordinator = new UpdateCoordinator(new ThrowingApplyUpdateService(), () => true, _ => { }, _ => { });
        Assert.Equal(UpdateCheckOutcome.UpdateReady,
            Task.Run(() => coordinator.RunManualCheckAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken)
                .GetAwaiter().GetResult());
        window.SetUpdateCoordinatorForTest(coordinator);
        int shutdowns = 0;
        _mux.Server.ShutdownRequested += () => Interlocked.Increment(ref shutdowns);
        window.MuxProbeForUpdate = daemonShutDown
            ? ct => MuxClient.ConnectAsync(_mux.Listener.Connect(), null, ct)!
            : _ => Task.FromResult<MuxClient?>(null);

        RunToCompletion(window).GetAwaiter().GetResult();

        if (daemonShutDown) PumpUntil(() => Volatile.Read(ref shutdowns) == 1, "the daemon (no descriptor: not kept) received shutdown");
        Assert.True(File.Exists(AppPaths.SessionFilePath), "the apply saved the session");
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

    /// <summary>PR #489 follow-up: a teardown that throws must be reported, never an unobserved fault, and must not apply.</summary>
    [AvaloniaFact]
    public void A_teardown_that_throws_is_reported_and_the_update_is_not_applied()
    {
        MainWindow window = CreateWindow();
        FakeApplyUpdateService service = StageUpdate(window);
        window.MuxProbeForUpdate = _ => Task.FromResult<MuxClient?>(null);
        window.TeardownFaultForTest = () => throw new InvalidOperationException("scripted teardown failure");

        Task task = window.ApplyStagedUpdateAsync();
        PumpUntil(() => task.IsCompleted, "ApplyStagedUpdateAsync finished");

        Assert.True(task.IsCompletedSuccessfully, $"the apply path faulted: {task.Exception?.GetBaseException().Message}");
        Assert.Equal(0, service.ApplyCount);
        Assert.Equal("Update could not be applied", window.FindControl<Avalonia.Controls.TextBlock>("RecordingToastTitle")!.Text);

        // The window is still up; its later close must run the whole teardown again (it saves the session).
        window.TeardownFaultForTest = null;
        if (File.Exists(AppPaths.SessionFilePath)) File.Delete(AppPaths.SessionFilePath);
        typeof(MainWindow).GetMethod("PerformAppTeardown", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(window, null);
        Assert.True(File.Exists(AppPaths.SessionFilePath));
    }
}
