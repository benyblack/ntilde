using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Ntilde.Controls;
using Ntilde.Mux;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Platform.Ssh.Models;
using Ntilde.Services.Ssh;
using Ntilde.Shell;
using Ntilde.Shell.Mux;
using Ntilde.Shell.Mux.Remote;
using Ntilde.Tests.Shell.Mux;
using Ntilde.Tests.Shell.Mux.Remote;
using Ntilde.VT.Tests.StateTransfer;

namespace Ntilde.Tests.Controls;

/// <summary>
/// A TerminalPane whose SSH profile persists its remote sessions (Phase 4 spec §7.4): it opens on the
/// profile's remote daemon - a <see cref="FakeRemoteHost"/> running the real proxy and daemon in-process,
/// on a clock the test advances - off the UI thread, rides out a dropped link with a banner, drops what is
/// typed meanwhile, and offers Enter whenever it cannot get its session back on its own.
/// </summary>
public sealed class MuxRemotePaneTests : IDisposable
{
    private const string Host = "nova@fake-host";
    private static readonly TimeSpan Patient = TimeSpan.FromSeconds(30);

    /// <summary>The reconnect loop's first wait at its longest: one second, jittered by 20%.</summary>
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(1.2);

    /// <summary>sshd refusing a batch-mode (automatic) sign-in: the loop needs the user, and stops.</summary>
    private static readonly FakeRemoteScript NeedsPassword =
        new(Stderr: "nova@fake-host: Permission denied (publickey,password).\r\n", ExitCode: FakeRemoteHost.LinkLostExitCode);

    private readonly List<IDisposable> _owned = [];
    private readonly FakeRemoteHost _remote = new();
    private readonly FakeMuxTimerScheduler _clock = new();
    private readonly SshProfile _sshProfile = RemoteMuxConnectorTests.Profile();
    private readonly RecordingSessionFactory _fallback = new(new FakeTerminalSession());
    private readonly MuxConnectionHosts _hosts;
    private readonly MuxTerminalSessionFactory _factory;
    private readonly ConcurrentQueue<int> _factoryThreads = new();
    private readonly ConcurrentQueue<Exception> _offUiFailures = new(); // a test's own assertion that failed off the UI thread
    private readonly List<(string Title, string Message, PersistenceNoticeAction? Action)> _notices = [];
    private readonly List<Window> _windows = [];
    private readonly List<TerminalPane> _panes = [];
    private bool _nativeSshEnabled = true; // the global switch (Settings > SSH), read by each attempt's transport

    /// <summary>The start of the line a pane shows under its Enter banner while native SSH is off (codex4 F).</summary>
    private const string NativeSshOffLine = "[Native SSH is disabled globally.";

    public MuxRemotePaneTests()
    {
        MuxConnectionHost local = Own(new MuxConnectionHost(_ => throw new InvalidOperationException("a remote pane never uses the local daemon"), "local", null));
        _hosts = Own(new MuxConnectionHosts(local, id => RemoteMuxHostFactory.Create(id, Resolve, NativeSwitchedRemote, log: null, userPrompts: null, scheduler: _clock)));
        _factory = new MuxTerminalSessionFactory(_hosts, _fallback, Resolve, log: null);
    }

