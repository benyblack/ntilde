using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.Controls;
using Ntilde.Mux;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Daemon;
using Ntilde.Mux.Tests.Support;
using Ntilde.Platform.Ssh.Launch;
using Ntilde.Pty;
using Ntilde.Shell;
using Ntilde.Shell.Mux;
using Ntilde.Tests.Controls; // FakeTerminalSession, RecordingSessionFactory
using Ntilde.Tests.Shell.Mux;
using FirstCloseAction = Ntilde.MainWindow.FirstCloseAction;
using FirstCloseAnswer = Ntilde.MainWindow.FirstCloseAnswer;

namespace Ntilde.Tests.Core;

/// <summary>
/// The Phase 5 manual checklist's local window steps, automated against the real multiplexer: a separate
/// <c>Ntilde mux serve</c> process (started by the launcher, as <see cref="MuxDaemonSmokeTests"/> starts it) running real
/// shells in real PTYs, driven through a headless <see cref="MainWindow"/>. Where the other window tests ask an in-memory
/// server which sessions it holds, these look at the shells' own processes.
/// <list type="bullet">
/// <item>steps 1-3: Keep running leaves the shells in one daemon, their children, and the next window brings them back;</item>
/// <item>step 5: a killed daemon shows its banner, and Enter starts a new daemon and a new shell, and says so;</item>
/// <item>step 6: closing a pane ends its shell, and only that one;</item>
/// <item>step 7: <c>ntilde mux kill-server</c> ends the daemon and every shell, and the window stays, its panes saying so;</item>
/// <item>step 8: a second instance takes no shell another window shows, and says so;</item>
/// <item>steps 12 and 13: Close them ends this window's shells but one another client shows; the next launch is quiet;</item>
/// <item>step 15: Quit and close all shells ends every shell and stops the daemon;</item>
/// <item>step 17: a relaunch after the daemon died starts fresh shells and says so;</item>
/// <item>step 19: the same shell in two windows echoes both ways;</item>
/// <item>steps 20 and 21: a detached shell keeps running, is not reopened at the next launch, and is announced;</item>
/// <item>step 54 (PR #512 H1): persistence turned Off, then the close, ends the window's shells and no other.</item>
/// </list>
/// </summary>
/// <remarks>
/// <c>Category=PtySmoke</c>: it starts real processes, so it runs in the PTY smoke lane (ubuntu and windows), not the gating
/// unit lane. Each test has its own <see cref="TestAppDataRoot"/>, so its own daemon; whatever it started is killed at the end.
/// </remarks>
[Trait("Category", "PtySmoke")]
[Collection(PtyRealShellCollection.Name)]
public sealed class MainWindowRealDaemonTests : IDisposable
{
    private static readonly TimeSpan Patient = TimeSpan.FromSeconds(30);

    private readonly TestAppDataRoot _root = new();
    private readonly MuxDaemonLauncher _launcher;
    private readonly List<MuxConnectionHost> _hosts = [];
    private readonly List<IDisposable> _owned = [];
    private readonly HashSet<int> _started = [];

    /// <summary>Every count a first-close or quit-and-close question was asked with, in order.</summary>
    private readonly List<int> _asked = [];

    public MainWindowRealDaemonTests()
    {
        _launcher = new MuxDaemonLauncher(DescriptorPath, MuxDaemonSmokeTests.Spawner()) { SpawnTimeout = Patient };
    }

