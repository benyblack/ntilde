using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.Controls;
using Ntilde.Mux;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Platform.Ssh.Launch;
using Ntilde.Shell;
using Ntilde.Shell.Mux;
using Ntilde.Tests.Controls; // FakeTerminalSession, RecordingSessionFactory
using Ntilde.Tests.Core;

namespace Ntilde.Tests.Shell.Mux;

/// <summary>
/// Phase 5, ruling R6, at the window. Closing a local mux pane kills its shell from the UI thread
/// (<c>MainWindow.KillMuxSessionOnClose</c>). Over a stalled link - a local daemon that stops reading, which no liveness
/// ping ever drops - that send waited for the daemon, and the window froze for as long as the daemon did. The close
/// returns at once now, and the kill goes out once the daemon reads again.
/// </summary>
public sealed class MuxLocalCloseStalledLinkTests : IClassFixture<TestAppDataRoot>, IDisposable
{
    private static readonly TimeSpan Patient = TimeSpan.FromSeconds(30);

    /// <summary>How long a close may take on a stalled link before it counts as having waited for the daemon.</summary>
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(2);

    /// <summary>A close that waits for the daemon is let go after this long, so the test fails instead of hanging.</summary>
    private static readonly TimeSpan LetGoAfter = TimeSpan.FromSeconds(3);

    private readonly FakeMuxServerEnd _daemon = FakeMuxServerEnd.Create(FullSendQueue.PipeCapacityBytes);
    private readonly AnsweringDaemon _answering;
    private MuxConnectionHost? _host;

    public MuxLocalCloseStalledLinkTests()
    {
        // No saved session to restore: its panes would name sessions this daemon never had.
        if (File.Exists(AppPaths.SessionFilePath)) File.Delete(AppPaths.SessionFilePath);
        _answering = new AnsweringDaemon(_daemon);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        TestMainWindowFactory.DisposeCreatedWindows();
        _host?.Dispose();
        _daemon.Dispose();
    }

    [AvaloniaFact]
    public void Closing_a_local_mux_tab_on_a_stalled_link_returns_at_once_and_the_kill_goes_out_once_it_drains()
    {
        // The daemon answers the window's startup - a hello, then a spawn and an attach per tab - until a ping.
        Task<MuxRequest?> startup = _answering.ServeAsync(until: r => r.Method == MuxMethods.Ping);
        MainWindow window = CreateWindow();
        typeof(MainWindow).GetMethod("AddTab", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [null, SshDiagnosticsLevel.None]);
        PumpUntil(() => AllPanes(window).Count(p => p.Session is MuxClientSession { IsAttached: true }) == 2, "the second tab attached");
        IReadOnlyList<TerminalPane> panes = AllPanes(window);
        TerminalPane closing = panes[panes.Count - 1];
        Guid id = ((MuxClientSession)closing.Session!).Id;
        TabItem tab = window.FindControl<TabControl>("Tabs")!.Items.OfType<TabItem>().Single(t => ReferenceEquals(t.Content, closing));
        MuxClient client = _host!.CurrentClient!;

        // Then the daemon stops reading - it answers that ping and nothing after it - and the client's send queue fills.
        Assert.True(Task.Run(() => client.PingAsync(Ct), Ct).Wait(Patient, Ct), "the daemon answered the last ping");
        Assert.True(startup.Wait(Patient, Ct), "the daemon stopped reading");
        Assert.True(Task.Run(() => FullSendQueue.FillAsync(client), Ct).Wait(Patient, Ct), "the send queue filled");

        // The daemon reads again once the close has returned, or a while after it has not: a close that waits for it
        // then fails on the stopwatch instead of hanging the test.
        var closeReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<MuxRequest?> kill = Task.Run(
            async () =>
            {
                await Task.WhenAny(closeReturned.Task, Task.Delay(LetGoAfter, Ct));
                return await _answering.ServeAsync(until: r => r.Method == MuxMethods.Kill);
            },
            Ct);

        var stopwatch = Stopwatch.StartNew();
        typeof(MainWindow).GetMethod("CloseTab", BindingFlags.NonPublic | BindingFlags.Instance, [typeof(TabItem)])!.Invoke(window, [tab]);
        stopwatch.Stop();
        closeReturned.TrySetResult();

        PumpUntil(() => kill.IsCompleted, "the kill reached the daemon", Patient);
        _ = _answering.ServeAsync(until: _ => false);   // and the daemon keeps answering, through the window's teardown
        Assert.True(stopwatch.Elapsed < Prompt, $"closing the tab took {stopwatch.Elapsed.TotalSeconds:F1} s: it waited on the stalled link");
        Assert.Equal(id, FullSendQueue.KilledSession(kill.Result ?? throw new EndOfStreamException("The client closed before it sent the kill.")));
    }