    /// <summary>
    /// The fake remote, behind the app's native SSH switch (<see cref="RemoteMuxHostFactory.CreateTransport"/>): a native
    /// profile's attempt is refused while <see cref="_nativeSshEnabled"/> is off.
    /// </summary>
    private FakeRemoteHost NativeSwitchedRemote(SshProfile profile, RemoteMuxTransportRequest _)
    {
        RemoteMuxHostFactory.ThrowIfNativeSshDisabled(profile, () => Volatile.Read(ref _nativeSshEnabled));
        return _remote;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Endpoint => MuxEndpointId.ForSsh(_sshProfile.Id).ToString();

    private MuxConnectionHost RemoteHost =>
        _hosts.TryGet(MuxEndpointId.ForSsh(_sshProfile.Id)) ?? throw new InvalidOperationException("no remote host yet");

    public void Dispose()
    {
        foreach (Window window in _windows) window.Close();
        Dispatcher.UIThread.RunJobs();
        foreach (TerminalPane pane in _panes) pane.Dispose();
        for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        _remote.Dispose();
    }

    private T Own<T>(T disposable) where T : IDisposable
    {
        _owned.Add(disposable);
        return disposable;
    }

    private SshProfile? Resolve(Guid id) => id == _sshProfile.Id ? _sshProfile : null;

    /// <summary>
    /// The factory call, on a pool thread whose id is recorded. A test's assertion that fails in there is kept, and
    /// <see cref="PumpUntil"/> rethrows it: it would otherwise only fault the call and surface as a timeout.
    /// </summary>
    private Task<PersistentSessionResult> OffUiThread(Func<PersistentSessionResult> create) => Task.Run(() =>
    {
        _factoryThreads.Enqueue(Environment.CurrentManagedThreadId);
        try
        {
            return create();
        }
        catch (Exception ex) when (ex is Xunit.Sdk.IAssertionException)
        {
            _offUiFailures.Enqueue(ex);
            throw;
        }
    });

    /// <summary>
    /// A pane of the persisted SSH profile in a shown window, spawned the way a real launch spawns it (an
    /// unhosted TermView has a 0x0 grid, and InitializeSession returns early on that).
    /// </summary>
    private TerminalPane ShowPane(
        Guid? restore = null,
        Func<Func<PersistentSessionResult>, Task<PersistentSessionResult>>? offUi = null,
        SshBackendKind backend = SshBackendKind.OpenSsh,
        Action<TerminalPane>? configure = null)
    {
        var pane = new TerminalPane(new TerminalProfile
        {
            Id = _sshProfile.Id,
            Name = _sshProfile.Name,
            Type = ConnectionType.SSH,
            SshHost = "fake-host",
            SshUser = "nova",
            SshBackendKind = backend,
        });
        PaneSpawnTestHelpers.DisableShellIntegration(pane);
        pane.SessionFactory = _factory;
        pane.RunOffUiThread = offUi ?? OffUiThread;
        if (restore is Guid id)
        {
            pane.MuxSessionIdToRestore = id;
            pane.MuxEndpoint = Endpoint;
        }

        pane.PersistenceNotice += (_, title, message, action) => _notices.Add((title, message, action));
        configure?.Invoke(pane);
        var window = new Window { Content = pane, Width = 900, Height = 500 };
        _panes.Add(pane);
        _windows.Add(window);
        window.Show();
        return pane;
    }

    private MuxClientSession Attached(TerminalPane pane)
    {
        PumpUntil(() => pane.Session is MuxClientSession { IsAttached: true }, "the remote pane attached");
        return (MuxClientSession)pane.Session!;
    }

    /// <summary>Waits until <paramref name="pane"/> shows <paramref name="id"/> again, through a session other than <paramref name="before"/>.</summary>
    private MuxClientSession Reattached(TerminalPane pane, Guid id, MuxClientSession before)
    {
        PumpUntil(() => pane.Session is MuxClientSession { IsAttached: true } m && !ReferenceEquals(m, before), "the pane reattached");
        var again = (MuxClientSession)pane.Session!;
        Assert.Equal(id, again.Id);
        return again;
    }

    private void PumpUntil(Func<bool> condition, string because, int timeoutMs = 20_000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (_offUiFailures.TryPeek(out Exception? failure)) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            if (sw.ElapsedMilliseconds > timeoutMs) Assert.Fail($"Timed out waiting until {because}.");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
    }

    private HeadlessTerminalSession OnDaemon(Guid id) =>
        _remote.Server.TryGetSession(id, out HeadlessTerminalSession? session) ? session : throw new InvalidOperationException($"No session {id} on the remote daemon.");

    private ScriptedTerminalSession Shell(Guid id) => (ScriptedTerminalSession)OnDaemon(id).Inner;

    /// <summary>Quiesces one remote session as MuxTestHost.SettleAsync does: ping, flush the daemon's parser, ping.</summary>
    private void Settle(Guid id)
    {
        MuxClient client = RemoteHost.CurrentClient ?? throw new InvalidOperationException("the remote host is not connected");
        HeadlessTerminalSession onDaemon = OnDaemon(id);
        Task.Run(async () =>
        {
            await client.PingAsync(Ct);
            await onDaemon.FlushAsync();
            await client.PingAsync(Ct);
        }, Ct).GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
    }

    private void AssertPaneEqualsDaemon(TerminalPane pane, Guid id, string because)
    {
        Settle(id);
        HeadlessTerminalSession onDaemon = OnDaemon(id);
        TerminalStateAssert.AssertEquivalent($"[{because}]", onDaemon.Buffer, onDaemon.Parser, pane.Buffer!, pane.Parser!);
    }

    private static string Text(TerminalPane pane) => MuxTestText.VisibleText(pane.Buffer!);

    private static int Occurrences(string text, string what)
    {
        int count = 0;
        for (int at = text.IndexOf(what, StringComparison.Ordinal); at >= 0; at = text.IndexOf(what, at + what.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    /// <summary>A key through Avalonia's real input pipeline: the focused TerminalView first, the pane second.</summary>
    private static void Press(TerminalPane pane, Key key, PhysicalKey physical, string? symbol)
    {
        pane.TermView.Focus();
        Assert.True(pane.IsKeyboardFocusWithin);
        TopLevel.GetTopLevel(pane)!.KeyPress(key, RawInputModifiers.None, physical, symbol);
        Dispatcher.UIThread.RunJobs();
    }

    private static void PressEnter(TerminalPane pane) => Press(pane, Key.Enter, PhysicalKey.Enter, "\r");

    private void ShowsBanner(TerminalPane pane, string banner) =>
        PumpUntil(() => Text(pane).Contains(banner, StringComparison.Ordinal), $"the pane shows {banner}");

    /// <summary>Raises one of the host's events as the host does, on a pool thread: the pane must decide from its own state.</summary>
    private static void Raise(MuxConnectionHost host, string name, params object?[] args)
    {
        var handlers = (Delegate?)typeof(MuxConnectionHost).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(host);
        Assert.NotNull(handlers); // the pane follows the host's events
        Task.Run(() => handlers.DynamicInvoke(args), Ct).GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
    }

    private static bool IsDisposed(MuxClientSession session) =>
        (int)typeof(MuxClientSession).GetField("_disposed", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(session)! != 0;

    [AvaloniaFact]
    public void Persisted_ssh_pane_connects_off_the_ui_thread_and_attaches()
    {
        int uiThread = Environment.CurrentManagedThreadId;
        using var gate = new ManualResetEventSlim();
        TerminalPane pane = ShowPane(
            offUi: create => OffUiThread(() =>
            {
                gate.Wait(Patient);
                return create();
            }),
            backend: SshBackendKind.Native);

        ShowsBanner(pane, TerminalPane.RemoteConnectingBanner(Host));
        Assert.Null(pane.Session);                  // nothing to wire until the result is back
        Assert.Equal(0, _remote.StartCount);        // and the UI thread did not connect
        Assert.Equal(Endpoint, pane.MuxEndpoint);
        Assert.True(pane.IsPersistentRemoteTab);

        gate.Set();
        MuxClientSession session = Attached(pane);

        Assert.NotEqual(uiThread, Assert.Single(_factoryThreads));
        Assert.Equal(1, _remote.StartCount);
        Assert.Contains(session.Id, _remote.Server.GetSessionIds());
        Assert.Equal(Endpoint, pane.MuxEndpoint);
        Assert.Same(session, pane.TermView.SessionForTest);
        Assert.Null(_fallback.LastRequest);
        // Spec §8.4: a persisted remote pane is not a native SSH session the sidebar or forwards could use.
        Assert.False(ActiveSshSessionRegistry.Instance.TryGet(session.Id, out _));

        Shell(session.Id).Emit("hello from the remote\r\n");
        AssertPaneEqualsDaemon(pane, session.Id, "after remote output");
        Assert.Contains("hello from the remote", Text(pane));
        Assert.DoesNotContain(TerminalPane.RemoteConnectingBanner(Host), Text(pane)); // the snapshot replaced it
    }

    [AvaloniaFact]
    public void Link_drop_shows_reconnecting_then_reattaches_the_same_session()
    {
        TerminalPane pane = ShowPane();
        MuxClientSession first = Attached(pane);
        Shell(first.Id).Emit("before the drop\r\n");
        Settle(first.Id);

        _remote.CutLink();

        ShowsBanner(pane, TerminalPane.RemoteReconnectingBanner(Host));
        Assert.True(RemoteHost.IsReconnecting);
        Assert.Contains("before the drop", Text(pane));    // the last screen stays
        Assert.Null(pane.TermView.SessionForTest);         // keys reach the pane, not a dead connection
        Assert.Equal(1, _remote.StartCount);

        _clock.Advance(FirstRetry);                        // the loop's first attempt connects: Reconnected

        MuxClientSession again = Reattached(pane, first.Id, first);
        Assert.Equal(MuxAttachMode.Shared, again.AttachMode); // its own ghost may still hold it (spec §7.4)
        PumpUntil(() => !Text(pane).Contains(TerminalPane.RemoteReconnectingBanner(Host), StringComparison.Ordinal) && Text(pane).Contains("before the drop", StringComparison.Ordinal),
            "the reattach snapshot replaced the banners");
        AssertPaneEqualsDaemon(pane, first.Id, "after the reattach");
        Assert.Equal(Endpoint, pane.MuxEndpoint);
        Assert.Equal(2, _remote.StartCount);
    }

    [AvaloniaFact]
    public void Input_while_reconnecting_is_dropped_with_one_hint()
    {
        TerminalPane pane = ShowPane();
        MuxClientSession first = Attached(pane);
        _remote.CutLink();
        ShowsBanner(pane, TerminalPane.RemoteReconnectingBanner(Host));

        Press(pane, Key.A, PhysicalKey.A, "a");
        Press(pane, Key.B, PhysicalKey.B, "b");

        Assert.Equal(1, Occurrences(Text(pane), TerminalPane.RemoteInputDroppedHint));

        _clock.Advance(FirstRetry);
        Reattached(pane, first.Id, first);
        Settle(first.Id);
        Assert.Empty(Shell(first.Id).SentInput); // dropped, never queued for after the reattach
    }

    [AvaloniaFact]
    public void Enter_while_reconnecting_retries_at_once_and_is_not_sent()
    {
        TerminalPane pane = ShowPane();
        MuxClientSession first = Attached(pane);
        _remote.CutLink();
        ShowsBanner(pane, TerminalPane.RemoteReconnectingBanner(Host));

        PressEnter(pane); // no clock advance: the loop would still be waiting

        Reattached(pane, first.Id, first);
        Assert.Equal(2, _remote.StartCount);
        Settle(first.Id);
        Assert.Empty(Shell(first.Id).SentInput);
        Assert.DoesNotContain(TerminalPane.RemoteInputDroppedHint, Text(pane));
    }

    [AvaloniaFact]
    public void Abandoned_reconnect_shows_the_enter_banner_and_enter_retries()
    {
        TerminalPane pane = ShowPane();
        MuxClientSession first = Attached(pane);
        Shell(first.Id).Emit("kept running\r\n");
        Settle(first.Id);
        _remote.Script = NeedsPassword;
        _remote.CutLink();
        ShowsBanner(pane, TerminalPane.RemoteReconnectingBanner(Host));

        _clock.Advance(FirstRetry); // the automatic attempt cannot sign in: the loop gives up

        ShowsBanner(pane, TerminalPane.RemoteAbandonedBanner(Host));
        Assert.False(RemoteHost.IsReconnecting);
        Assert.Equal(first.Id.ToString("D"), SessionManager.BuildPaneTree(pane)!.MuxSessionId); // still this pane's
        int generation = pane.RemoteConnectGenerationForTest;

        _remote.Script = null; // the user's own attempt signs in
        PressEnter(pane);

        Reattached(pane, first.Id, first);
        PumpUntil(() => Text(pane).Contains("kept running", StringComparison.Ordinal), "the snapshot shows the shell's screen");
        // The Reconnected that followed this pane's own Enter is a duplicate: nothing more is started.
        Assert.True(RemoteHost.EventsForTest.Wait(Patient, Ct));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(generation + 1, pane.RemoteConnectGenerationForTest);
        Assert.Equal(3, _remote.StartCount);
    }

    /// <summary>
    /// The smoke test's complaint: an automatic attempt refused by sshd stopped at the Enter banner, and the pane wrote
    /// ssh's own "Permission denied" under it - confusing under a banner that asks for Enter. The line says what Enter is
    /// for instead; ssh's words go to the log only.
    /// </summary>
    [AvaloniaFact]
    public void A_refused_automatic_sign_in_says_enter_is_needed_and_never_shows_ssh_s_own_words()
    {
        TerminalPane pane = ShowPane();
        Attached(pane);
        _remote.Script = NeedsPassword;
        _remote.CutLink();
        ShowsBanner(pane, TerminalPane.RemoteReconnectingBanner(Host));

        _clock.Advance(FirstRetry); // the automatic attempt cannot sign in: the loop gives up

        ShowsBanner(pane, TerminalPane.RemoteAbandonedBanner(Host));
        ShowsBanner(pane, "[Automatic reconnect can't sign in without you — press Enter]");
        Assert.DoesNotContain("Permission denied", Text(pane));
        Assert.DoesNotContain("publickey", Text(pane));
    }

    /// <summary>
    /// The line under the Enter banner, by why the failure needs the user - never ssh's or rusty_ssh's reason. Only the
    /// native-SSH-switched-off refusal (codex4 F) keeps its own text: Enter alone cannot fix it. Other failures get none.
    /// </summary>
    [AvaloniaFact]
    public void The_needs_you_line_says_why_in_ntildes_words()
    {
        const string sshSaid = "nova@fake-host: Permission denied (publickey,password).";
        string nativeOff = Ntilde.Platform.Ssh.Sessions.SshSessionFactory.NativeSshDisabledMessage;

        Assert.Equal(
            "[The saved password was refused — press Enter to sign in]",
            TerminalPane.RemoteNeedsUserLine(new RemoteMuxFailure(RemoteFailureKind.NeedsUser, sshSaid, RemoteNeedsUserCause.SavedPasswordRefused)));
        Assert.Equal(
            "[Automatic reconnect can't sign in without you — press Enter]",
            TerminalPane.RemoteNeedsUserLine(new RemoteMuxFailure(RemoteFailureKind.NeedsUser, sshSaid)));
        Assert.Equal(
            "[Automatic reconnect can't sign in without you — press Enter]",
            TerminalPane.RemoteNeedsUserLine(new RemoteMuxFailure(RemoteFailureKind.NeedsUser, "signing in to nova@fake-host needs a key passphrase, which an automatic reconnect does not ask for")));
        Assert.Equal(
            $"[{nativeOff}]",
            TerminalPane.RemoteNeedsUserLine(new RemoteMuxFailure(RemoteFailureKind.NeedsUser, nativeOff, RemoteNeedsUserCause.NativeSshDisabled)));
        Assert.Null(TerminalPane.RemoteNeedsUserLine(new RemoteMuxFailure(RemoteFailureKind.SshFailed, sshSaid)));
        Assert.Null(TerminalPane.RemoteNeedsUserLine(null));
    }

    [AvaloniaFact]
    public void One_enter_after_a_give_up_brings_back_every_pane_of_the_host()
    {
        TerminalPane a = ShowPane();
        MuxClientSession firstA = Attached(a);
        TerminalPane b = ShowPane();
        MuxClientSession firstB = Attached(b);
        _remote.Script = NeedsPassword;
        _remote.CutLink();
        ShowsBanner(a, TerminalPane.RemoteReconnectingBanner(Host));
        ShowsBanner(b, TerminalPane.RemoteReconnectingBanner(Host));
        _clock.Advance(FirstRetry);
        ShowsBanner(a, TerminalPane.RemoteAbandonedBanner(Host));
        ShowsBanner(b, TerminalPane.RemoteAbandonedBanner(Host));

        _remote.Script = null;
        PressEnter(a);

        Reattached(a, firstA.Id, firstA);
        Reattached(b, firstB.Id, firstB); // Reconnected reached it: no Enter of its own
        Assert.Equal(3, _remote.StartCount); // one connection for both panes
    }

    [AvaloniaFact]
    public void Daemon_stopped_then_enter_starts_a_fresh_shell_with_the_previous_lost_notice()
    {
        TerminalPane pane = ShowPane();
        MuxClientSession first = Attached(pane);

        _remote.StopDaemon();

        ShowsBanner(pane, TerminalPane.RemoteDaemonStoppedBanner(Host));
        Assert.False(RemoteHost.IsReconnecting); // no loop: the daemon, not the link, went away
        Assert.Null(pane.TermView.SessionForTest);
        Assert.Empty(_notices);

        PressEnter(pane);

        PumpUntil(() => pane.Session is MuxClientSession { IsAttached: true } m && m.Id != first.Id, "a fresh remote shell attached");
        PumpUntil(() => _notices.Count > 0, "the lost notice is raised");
        Assert.Equal((TerminalPane.MuxPreviousLostNoticeTitle, TerminalPane.MuxPreviousLostBanner), (_notices.Single().Title, _notices.Single().Message));
        Assert.Contains(pane.Session!.Id, _remote.Server.GetSessionIds());
    }

    /// <summary>Review Focus 2: the link came back but the reattach did not. Never no session and no banner.</summary>
    [AvaloniaFact]
    public void Reattach_failure_after_reconnect_shows_the_enter_banner()
    {
        int calls = 0;
        TerminalPane pane = ShowPane(offUi: create => OffUiThread(() =>
        {
            PersistentSessionResult result = create();
            // The second call is the reattach after Reconnected: its session goes before the pane attaches,
            // so the attach itself fails over a connection that stays up.
            if (Interlocked.Increment(ref calls) == 2) _remote.Server.KillAllSessions();
            return result;
        }));
        MuxClientSession first = Attached(pane);
        _remote.CutLink();
        ShowsBanner(pane, TerminalPane.RemoteReconnectingBanner(Host));

        _clock.Advance(FirstRetry);

        ShowsBanner(pane, TerminalPane.RemoteAbandonedBanner(Host));
        Assert.Equal(2, calls);
        Assert.False(RemoteHost.IsReconnecting);
        Assert.NotNull(RemoteHost.CurrentClient); // only the reattach failed
        Assert.Null(pane.TermView.SessionForTest);

        PressEnter(pane); // armed: the session is gone, so a fresh shell and the lost notice

        PumpUntil(() => pane.Session is MuxClientSession { IsAttached: true } m && m.Id != first.Id, "Enter started a fresh shell");
        PumpUntil(() => _notices.Any(n => n.Title == TerminalPane.MuxPreviousLostNoticeTitle), "the lost notice is raised");
    }

    /// <summary>Review Focus 2, the other way out: the link dropped again during the reattach, so the pane is back in the loop.</summary>
    [AvaloniaFact]
    public void Reattach_failure_while_the_host_is_still_reconnecting_stays_in_the_loop()
    {
        int calls = 0;
        TerminalPane pane = ShowPane(offUi: create => OffUiThread(() =>
        {
            if (Interlocked.Increment(ref calls) != 2) return create();
            _remote.CutLink(); // the reattach is overtaken by another drop
            Assert.True(SpinWait.SpinUntil(() => RemoteHost.IsReconnecting, Patient), "the host is reconnecting again");
            return new PersistentSessionResult(null, PersistentSessionOutcome.DaemonUnreachable, Endpoint, "the link dropped again") { HostDisplayName = Host };
        }));
        MuxClientSession first = Attached(pane);
        _remote.CutLink();
        ShowsBanner(pane, TerminalPane.RemoteReconnectingBanner(Host));
        _clock.Advance(FirstRetry);

        PumpUntil(() => Occurrences(Text(pane), TerminalPane.RemoteReconnectingBanner(Host)) == 2, "the pane is back in the loop");
        Assert.DoesNotContain(TerminalPane.RemoteAbandonedBanner(Host), Text(pane));
        Assert.Null(pane.Session);
        Assert.Equal(first.Id.ToString("D"), SessionManager.BuildPaneTree(pane)!.MuxSessionId); // the id is kept
        Assert.Equal(Endpoint, SessionManager.BuildPaneTree(pane)!.MuxEndpoint);

        _clock.Advance(FirstRetry); // the loop reconnects again: this time the reattach goes through

        PumpUntil(() => pane.Session is MuxClientSession { IsAttached: true }, "the pane reattached");
        Assert.Equal(first.Id, pane.Session!.Id);
        Assert.Equal(3, calls);
    }

    [AvaloniaFact]
    public void Stale_connect_result_is_disposed()
    {
        using var firstGate = new ManualResetEventSlim();
        int calls = 0;
        PersistentSessionResult? firstResult = null;
        TerminalPane pane = ShowPane(offUi: create => OffUiThread(() =>
        {
            bool isFirst = Interlocked.Increment(ref calls) == 1;
            if (isFirst) firstGate.Wait(Patient);
            PersistentSessionResult result = create();
            if (isFirst) Volatile.Write(ref firstResult, result);
            return result;
        }));
        PumpUntil(() => Volatile.Read(ref calls) == 1, "the first connect started");
        int generation = pane.RemoteConnectGenerationForTest;

        pane.Reconnect(); // while the first connect is still pending

        Assert.Equal(generation + 1, pane.RemoteConnectGenerationForTest);
        MuxClientSession second = Attached(pane);

        firstGate.Set();
        PumpUntil(() => Volatile.Read(ref firstResult) is not null, "the first connect finished");
        var stale = Assert.IsType<MuxClientSession>(firstResult!.Session);
        Assert.NotEqual(second.Id, stale.Id);
        PumpUntil(() => IsDisposed(stale), "the stale session was disposed");
        // The shell it started has nobody to show it: ended, not left running unseen.
        PumpUntil(() =>
        {
            _remote.Server.ReapExitedSessions(TimeSpan.Zero);
            return !_remote.Server.GetSessionIds().Contains(stale.Id);
        }, "the stale shell was ended");
        Dispatcher.UIThread.RunJobs();
        Assert.Same(second, pane.Session);
        Assert.Contains(second.Id, _remote.Server.GetSessionIds());
    }

    /// <summary>
    /// Codex C1: only a shell started for a stale result is killed. A stale result that reopened an existing shell did
    /// not start it, so its session is let go and the shell keeps running. (A pane closed in a window has its pending
    /// id killed by the window's close; this pane has no window to do that.)
    /// </summary>
    [AvaloniaFact]
    public void A_stale_reattach_result_leaves_its_shell_running()
    {
        TerminalPane earlier = ShowPane();
        Guid id = Attached(earlier).Id;
        earlier.Dispose(); // a plain detach: the shell keeps running, for the pane below to reopen
        var reopened = new TaskCompletionSource<PersistentSessionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deliver = new TaskCompletionSource<PersistentSessionResult>(); // inline continuations: SetResult posts the result
        TerminalPane pane = ShowPane(restore: id, offUi: create =>
        {
            _ = OffUiThread(create).ContinueWith(
                t => _ = t.IsCompletedSuccessfully ? reopened.TrySetResult(t.Result) : reopened.TrySetException(t.Exception!.GetBaseException()),
                TaskScheduler.Default);
            return deliver.Task;
        });
        PumpUntil(() => reopened.Task.IsCompleted, "the reopen finished");
        PersistentSessionResult result = reopened.Task.Result;
        Assert.Equal(PersistentSessionOutcome.Reattached, result.Outcome);
        var stale = Assert.IsType<MuxClientSession>(result.Session);

        pane.Dispose();          // gone before its result is back
        deliver.SetResult(result);
        PumpUntil(() => IsDisposed(stale), "the stale session was let go");
        Settle(id);
        _remote.Server.ReapExitedSessions(TimeSpan.Zero);

        Assert.Contains(id, _remote.Server.GetSessionIds());
        Assert.False(OnDaemon(id).IsExited, "the reopened shell was killed");
    }

    /// <summary>Task 19 ruling: a new remote tab whose SSH connect failed gets no plain SSH stand-in and no id, but a retry.</summary>
    [AvaloniaFact]
    public void A_new_remote_tab_whose_ssh_connect_fails_offers_a_retry()
    {
        _remote.Script = FakeRemoteScript.ConnectionRefused;
        TerminalPane pane = ShowPane();

        ShowsBanner(pane, TerminalPane.RemoteUnreachableBanner(Host));
        Assert.Null(pane.Session);
        Assert.Null(pane.MuxSessionIdToRestore);
        Assert.Null(SessionManager.BuildPaneTree(pane)!.MuxSessionId);
        Assert.Null(_fallback.LastRequest);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(_notices); // SSH itself failed: the banner says it, no "will not persist" toast

        _remote.Script = null;
        PressEnter(pane);

        MuxClientSession session = Attached(pane);
        Assert.Contains(session.Id, _remote.Server.GetSessionIds());
        Assert.Equal(2, _remote.StartCount);
    }

    /// <summary>
    /// Codex4 F: a new remote tab on a native profile while native SSH is off (Settings &gt; SSH) starts nothing - no
    /// native connection, and no plain SSH stand-in, which would be refused as well - and says why under the retry
    /// banner. Once the switch is on again, Enter connects.
    /// </summary>
    [AvaloniaFact]
    public void A_new_native_remote_tab_while_native_ssh_is_off_starts_nothing_and_says_why()
    {
        _sshProfile.BackendKind = SshBackendKind.Native;
        Volatile.Write(ref _nativeSshEnabled, false);

        TerminalPane pane = ShowPane(backend: SshBackendKind.Native);

        ShowsBanner(pane, TerminalPane.RemoteUnreachableBanner(Host));
        ShowsBanner(pane, NativeSshOffLine);
        Assert.Null(pane.Session);
        Assert.Null(pane.MuxSessionIdToRestore);
        Assert.Equal(0, _remote.StartCount);
        Assert.Null(_fallback.LastRequest);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(_notices); // the banner says it: no "will not persist" toast

        Volatile.Write(ref _nativeSshEnabled, true);
        PressEnter(pane);

        MuxClientSession session = Attached(pane);
        Assert.Contains(session.Id, _remote.Server.GetSessionIds());
        Assert.Equal(1, _remote.StartCount);
    }

    /// <summary>
    /// Codex4 F: native SSH turned off while a native remote pane is connected. Its link drops, the loop's first attempt
    /// is refused, and the loop stops at once: the Enter banner, with why under it. Enter while the switch is still off is
    /// refused the same way, and says so again; once it is on, Enter takes the same shell back.
    /// </summary>
    [AvaloniaFact]
    public void Native_ssh_turned_off_ends_a_native_panes_reconnect_with_why_and_enter_reattaches_once_it_is_on()
    {
        _sshProfile.BackendKind = SshBackendKind.Native;
        TerminalPane pane = ShowPane(backend: SshBackendKind.Native);
        MuxClientSession first = Attached(pane);
        Volatile.Write(ref _nativeSshEnabled, false);
        _remote.CutLink();
        ShowsBanner(pane, TerminalPane.RemoteReconnectingBanner(Host));

        _clock.Advance(FirstRetry); // the loop's attempt is refused: it stops

        ShowsBanner(pane, TerminalPane.RemoteAbandonedBanner(Host));
        ShowsBanner(pane, NativeSshOffLine);
        Assert.False(RemoteHost.IsReconnecting);
        Assert.Equal(1, _remote.StartCount);

        PressEnter(pane); // still off

        PumpUntil(() => Occurrences(Text(pane), NativeSshOffLine) == 2, "the retry was refused, and the pane said why again");
        Assert.Equal(1, _remote.StartCount);

        Volatile.Write(ref _nativeSshEnabled, true);
        PressEnter(pane);

        Reattached(pane, first.Id, first);
        Assert.Equal(2, _remote.StartCount);
    }

    [AvaloniaFact]
    public void A_restore_whose_remote_runs_another_ntilde_mux_version_keeps_its_id_and_offers_the_update()
    {
        FakeRemoteHost otherVersion = Own(new FakeRemoteHost(serverOptions: new MuxServerOptions { ForceConPtyFiltering = false, MinProtocolVersion = 3, MaxProtocolVersion = 4 }));
        Guid id = Guid.NewGuid();
        var offered = new List<RemoteFailureKind?>();
        MuxConnectionHosts hosts = Own(new MuxConnectionHosts(
            Own(new MuxConnectionHost(_ => throw new InvalidOperationException("a remote pane never uses the local daemon"), "local", null)),
            endpoint => RemoteMuxHostFactory.Create(endpoint, Resolve, (_, _) => otherVersion, log: null, userPrompts: null, scheduler: _clock)));
        TerminalPane pane = ShowPane(restore: id, configure: p =>
        {
            p.SessionFactory = new MuxTerminalSessionFactory(hosts, _fallback, Resolve, log: null);
            p.RemoteNoticeAction = (failure, profileId, host) =>
            {
                Assert.Equal(_sshProfile.Id, profileId);
                Assert.Equal(Host, host);   // final review I1: the host the notice's line names
                offered.Add(failure?.Kind);
                return new PersistenceNoticeAction(TerminalPane.RemoteMuxUpdateActionLabel(host), () => { });
            };
        });

        ShowsBanner(pane, TerminalPane.RemoteUnreachableBanner(Host));
        PumpUntil(() => _notices.Count > 0, "the notice is raised");

        Assert.Null(pane.Session);
        Assert.Equal(id, pane.MuxSessionIdToRestore);
        Assert.Equal(Endpoint, pane.MuxEndpoint);
        Assert.Equal(new RemoteFailureKind?[] { RemoteFailureKind.VersionMismatch }, offered);
        (string title, string message, PersistenceNoticeAction? action) = _notices.Single();
        Assert.Equal(TerminalPane.RemoteMuxUnavailableNoticeTitle, title);
        Assert.StartsWith($"[{Host}: ", message, StringComparison.Ordinal);
        Assert.Equal(TerminalPane.RemoteMuxUpdateActionLabel(Host), action?.Label);
    }

    [AvaloniaFact]
    public void A_remote_tab_without_ntilde_mux_falls_back_to_plain_ssh_with_the_install_notice()
    {
        _remote.Script = FakeRemoteScript.NotInstalledDash;
        var offered = new List<(RemoteMuxFailure? Failure, Guid ProfileId)>();
        TerminalPane pane = ShowPane(configure: p => p.RemoteNoticeAction = (failure, profileId, host) =>
        {
            Assert.Equal(Host, host);   // final review I1: the host the notice's line names
            offered.Add((failure, profileId));
            return new PersistenceNoticeAction(TerminalPane.RemoteMuxInstallActionLabel(host), () => { });
        });

        PumpUntil(() => pane.Session is FakeTerminalSession, "plain SSH stands in");
        PumpUntil(() => _notices.Count > 0, "the notice is raised");

        (RemoteMuxFailure? failure, Guid profile) = Assert.Single(offered);
        Assert.Equal(RemoteFailureKind.NotInstalled, failure!.Kind);
        Assert.Equal(_sshProfile.Id, profile);
        (string title, string message, PersistenceNoticeAction? action) = Assert.Single(_notices);
        Assert.Equal(TerminalPane.RemoteMuxUnavailableNoticeTitle, title);
        Assert.Equal(TerminalPane.RemoteMuxUnavailableMessage(Host, failure.Reason), message);
        Assert.Equal(TerminalPane.RemoteMuxInstallActionLabel(Host), action?.Label);
        Assert.NotNull(_fallback.LastRequest);
        Assert.False(pane.IsPersistentRemoteTab); // a plain SSH session: the sidebar works on it
    }

    /// <summary>
    /// Spec §7.6 and the Task 17 note: a pane restoring an <c>ssh:</c> id whose profile no longer persists
    /// opens plain SSH and leaves the id pending - on its own endpoint, never rewritten as <c>local</c>.
    /// </summary>
    [AvaloniaFact]
    public void A_plain_ssh_pane_keeps_a_pending_remote_id_on_its_own_endpoint()
    {
        _sshProfile.MuxOptions.PersistRemoteSessions = false;
        Guid id = Guid.NewGuid();

        TerminalPane pane = ShowPane(restore: id);

        PumpUntil(() => pane.Session is FakeTerminalSession, "plain SSH started");
        Assert.Null(_fallback.LastRequest!.ExistingMuxSessionId);
        Assert.Equal(id, pane.MuxSessionIdToRestore);
        Ntilde.Pty.PaneNode node = SessionManager.BuildPaneTree(pane)!;
        Assert.Equal((id.ToString("D"), Endpoint), (node.MuxSessionId, node.MuxEndpoint));
        Assert.Equal(0, _remote.StartCount);
    }

    /// <summary>
    /// Task 20 notes: the host's events name no client. A pane whose own session is connected ignores a loss,
    /// a stop, a give-up and a reconnect: they are about a client it no longer uses.
    /// </summary>
    [AvaloniaFact]
    public void Host_events_leave_a_connected_pane_alone()
    {
        TerminalPane pane = ShowPane();
        MuxClientSession session = Attached(pane);
        int generation = pane.RemoteConnectGenerationForTest;
        MuxConnectionHost host = RemoteHost;

        Raise(host, nameof(MuxConnectionHost.ConnectionLost), "disconnected");
        Raise(host, nameof(MuxConnectionHost.DaemonStopped));
        Raise(host, nameof(MuxConnectionHost.ReconnectAbandoned));
        Raise(host, nameof(MuxConnectionHost.Reconnected), host.CurrentClient);

        Assert.Same(session, pane.Session);
        Assert.Same(session, pane.TermView.SessionForTest);
        Assert.Equal(generation, pane.RemoteConnectGenerationForTest);
        string text = Text(pane);
        Assert.DoesNotContain(TerminalPane.RemoteReconnectingBanner(Host), text);
        Assert.DoesNotContain(TerminalPane.RemoteDaemonStoppedBanner(Host), text);
        Assert.DoesNotContain(TerminalPane.RemoteAbandonedBanner(Host), text);
    }

    [AvaloniaFact]
    public void A_disposed_pane_stops_following_its_host()
    {
        TerminalPane pane = ShowPane();
        Attached(pane);
        MuxConnectionHost host = RemoteHost;
        var field = typeof(MuxConnectionHost).GetField(nameof(MuxConnectionHost.ConnectionLost), BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.NotNull(field.GetValue(host));

        _windows.Single().Close();
        pane.Dispose();

        Assert.Null(field.GetValue(host));
    }

    /// <summary>
    /// Task 21 review: a pane at the daemon-stopped banner waits for its own Enter. A later link of the host (another
    /// pane's Enter brought it up) dropping and coming back is not its loss, and must not start a shell it did not ask for.
    /// </summary>
    [AvaloniaFact]
    public void A_pane_at_the_daemon_stopped_banner_waits_for_its_own_enter()
    {
        TerminalPane waiting = ShowPane();
        Attached(waiting);
        TerminalPane other = ShowPane();
        MuxClientSession otherFirst = Attached(other);
        _remote.StopDaemon();
        ShowsBanner(waiting, TerminalPane.RemoteDaemonStoppedBanner(Host));
        ShowsBanner(other, TerminalPane.RemoteDaemonStoppedBanner(Host));
        PressEnter(other); // a fresh shell on a new daemon
        PumpUntil(() => other.Session is MuxClientSession { IsAttached: true } m && m.Id != otherFirst.Id, "the other pane started a fresh shell");
        MuxClientSession otherFresh = (MuxClientSession)other.Session!;
        int generation = waiting.RemoteConnectGenerationForTest;
        string before = Text(waiting);

        _remote.CutLink(); // the new link drops ...
        ShowsBanner(other, TerminalPane.RemoteReconnectingBanner(Host));
        _clock.Advance(FirstRetry); // ... and comes back
        Reattached(other, otherFresh.Id, otherFresh);
        Assert.True(RemoteHost.EventsForTest.Wait(Patient, Ct));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(generation, waiting.RemoteConnectGenerationForTest); // no reattach, no fresh shell
        Assert.Equal(before, Text(waiting));                               // and no reconnecting banner
        Assert.Null(waiting.TermView.SessionForTest);
    }

    /// <summary>
    /// Task 21 review: the profile stopped persisting between the route and the factory call. Plain SSH stands in,
    /// and - as for any plain SSH pane - the id stays pending on its own endpoint.
    /// </summary>
    [AvaloniaFact]
    public void A_not_persistent_result_keeps_the_restore_id_on_its_endpoint()
    {
        Guid id = Guid.NewGuid();
        TerminalPane pane = ShowPane(restore: id, offUi: create => OffUiThread(() =>
        {
            _sshProfile.MuxOptions.PersistRemoteSessions = false; // turned off while the route was being decided
            return create();
        }));

        PumpUntil(() => pane.Session is FakeTerminalSession, "plain SSH stood in");
        Ntilde.Pty.PaneNode node = SessionManager.BuildPaneTree(pane)!;
        Assert.Equal((id.ToString("D"), Endpoint), (node.MuxSessionId, node.MuxEndpoint));
        Assert.False(pane.IsPersistentRemoteTab);
    }

    /// <summary>Task 21 review: a dropped remote session respawned as plain SSH (persistence switched off) stays saved.</summary>
    [AvaloniaFact]
    public void A_dropped_remote_session_respawned_as_plain_ssh_stays_saved()
    {
        TerminalPane pane = ShowPane();
        MuxClientSession first = Attached(pane);
        _remote.CutLink();
        ShowsBanner(pane, TerminalPane.RemoteReconnectingBanner(Host));

        pane.SessionFactory = _fallback; // what turning persistence off does to every pane
        pane.Reconnect();

        Assert.IsType<FakeTerminalSession>(pane.Session);
        Ntilde.Pty.PaneNode node = SessionManager.BuildPaneTree(pane)!;
        Assert.Equal((first.Id.ToString("D"), Endpoint), (node.MuxSessionId, node.MuxEndpoint));
    }

    /// <summary>Task 21 review: a plain SSH spawn is not routed again inside the factory, where a flipped flag would connect on the UI thread.</summary>
    [AvaloniaFact]
    public void A_plain_ssh_spawn_goes_straight_to_the_fallback()
    {
        _sshProfile.MuxOptions.PersistRemoteSessions = false;
        var flipping = new FlipOnFirstLookup(_sshProfile);
        TerminalPane pane = ShowPane(configure: p => p.SessionFactory = new MuxTerminalSessionFactory(_hosts, _fallback, flipping.Resolve, log: null));

        PumpUntil(() => pane.Session is FakeTerminalSession, "plain SSH started");
        Assert.Equal(0, _remote.StartCount); // the flag came on after the route was decided: still plain SSH, no remote connect
        Assert.True(flipping.Flipped);
    }

    /// <summary>The first lookup sees the profile without the flag; the flag is switched on right after it.</summary>
    private sealed class FlipOnFirstLookup(SshProfile profile)
    {
        private int _lookups;

        public bool Flipped => Volatile.Read(ref _lookups) > 0;

        public SshProfile? Resolve(Guid id)
        {
            if (id != profile.Id) return null;
            SshProfile seen = new() { Id = profile.Id, Name = profile.Name, Host = profile.Host, User = profile.User, MuxOptions = new SshMuxOptions { PersistRemoteSessions = profile.MuxOptions.PersistRemoteSessions } };
            if (Interlocked.Increment(ref _lookups) == 1) profile.MuxOptions.PersistRemoteSessions = true;
            return seen;
        }
    }
}
