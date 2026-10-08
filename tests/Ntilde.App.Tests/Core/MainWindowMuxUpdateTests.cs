using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Ntilde.Controls;
using Ntilde.Mux;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Daemon;
using Ntilde.Mux.Tests.Support;
using Ntilde.Pty;
using Ntilde.Shell;
using Ntilde.Shell.Mux;
using Ntilde.Tests.Controls; // FakeTerminalSession, RecordingSessionFactory

namespace Ntilde.Tests.Core;

/// <summary>
/// Phase 5 Task 23: an update now keeps a compatible daemon running (R9, R10), so the new build can find the previous
/// build's daemon still serving its shells. The window says so once per launch, offering "Restart multiplexer now":
/// asked, then <c>shutdown</c> and up to 5 s for the exit, then by pid as <c>kill-server --force</c> does, then a warm-up
/// that starts a daemon of this build. A daemon of another protocol version - the "Session not persistent" fallback -
/// gets the same action next to its hint.
/// </summary>
/// <remarks>
/// The window's daemon is an in-memory one: <see cref="_old"/> (reporting <see cref="PreviousBuild"/>) until it is told
/// to shut down, then <see cref="_current"/>, this build's - what the launcher's spawn gives, which the host's connect
/// counts (<see cref="_connects"/>). <see cref="TestAppDataRoot"/> is taken for its lifetime: the window restores and saves
/// its session there.
/// </remarks>
public sealed class MainWindowMuxUpdateTests : IClassFixture<TestAppDataRoot>, IDisposable
{
    private const string ThisBuild = "0.12.0";
    private const string PreviousBuild = "0.0.1";

    private static readonly MuxEndpointDescriptor OldDaemon = new() { Endpoint = "test", Pid = 4242, ProcessName = "Ntilde", MinVersion = 1, MaxVersion = 2 };

    private readonly MuxTestHost _old = new(new MuxServerOptions { ForceConPtyFiltering = false, AppVersion = PreviousBuild });
    private readonly MuxTestHost _current = new(new MuxServerOptions { ForceConPtyFiltering = false, AppVersion = ThisBuild });
    private readonly TaskCompletionSource<bool> _oldExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<MuxConnectionHost> _hosts = [];
    private volatile bool _oldStopped;
    private int _connects;
    private int _oldShutdowns;

    public MainWindowMuxUpdateTests()
    {
        if (File.Exists(AppPaths.SessionFilePath)) File.Delete(AppPaths.SessionFilePath);
        // What a real daemon does with shutdown: it exits, and every connection to it drops.
        _old.Server.ShutdownRequested += () =>
        {
            Interlocked.Increment(ref _oldShutdowns);
            _oldStopped = true;
            _ = Task.Run(() =>
            {
                _old.Server.Dispose();
                _oldExited.TrySetResult(true);
            });
        };
    }