    private MainWindow CreateWindow()
    {
        _host = new MuxConnectionHost(ct => MuxClient.ConnectAsync(_daemon.ClientEnd, null, ct), "test", null)
        {
            DisposeFlushTimeout = TimeSpan.FromSeconds(1),
        };
        var factory = new MuxTerminalSessionFactory(_host, new RecordingSessionFactory(new FakeTerminalSession()), null);
        MainWindow window = TestMainWindowFactory.Create(AppServices.BuildForDesigner() with
        {
            CommandAssist = TestCommandAssistServices.Instance,
            SessionFactory = factory,
        });
        Assert.Same(_host, window.MuxHost);
        window.Show();
        PumpUntil(() => AllPanes(window).Any(p => p.Session is MuxClientSession { IsAttached: true }), "the first tab attached");
        return window;
    }

    private static IReadOnlyList<TerminalPane> AllPanes(MainWindow w) => w.AllPanesForTest();

    private static void PumpUntil(Func<bool> condition, string because, TimeSpan? timeout = null)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > (timeout ?? TimeSpan.FromSeconds(10))) Assert.Fail($"Timed out: {because}");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
    }

    /// <summary>
    /// The local daemon in miniature: it answers what a window's panes ask - a hello, spawns, attaches - for as long as
    /// it is reading. It reads only while a <see cref="ServeAsync"/> runs, one at a time.
    /// </summary>
    private sealed class AnsweringDaemon(FakeMuxServerEnd end)
    {
        /// <summary>
        /// Reads and answers until it has answered a request <paramref name="until"/> picks, and returns that one; null
        /// when the client closed first.
        /// </summary>
        public Task<MuxRequest?> ServeAsync(Func<MuxRequest, bool> until) => Task.Run<MuxRequest?>(() =>
        {
            try
            {
                while (MuxFrameReader.Read(end.Raw.Stream) is { } frame)
                {
                    using (frame)
                    {
                        if (frame.Kind != MuxFrameKind.Request) continue;
                        MuxRequest request = MuxFrames.ParseJson(frame.Payload, MuxJsonContext.Default.MuxRequest);
                        Answer(request);
                        if (until(request)) return request;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // The test is over: the pipe closed under the read or the reply.
            }

            return null;
        });

        private void Answer(MuxRequest request)
        {
            if (request.Id == 0) return;   // fire-and-forget: resize, detach, input
            switch (request.Method)
            {
                case MuxMethods.Hello:
                    end.Reply(request.Id, new WelcomeResult { Version = MuxProtocol.SessionEventsVersion }, MuxJsonContext.Default.WelcomeResult);
                    break;
                case MuxMethods.Spawn:
                    end.Reply(request.Id, new SpawnResult { SessionId = Guid.NewGuid() }, MuxJsonContext.Default.SpawnResult);
                    break;
                case MuxMethods.Attach:
                    AttachParams attach = MuxFrames.ParseParams(request.Params, MuxJsonContext.Default.AttachParams);
                    end.Raw.Send(MuxFrames.Snapshot(request.Id, attach.SessionId, 0, FakeMuxServerEnd.SnapshotJson()));
                    break;
                case MuxMethods.ListSessions:
                    end.Reply(request.Id, new ListSessionsResult(), MuxJsonContext.Default.ListSessionsResult);
                    break;
                case MuxMethods.SessionInfo:
                    end.Reply(request.Id, new SessionInfoResult { Running = true }, MuxJsonContext.Default.SessionInfoResult);
                    break;
                default:   // ping, kill
                    end.Reply(request.Id, new MuxEmpty(), MuxJsonContext.Default.MuxEmpty);
                    break;
            }
        }
    }
}