    private string DescriptorPath => MuxDiscovery.GetDescriptorPath(_root.RootPath);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        TestMainWindowFactory.DisposeCreatedWindows();
        foreach (MuxConnectionHost host in _hosts) host.Dispose();
        for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        if (MuxDiscovery.TryReadDescriptor(DescriptorPath, out MuxEndpointDescriptor? descriptor)) _started.Add(descriptor.Pid);
        foreach (int pid in _started) Kill(pid);
        _root.Dispose();
    }

    // ── Steps 1-3 ──

    [AvaloniaFact]
    public void Keep_running_leaves_the_real_shells_in_the_daemon_and_the_next_window_brings_them_back()
    {
        MainWindow first = CreateWindow();
        AddTab(first);
        PumpUntil(() => Attached(first).Count == 2, "the second tab's shell attached");
        Guid typed = Attached(first)[0].Id;
        TypeInto(PaneOf(first, typed), "t30-kept");
        Guid[] ids = [.. Attached(first).Select(s => s.Id).Order()];
        Dictionary<Guid, int> pids = Attached(first).ToDictionary(s => s.Id, ShellPid);
        int daemon = DaemonPid();
        if (OperatingSystem.IsLinux())
        {
            Assert.All(pids.Values, pid => Assert.Equal(daemon, LinuxParentOf(pid)));
        }

        first.Close();
        PumpUntil(() => !first.IsVisible, "the window closed");

        Assert.Equal([2], _asked);
        Assert.All(pids.Values, pid => Assert.True(MuxDaemonSmokeTests.IsAlive(pid), $"shell {pid} ended with the window"));
        IReadOnlyList<SessionSummary> left = Sessions();
        Assert.Equal(ids, left.Select(s => s.SessionId).Order());
        Assert.All(left, s => Assert.True(s.Running && s.AttachedClients == 0, $"{s.SessionId}: running {s.Running}, {s.AttachedClients} attached"));
        Assert.Equal(daemon, DaemonPid());

        MainWindow second = CreateWindow();
        ShowEveryTab(second, 2);
        Assert.Equal(ids, Attached(second).Select(s => s.Id).Order());
        Assert.All(Attached(second), s => Assert.Equal(pids[s.Id], ShellPid(s)));
        PumpUntil(() => Count(Text(PaneOf(second, typed)), "t30-kept") >= 2, "the reattached screen shows the earlier output");
        Assert.NotEqual(TerminalPane.MuxPreviousLostNoticeTitle, Toast(second).Title);
        Assert.Equal(daemon, DaemonPid());
    }

    // ── Step 5 ──

    [AvaloniaFact]
    public void A_killed_daemon_shows_its_banner_and_Enter_starts_a_new_daemon_and_shell_and_says_so()
    {
        MainWindow window = CreateWindow();
        TerminalPane pane = PaneOf(window, Attached(window).Single().Id);
        var old = (MuxClientSession)pane.Session!;
        int oldShell = ShellPid(old);
        int daemon = DaemonPid();

        using (Process process = Process.GetProcessById(daemon)) process.Kill(entireProcessTree: false); // kill -9 mux serve, not its shells

        PumpUntil(() => Text(pane).Contains(TerminalPane.MuxDisconnectedBanner, StringComparison.Ordinal), "the pane shows the disconnected banner");
        pane.Reconnect(); // Enter
        PumpUntil(() => pane.Session is MuxClientSession { IsAttached: true } s && s.Id != old.Id, "Enter started a new shell");
        PumpUntil(() => Toast(window).Title == TerminalPane.MuxPreviousLostNoticeTitle, "the lost-session toast is shown");
        Assert.Equal(TerminalPane.MuxPreviousLostBanner, Toast(window).Message);
        Assert.NotEqual(daemon, DaemonPid());
        if (!OperatingSystem.IsWindows())
        {
            // Its PTY's master went with the daemon: the kernel hangs the shell up. (Windows' ConPTY is not asked here.)
            PumpUntil(() => !MuxDaemonSmokeTests.IsAlive(oldShell), "the old shell went with its daemon");
        }
    }

    // ── Step 6 ──

    [AvaloniaFact]
    public void Closing_a_pane_ends_its_real_shell_and_no_other()
    {
        MainWindow window = CreateWindow();
        AddTab(window);
        PumpUntil(() => Attached(window).Count == 2, "the second tab's shell attached");
        MuxClientSession closing = Attached(window)[1];
        MuxClientSession kept = Attached(window)[0];
        int closingPid = ShellPid(closing);
        int keptPid = ShellPid(kept);

        var close = (Task<bool>)typeof(MainWindow).GetMethod("ClosePaneAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(window, [PaneOf(window, closing.Id), true])!;
        PumpUntil(() => close.IsCompleted, "the pane closed");

        PumpUntil(() => !MuxDaemonSmokeTests.IsAlive(closingPid), "the closed pane's shell ended");
        Assert.DoesNotContain(closing.Id, Sessions().Select(s => s.SessionId));
        Assert.True(MuxDaemonSmokeTests.IsAlive(keptPid), "the other pane's shell ended too");
    }

    // ── Step 7 ──

    [AvaloniaFact]
    public void Kill_server_from_the_command_line_ends_the_daemon_and_its_real_shells_and_the_window_says_so()
    {
        MainWindow window = CreateWindow();
        AddTab(window);
        PumpUntil(() => Attached(window).Count == 2, "the second tab's shell attached");
        int[] shells = [.. Attached(window).Select(ShellPid)];
        TerminalPane[] panes = [.. Attached(window).Select(s => PaneOf(window, s.Id))];
        int daemon = DaemonPid();

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = OffTheUiThread(() => Task.FromResult(MuxCommand.Execute(["mux", "kill-server"], stdout, stderr, _root.RootPath)));

        Assert.True(code == 0, $"kill-server exited {code}: {stderr}");
        Assert.Equal("Multiplexer stopped.", stdout.ToString().Trim());
        PumpUntil(() => !MuxDaemonSmokeTests.IsAlive(daemon), "the daemon stopped");
        foreach (int pid in shells) PumpUntil(() => !MuxDaemonSmokeTests.IsAlive(pid), $"shell {pid} ended");
        foreach (TerminalPane pane in panes)
        {
            PumpUntil(() => Text(pane).Contains(TerminalPane.MuxDisconnectedBanner, StringComparison.Ordinal), "each pane shows the disconnected banner");
        }

        Assert.True(window.IsVisible, "the window closed with the shells");
        Assert.Equal(2, window.AllPanesForTest().Count);
    }

    // ── Step 8 ──

    [AvaloniaFact]
    public void A_second_instance_takes_no_shell_another_window_shows()
    {
        MainWindow before = CreateWindow();
        Guid shown = Attached(before).Single().Id;
        before.Close();
        PumpUntil(() => !before.IsVisible, "the earlier window closed, keeping its shell");
        MainWindow first = CreateWindow(); // reattaches it, and keeps it open
        Assert.Equal(shown, Attached(first).Single().Id);

        MainWindow second = CreateWindow(); // a second instance restoring the same session

        PumpUntil(() => Toast(second).Title == TerminalPane.MuxAttachedElsewhereNoticeTitle, "the in-use toast is shown");
        Assert.Equal(TerminalPane.MuxAttachedElsewhereBanner, Toast(second).Message);
        Assert.DoesNotContain(shown, Attached(second).Select(s => s.Id));
        PumpFor(TimeSpan.FromSeconds(1));
        Assert.NotEqual(TerminalPane.MuxPreviousLostNoticeTitle, Toast(second).Title);
        SessionSummary kept = Sessions().Single(s => s.SessionId == shown);
        Assert.Equal(1, kept.AttachedClients); // still the first window's only
    }

    // ── Steps 12 and 13 ──

    [AvaloniaFact]
    public void Close_them_ends_the_real_shell_and_the_next_launch_starts_a_fresh_one_quietly()
    {
        MainWindow first = CreateWindow(FirstCloseAction.Close);
        MuxClientSession own = Attached(first).Single();
        int ownPid = ShellPid(own);

        first.Close();
        PumpUntil(() => !first.IsVisible, "the window closed");

        Assert.Equal([1], _asked);
        PumpUntil(() => !MuxDaemonSmokeTests.IsAlive(ownPid), "Close them ended the shell");
        Assert.Empty(Sessions());

        MainWindow second = CreateWindow();
        Assert.NotEqual(own.Id, Attached(second).Single().Id);
        Assert.NotEqual(TerminalPane.MuxPreviousLostNoticeTitle, Toast(second).Title);
    }

    [AvaloniaFact]
    public void Close_them_keeps_the_real_shell_another_client_shows()
    {
        MainWindow window = CreateWindow(FirstCloseAction.Close);
        int ownPid = ShellPid(Attached(window).Single());
        (Guid shared, ClientPaneModel theirs) = SpawnElsewhere(Observer());
        int sharedPid = ShellPid(theirs.Session);
        TerminalPane mine = AttachInWindow(window, shared);
        PumpUntil(() => mine.MuxOtherClients == 1, "this window knows the shell is shared");

        window.Close();
        PumpUntil(() => !window.IsVisible, "the window closed");

        Assert.Equal([2], _asked);
        PumpUntil(() => !MuxDaemonSmokeTests.IsAlive(ownPid), "Close them ended this window's own shell");
        Assert.True(MuxDaemonSmokeTests.IsAlive(sharedPid), "Close them ended a shell another client shows");
        SessionSummary left = Assert.Single(Sessions());
        Assert.Equal(shared, left.SessionId);
        Assert.Equal(1, left.AttachedClients);
    }

    // ── Step 15 ──

    [AvaloniaFact]
    public void Quit_and_close_all_shells_ends_every_real_shell_and_stops_the_daemon()
    {
        MainWindow window = CreateWindow();
        int ownPid = ShellPid(Attached(window).Single());
        MuxClient other = Observer();
        (Guid shared, ClientPaneModel theirs) = SpawnElsewhere(other);
        int sharedPid = ShellPid(theirs.Session);
        AttachInWindow(window, shared);
        (_, ClientPaneModel detached) = SpawnElsewhere(other);
        int detachedPid = ShellPid(detached.Session);
        Detach(detached);
        int daemon = DaemonPid();

        Task quit = window.QuitAndCloseAllShellsAsync();
        PumpUntil(() => quit.IsCompleted && !window.IsVisible, "the window quit");

        Assert.Equal([3], _asked);
        foreach (int pid in new[] { ownPid, sharedPid, detachedPid })
        {
            PumpUntil(() => !MuxDaemonSmokeTests.IsAlive(pid), $"shell {pid} ended");
        }

        PumpUntil(() => !MuxDaemonSmokeTests.IsAlive(daemon), "the daemon stopped");
        Assert.False(File.Exists(DescriptorPath), "the stopped daemon left its descriptor");
    }

    // ── Step 17 ──

    [AvaloniaFact]
    public void A_relaunch_after_the_daemon_died_starts_fresh_real_shells_and_says_so()
    {
        MainWindow first = CreateWindow();
        AddTab(first);
        PumpUntil(() => Attached(first).Count == 2, "the second tab's shell attached");
        Guid[] gone = [.. Attached(first).Select(s => s.Id)];
        first.Close();
        PumpUntil(() => !first.IsVisible, "the window closed");
        int daemon = DaemonPid();
        Kill(daemon);
        PumpUntil(() => !MuxDaemonSmokeTests.IsAlive(daemon), "the daemon is gone");

        MainWindow second = CreateWindow();
        ShowEveryTab(second, 2);

        Assert.Empty(Attached(second).Select(s => s.Id).Intersect(gone));
        PumpUntil(() => Toast(second).Title == TerminalPane.MuxPreviousLostNoticeTitle, "the lost-session toast is shown");
        // Both tabs lost theirs: the two notices merge into one line when they land before the toast shows, else each shows alone.
        string[] lostLines = [TerminalPane.MuxPreviousLostBanner, MainWindow.BuildPersistenceNoticeMessage(TerminalPane.MuxPreviousLostNoticeTitle, TerminalPane.MuxPreviousLostBanner, 2)];
        Assert.Contains(Toast(second).Message, lostLines);
        Assert.NotEqual(daemon, DaemonPid());
    }

    // ── Step 19 ──

    [AvaloniaFact]
    public void The_same_real_shell_in_two_windows_echoes_both_ways()
    {
        MainWindow a = CreateWindow();
        Guid id = Attached(a).Single().Id;
        TerminalPane inA = PaneOf(a, id);
        MainWindow b = CreateWindow();
        TerminalPane inB = AttachInWindow(b, id);
        PumpUntil(() => inA.MuxOtherClients == 1 && inB.MuxOtherClients == 1, "both windows know the shell is shared");

        TypeInto(inA, "from-A");
        PumpUntil(() => Count(Text(inB), "from-A") >= 2, "B shows what A typed");
        TypeInto(inB, "from-B");
        PumpUntil(() => Count(Text(inA), "from-B") >= 2, "A shows what B typed");
    }

    // ── Steps 20 and 21 ──

    [AvaloniaFact]
    public void A_detached_real_shell_keeps_running_is_not_reopened_and_is_announced_once()
    {
        MainWindow first = CreateWindow();
        AddTab(first);
        PumpUntil(() => Attached(first).Count == 2, "the second tab's shell attached");
        MuxClientSession detaching = Attached(first)[1];
        int pid = ShellPid(detaching);

        Task<bool> detach = first.DetachPaneAsync(PaneOf(first, detaching.Id));
        PumpUntil(() => detach.IsCompleted, "the detach finished");

        Assert.True(detach.Result);
        PumpUntil(() => Toast(first).Title == "Shell detached", "the detach toast is shown");
        Assert.Equal("Shell kept running — Attach to session… to get it back", Toast(first).Message);
        // The toast is the window's. The detach reaches the daemon as a message nothing answers, so on a slow runner the
        // daemon records it a moment after the toast: wait for it there, as Detach does.
        PumpUntil(() => Sessions().Single(s => s.SessionId == detaching.Id) is { DetachedByUser: true, AttachedClients: 0 }, "the daemon recorded the user detach");
        Assert.True(Sessions().Single(s => s.SessionId == detaching.Id).Running, "the detached shell stopped running");

        first.Close();
        PumpUntil(() => !first.IsVisible, "the window closed");
        Assert.True(MuxDaemonSmokeTests.IsAlive(pid), "the detached shell ended with the window");

        MainWindow second = CreateWindow();
        PumpUntil(() => Toast(second).Title == "Detached shells", "the detached-shells toast is shown");
        Assert.Equal("1 detached shell is running — Attach to session… to reopen it", Toast(second).Message);
        Assert.DoesNotContain(detaching.Id, second.AllPanesForTest().Select(p => p.Session).OfType<MuxClientSession>().Select(s => s.Id));

        TerminalPane reopened = AttachInWindow(second, detaching.Id);
        Assert.Equal(pid, ShellPid((MuxClientSession)reopened.Session!));
        SessionSummary attached = Sessions().Single(s => s.SessionId == detaching.Id);
        Assert.True(attached.AttachedClients == 1 && !attached.DetachedByUser, $"{attached.AttachedClients} attached, detached {attached.DetachedByUser}");
    }

    // ── Step 54 (PR #512 H1) ──

    [AvaloniaFact]
    public void Turning_persistence_off_then_closing_ends_the_windows_real_shells_and_no_other()
    {
        MainWindow window = CreateWindow();
        int ownPid = ShellPid(Attached(window).Single());
        MuxClient other = Observer();
        (Guid shared, ClientPaneModel theirs) = SpawnElsewhere(other);
        int sharedPid = ShellPid(theirs.Session);
        TerminalPane mine = AttachInWindow(window, shared);
        PumpUntil(() => mine.MuxOtherClients == 1, "this window knows the shell is shared");
        (Guid detachedId, ClientPaneModel detached) = SpawnElsewhere(other);
        int detachedPid = ShellPid(detached.Session);
        Detach(detached);
        TurnPersistenceOff(window);

        window.Close();
        PumpUntil(() => !window.IsVisible, "the window closed");

        Assert.Empty(_asked); // Off asks nothing
        PumpUntil(() => !MuxDaemonSmokeTests.IsAlive(ownPid), "the window's own shell ended");
        Assert.True(MuxDaemonSmokeTests.IsAlive(sharedPid), "the close ended a shell another client shows");
        Assert.True(MuxDaemonSmokeTests.IsAlive(detachedPid), "the close ended a detached shell");
        Assert.Equal(new[] { detachedId, shared }.Order(), Sessions().Select(s => s.SessionId).Order());
    }

    // ── The window, the daemon and the shells ──

    /// <summary>A window with persistence on, whose local host reaches the test's real daemon through the launcher.</summary>
    private MainWindow CreateWindow(FirstCloseAction answer = FirstCloseAction.Keep)
    {
        var host = new MuxConnectionHost(ct => _launcher.EnsureConnectedAsync(ct), MuxDiscovery.GetDefaultEndpoint(_root.RootPath), null)
        {
            DisposeFlushTimeout = TimeSpan.FromSeconds(2),
        };
        _hosts.Add(host);
        var factory = new MuxTerminalSessionFactory(
            new MuxConnectionHosts(host, _ => null), new RecordingSessionFactory(new FakeTerminalSession()), null);
        MainWindow window = TestMainWindowFactory.Create(AppServices.BuildForDesigner() with
        {
            CommandAssist = TestCommandAssistServices.Instance,
            SessionFactory = factory,
            Settings = new TerminalSettings { SessionPersistence = SessionPersistenceMode.KeepOnClose },
            // Long before any session file these tests write: a restore is never quietened by a reboot (spec R2).
            SessionsCannotPredateUtc = static () => new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        window.MuxCloseChoiceStore = new MuxCloseChoiceStore(_root.RootPath);
        window.ConfirmFirstClose = count =>
        {
            _asked.Add(count);
            return Task.FromResult(new FirstCloseAnswer(answer, false));
        };
        window.ConfirmQuitAndCloseAll = count =>
        {
            _asked.Add(count);
            return Task.FromResult(true);
        };
        window.Show();
        PumpUntil(() => Attached(window).Count > 0, "the first pane attached to the real daemon");
        return window;
    }

    private static List<MuxClientSession> Attached(MainWindow window) =>
        [.. window.AllPanesForTest().Select(p => p.Session).OfType<MuxClientSession>().Where(s => s.IsAttached)];

    private static TerminalPane PaneOf(MainWindow window, Guid id) =>
        window.AllPanesForTest().Single(p => p.Session is MuxClientSession m && m.Id == id);

    private static void AddTab(MainWindow window) =>
        typeof(MainWindow).GetMethod("AddTab", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [null, SshDiagnosticsLevel.None, true, true, false]);

    /// <summary>Selects each restored tab in turn: a background tab's pane attaches only once it is shown.</summary>
    private static void ShowEveryTab(MainWindow window, int tabs)
    {
        TabControl tabControl = window.FindControl<TabControl>("Tabs")!;
        for (int i = 0; i < tabs; i++)
        {
            int count = i + 1;
            PumpUntil(() => tabControl.ItemCount >= tabs, "the restored tabs are built");
            tabControl.SelectedIndex = i;
            PumpUntil(() => Attached(window).Count >= count, $"tab {i}'s pane attached");
        }
    }

    /// <summary>"Attach to session…", choosing <paramref name="id"/>: its pane, once attached.</summary>
    private static TerminalPane AttachInWindow(MainWindow window, Guid id)
    {
        window.PickMuxSession = MuxPickerChoice.Session(id);
        Task attach = window.AttachToMuxSessionAsync();
        PumpUntil(() => attach.IsCompleted, "the attach command finished");
        PumpUntil(() => window.AllPanesForTest().Any(p => p.Session is MuxClientSession { IsAttached: true } m && m.Id == id), "the chosen shell attached");
        return PaneOf(window, id);
    }

    /// <summary>Settings saved with SessionPersistence Off: the factory swaps, the open panes stay connected until the close.</summary>
    private static void TurnPersistenceOff(MainWindow window)
    {
        var settings = (TerminalSettings)typeof(MainWindow).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        settings.SessionPersistence = SessionPersistenceMode.Off;
        typeof(MainWindow).GetMethod("ApplySessionPersistenceSetting", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
    }

    /// <summary>Waits for a prompt, types <c>echo marker</c>, and waits until the command and its output are both on screen.</summary>
    private static void TypeInto(TerminalPane pane, string marker)
    {
        PumpUntil(() => Text(pane).Trim().Length > 0, "the shell drew a prompt");
        PumpFor(TimeSpan.FromMilliseconds(500)); // early input can be eaten by a line editor still starting
        ((MuxClientSession)pane.Session!).SendInput($"echo {marker}\r");
        PumpUntil(() => Count(Text(pane), marker) >= 2, $"the shell echoed {marker}");
    }

    /// <summary>A second client of the test's daemon, as another instance or <c>ntilde mux attach</c> is.</summary>
    private MuxClient Observer()
    {
        MuxClient client = OffTheUiThread(() => _launcher.EnsureConnectedAsync(Ct));
        _owned.Add(client);
        return client;
    }

    /// <summary>A real shell that <paramref name="client"/> spawned and shows.</summary>
    private static (Guid Id, ClientPaneModel Pane) SpawnElsewhere(MuxClient client) => OffTheUiThread(async () =>
    {
        string shell = ShellHelper.GetDefaultShell();
        Guid id = await client.SpawnAsync(new SpawnParams
        {
            Command = shell,
            Cols = 100,
            Rows = 30,
            SkipPowerShellPostLaunchInit = true,
            Title = "elsewhere",
        }, Ct);
        var pane = new ClientPaneModel(client.OpenSession(id, shell));
        await pane.Session.AttachAsync(1000, MuxTestHost.DefaultPresentation, Ct);
        return (id, pane);
    });

    /// <summary>"Pane: Detach" in the other client: the shell runs on with no client and is marked detached by the user.</summary>
    private void Detach(ClientPaneModel pane)
    {
        Guid id = pane.Session.Id;
        pane.Session.Detach(userDetached: true);
        PumpUntil(() => Sessions().Single(s => s.SessionId == id) is { DetachedByUser: true, AttachedClients: 0 }, "the daemon recorded the user detach");
    }

    /// <summary>The daemon's sessions, read on a connection of its own.</summary>
    private IReadOnlyList<SessionSummary> Sessions() => OffTheUiThread(async () =>
    {
        using MuxClient client = await _launcher.EnsureConnectedAsync(Ct);
        return await client.ListSessionsAsync(Ct);
    });

    private int ShellPid(MuxClientSession session)
    {
        int pid = OffTheUiThread(() => session.RefreshSessionInfoAsync(Ct)).Pid
            ?? throw new InvalidOperationException($"Session {session.Id} reports no shell pid.");
        _started.Add(pid);
        return pid;
    }

    private int DaemonPid()
    {
        Assert.True(MuxDiscovery.TryReadDescriptor(DescriptorPath, out MuxEndpointDescriptor? descriptor), $"no daemon descriptor at {DescriptorPath}");
        Assert.NotEqual(Environment.ProcessId, descriptor.Pid);
        _started.Add(descriptor.Pid);
        return descriptor.Pid;
    }

    /// <summary>The parent of <paramref name="pid"/>, from <c>/proc/&lt;pid&gt;/stat</c> (its fourth field, after the command name).</summary>
    private static int LinuxParentOf(int pid)
    {
        string stat = File.ReadAllText($"/proc/{pid}/stat");
        string[] afterName = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
        return int.Parse(afterName[1], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Kill(int pid)
    {
        if (!MuxDaemonSmokeTests.IsAlive(pid)) return;
        try
        {
            using Process process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Gone in between.
        }
    }

    private static string Text(TerminalPane pane) => MuxTestText.VisibleText(pane.Buffer!);

    private static int Count(string text, string marker) => text.Split(marker).Length - 1;

    private static (bool Visible, string? Title, string? Message) Toast(MainWindow window) =>
        (window.FindControl<Border>("RecordingToast")!.IsVisible,
         window.FindControl<TextBlock>("RecordingToastTitle")!.Text,
         window.FindControl<TextBlock>("RecordingToastMessage")!.Text);

    /// <summary>A client call, off the UI thread (its replies arrive on the client's own reader), bounded.</summary>
    private static T OffTheUiThread<T>(Func<Task<T>> call) =>
        Task.Run(call, Ct).WaitAsync(Patient, Ct).GetAwaiter().GetResult();

    private static void PumpUntil(Func<bool> condition, string because)
    {
        var sw = Stopwatch.StartNew();
        Dispatcher.UIThread.RunJobs();
        while (!condition())
        {
            if (sw.Elapsed > Patient) Assert.Fail($"Timed out after {Patient.TotalSeconds:0} s: {because}");
            Thread.Sleep(20);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void PumpFor(TimeSpan duration)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < duration)
        {
            Thread.Sleep(20);
            Dispatcher.UIThread.RunJobs();
        }
    }
}