    public void Dispose()
    {
        TestMainWindowFactory.DisposeCreatedWindows();
        foreach (MuxConnectionHost host in _hosts) host.Dispose();
        _old.Dispose();
        _current.Dispose();
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
    /// A window whose local daemon is <see cref="_old"/> until it stops, then <see cref="_current"/>; persistence on unless
    /// <paramref name="persistence"/> says otherwise. <paramref name="connect"/> replaces the host's connect.
    /// </summary>
    private MainWindow CreateWindow(string persistence = SessionPersistenceMode.KeepOnClose, Func<CancellationToken, Task<MuxClient>>? connect = null)
    {
        var host = new MuxConnectionHost(connect ?? (ct =>
        {
            Interlocked.Increment(ref _connects);
            return MuxClient.ConnectAsync((_oldStopped ? _current : _old).Listener.Connect(), null, ct);
        }), "test", null);
        _hosts.Add(host);
        var factory = new MuxTerminalSessionFactory(
            new MuxConnectionHosts(host, _ => null), new RecordingSessionFactory(new FakeTerminalSession()), null);
        MainWindow window = TestMainWindowFactory.Create(AppServices.BuildForDesigner() with
        {
            CommandAssist = TestCommandAssistServices.Instance,
            SessionFactory = factory,
            Settings = new TerminalSettings { SessionPersistence = persistence },
        });
        window.MuxThisBuildVersion = ThisBuild;
        window.MuxPreviousBuildOffers = new MuxPreviousBuildNotice.Offered(); // this test's launch
        window.MuxProbeForUpdate = async ct => _oldStopped ? null : await MuxClient.ConnectAsync(_old.Listener.Connect(), null, ct);
        window.MuxReadDescriptorForUpdate = () => OldDaemon;
        window.MuxWaitForDaemonExitForUpdate = _ => _oldExited.Task;
        window.Show();
        return window;
    }

    /// <summary>Three shells running in the old daemon, and a session file whose one tab shows them side by side.</summary>
    private Guid[] ThreePanesOnTheOldDaemon()
    {
        Guid[] ids = Task.Run(async () =>
        {
            MuxClient client = await _old.ConnectClientAsync();
            return new[] { await MuxTestHost.SpawnAsync(client), await MuxTestHost.SpawnAsync(client), await MuxTestHost.SpawnAsync(client) };
        }, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        var split = new PaneNode { Type = NodeType.Split, SplitOrientation = 0, Children = [.. ids.Select(LocalLeaf)] };
        var session = new NtildeSession { ActiveTabIndex = 0 };
        session.Tabs.Add(new TabSession { Title = "three", Root = split });
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.SessionFilePath)!);
        File.WriteAllText(AppPaths.SessionFilePath, JsonSerializer.Serialize(session, SessionSerializationContext.Default.NtildeSession));
        return ids;
    }

    private static PaneNode LocalLeaf(Guid id) => new()
    {
        Type = NodeType.Leaf,
        Command = "scripted",
        MuxSessionId = id.ToString("D"),
        MuxEndpoint = MuxEndpointId.Local.ToString(),
    };

    private static List<MuxClientSession> Attached(MainWindow window) =>
        window.AllPanesForTest().Select(p => p.Session).OfType<MuxClientSession>().Where(s => s.IsAttached).ToList();

    private static (bool Visible, string? Title, string? Message) Toast(MainWindow window) =>
        (window.FindControl<Border>("RecordingToast")!.IsVisible,
         window.FindControl<TextBlock>("RecordingToastTitle")!.Text,
         window.FindControl<TextBlock>("RecordingToastMessage")!.Text);

    private static Button ToastButton(MainWindow window, string name) => window.FindControl<Button>(name)!;

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static readonly string ThreeShellsNotice = MuxPreviousBuildNotice.LocalMessage(PreviousBuild, 3);

    /// <summary>Waits for the window's three panes to reattach their shells, and for the notice that names them.</summary>
    private static void PumpUntilOffered(MainWindow window)
    {
        PumpUntil(() => Attached(window).Count == 3, "the three panes reattached");
        PumpUntil(() => Toast(window).Message?.Contains(ThreeShellsNotice, StringComparison.Ordinal) == true, "the notice is shown");
    }

    /// <summary>
    /// Every event the host has queued so far has been handled, and what they posted to the UI thread has run: a notice
    /// they were going to raise is on the toast by now.
    /// </summary>
    private static void PumpUntilQuiet(MuxConnectionHost host)
    {
        PumpUntil(() => host.EventsForTest.IsCompleted, "the host's events were handled");
        for (int i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
    }

    /// <summary>
    /// One connection, three panes: one notice, naming the daemon's version and its three shells, with the restart. The
    /// panes share the host's connection, so it is offered once, not per pane - and not again when the connection drops
    /// and comes back to the same daemon.
    /// </summary>
    [AvaloniaFact]
    public void A_daemon_of_the_previous_build_is_offered_a_restart_once()
    {
        ThreePanesOnTheOldDaemon();
        MainWindow window = CreateWindow();
        MuxConnectionHost host = _hosts.Single();

        PumpUntilOffered(window);

        (bool visible, string? title, string? message) = Toast(window);
        Assert.True(visible);
        Assert.Equal(MuxPreviousBuildNotice.Title, title);
        Assert.Equal(ThreeShellsNotice, message);
        Assert.True(ToastButton(window, "RecordingToastAction").IsVisible);
        Assert.Equal("Restart multiplexer now", ToastButton(window, "RecordingToastAction").Content);
        Assert.Equal(1, Volatile.Read(ref _connects));

        // Dismissed, then the connection drops and comes back to the same old daemon: nothing more is offered.
        Click(ToastButton(window, "RecordingToastClose"));
        MuxClient first = host.CurrentClient!;
        first.Dispose();
        host.WarmUp();
        PumpUntil(() => host.CurrentClient is { } c && !ReferenceEquals(c, first), "the host reconnected");
        PumpUntilQuiet(host);

        Assert.Equal(2, Volatile.Read(ref _connects));
        Assert.Equal(PreviousBuild, host.CurrentClient!.ServerVersion);
        Assert.False(Toast(window).Visible, $"offered again: '{Toast(window).Message}'");
    }

    /// <summary>A daemon of this very build is what the window wants: nothing to say.</summary>
    [AvaloniaFact]
    public void A_daemon_of_this_build_gives_no_notice()
    {
        _oldStopped = true; // the daemon is this build's from the start
        MainWindow window = CreateWindow();
        MuxConnectionHost host = _hosts.Single();
        PumpUntil(() => Attached(window).Count == 1, "the pane attached");
        PumpUntilQuiet(host);

        Assert.Equal(ThisBuild, host.CurrentClient!.ServerVersion);
        Assert.False(Toast(window).Visible, $"a notice was shown: '{Toast(window).Message}'");
    }

    /// <summary>With SessionPersistence Off nothing new appears, whatever the daemon the window's panes still use reports.</summary>
    [AvaloniaFact]
    public void With_persistence_off_no_notice_is_shown()
    {
        ThreePanesOnTheOldDaemon();
        MainWindow window = CreateWindow(SessionPersistenceMode.Off);
        MuxConnectionHost host = _hosts.Single();
        PumpUntil(() => host.CurrentClient is not null, "the host connected");
        PumpUntilQuiet(host);

        Assert.Equal(PreviousBuild, host.CurrentClient!.ServerVersion);
        Assert.False(Toast(window).Visible && Toast(window).Title == MuxPreviousBuildNotice.Title, $"a notice was shown: '{Toast(window).Message}'");
    }

    /// <summary>
    /// The action, confirmed: asked with the daemon's count, <c>shutdown</c> sent, the exit waited for (it went, so
    /// nothing is killed by pid), then the host connects again - the launcher's spawn of a daemon of this build. Its panes
    /// meet the old daemon's end through their "multiplexer disconnected" path, and no notice follows for the new one.
    /// </summary>
    [AvaloniaFact]
    public void Restarting_shuts_the_old_daemon_down_and_connects_to_a_new_one()
    {
        ThreePanesOnTheOldDaemon();
        MainWindow window = CreateWindow();
        MuxConnectionHost host = _hosts.Single();
        PumpUntilOffered(window);
        var asked = new List<(string? Host, int Shells)>();
        window.ConfirmMuxRestart = (where, shells) => { asked.Add((where, shells)); return Task.FromResult(true); };
        var terminated = new List<MuxEndpointDescriptor>();
        window.MuxTerminateDaemon = d => { lock (terminated) terminated.Add(d); return true; };

        Click(ToastButton(window, "RecordingToastAction"));
        PumpUntil(() => host.CurrentClient is { ServerVersion: ThisBuild }, "the host connected to a daemon of this build");
        PumpUntilQuiet(host);

        Assert.Equal([(null, 3)], asked);
        Assert.Equal(1, Volatile.Read(ref _oldShutdowns));
        Assert.Equal(2, Volatile.Read(ref _connects));
        lock (terminated) Assert.Empty(terminated); // it exited on its own
        Assert.All(window.AllPanesForTest(), p => Assert.False(p.Session is MuxClientSession { IsConnected: true }, "a pane is still on the old daemon"));
        Assert.False(Toast(window).Visible, $"a notice followed the restart: '{Toast(window).Message}'");
    }

    /// <summary>Declined: nothing is sent, the old daemon keeps its shells, and this launch does not offer it again.</summary>
    [AvaloniaFact]
    public void Declining_the_restart_sends_nothing()
    {
        Guid[] ids = ThreePanesOnTheOldDaemon();
        MainWindow window = CreateWindow();
        MuxConnectionHost host = _hosts.Single();
        PumpUntilOffered(window);
        int asked = 0;
        window.ConfirmMuxRestart = (_, _) => { asked++; return Task.FromResult(false); };
        bool probed = false;
        window.MuxProbeForUpdate = _ => { probed = true; return Task.FromResult<MuxClient?>(null); };
        int terminated = 0;
        window.MuxTerminateDaemon = _ => { Interlocked.Increment(ref terminated); return true; };

        Click(ToastButton(window, "RecordingToastAction"));
        PumpUntil(() => asked == 1, "asked");
        PumpUntilQuiet(host);

        Assert.False(probed, "a daemon was probed to be shut down");
        Assert.Equal(0, Volatile.Read(ref _oldShutdowns));
        Assert.Equal(0, Volatile.Read(ref terminated));
        Assert.Equal(1, Volatile.Read(ref _connects));
        Assert.All(ids, id => Assert.False(_old.Mux(id).IsExited));
        Assert.Equal(3, Attached(window).Count);
        Assert.False(Toast(window).Visible);
    }

    /// <summary>
    /// A daemon of another protocol version cannot even be connected to, so the pane falls back to a plain shell and says
    /// so with the hint (kept as it is). The notice now carries the same action: it cannot be asked to shut down, so it is
    /// terminated by pid - as <c>kill-server --force</c> does - and the warm-up that follows does not wait out the
    /// failure cooldown the mismatch started.
    /// </summary>
    [AvaloniaFact]
    public void The_version_mismatch_fallback_offers_the_restart_and_it_terminates_by_pid()
    {
        bool replaced = false;
        int connects = 0;
        MainWindow window = CreateWindow(connect: ct =>
        {
            Interlocked.Increment(ref connects);
            return Volatile.Read(ref replaced)
                ? MuxClient.ConnectAsync(_current.Listener.Connect(), null, ct)
                : throw new MuxUnavailableException($"different version. {MuxDaemonLauncher.KillServerHint}", versionMismatch: true);
        });
        MuxConnectionHost host = _hosts.Single();
        window.MuxProbeForUpdate = _ => throw new MuxUnavailableException("different version", versionMismatch: true);
        var asked = new List<(string? Host, int Shells)>();
        window.ConfirmMuxRestart = (where, shells) => { asked.Add((where, shells)); return Task.FromResult(true); };
        var terminated = new List<MuxEndpointDescriptor>();
        window.MuxTerminateDaemon = d =>
        {
            lock (terminated) terminated.Add(d);
            Volatile.Write(ref replaced, true);
            return true;
        };

        PumpUntil(() => Toast(window).Title == TerminalPane.MuxUnavailableNoticeTitle, "the fallback notice is shown");
        Assert.Contains(TerminalPane.MuxVersionMismatchHint, Toast(window).Message, StringComparison.Ordinal);
        Assert.True(ToastButton(window, "RecordingToastAction").IsVisible);
        Assert.Equal("Restart multiplexer now", ToastButton(window, "RecordingToastAction").Content);
        int before = Volatile.Read(ref connects);

        Click(ToastButton(window, "RecordingToastAction"));
        PumpUntil(() => host.CurrentClient is { ServerVersion: ThisBuild }, "the host connected to a daemon of this build");

        Assert.Equal([(null, -1)], asked); // the count cannot be had from a daemon this build cannot speak to
        lock (terminated) Assert.Same(OldDaemon, Assert.Single(terminated));
        Assert.Equal(before + 1, Volatile.Read(ref connects));
    }
}
