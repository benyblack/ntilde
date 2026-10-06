using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.Controls;
using Ntilde.Mux;
using Ntilde.Mux.Tests.Support;
using Ntilde.Pty;
using Ntilde.Shell;
using Ntilde.Shell.Mux;
using Ntilde.Tests.Shell.Mux;

namespace Ntilde.Tests.Controls;

/// <summary>Phase 3 spec §7.1, §7.2: restore is exclusive by protocol; a deliberate share joins.</summary>
public sealed class MuxPaneRestoreTests : IDisposable
{
    private MuxTestHost _mux = null!;
    private MuxConnectionHost _host = null!;
    private MuxTerminalSessionFactory _factory = null!;
    private TerminalPane? _pane;
    private Avalonia.Controls.Window? _window;

    public MuxPaneRestoreTests() => StartDaemon(null);

    /// <summary>Replaces the daemon (before anything used it): a test pinning the v1 path passes v1 options.</summary>
    private void StartDaemon(MuxServerOptions? options)
    {
        _host?.Dispose();
        _mux?.Dispose();
        _mux = new MuxTestHost(options);
        MuxTestHost mux = _mux;
        _host = new MuxConnectionHost(ct => MuxClient.ConnectAsync(mux.Listener.Connect(), null, ct), "test", null);
        _factory = new MuxTerminalSessionFactory(_host, new RecordingSessionFactory(new FakeTerminalSession()), null);
    }

    public void Dispose()
    {
        if (_window is not null)
        {
            _window.Close();
            Dispatcher.UIThread.RunJobs();
        }

        _pane?.Dispose();
        _host.Dispose();
        _mux.Dispose();
    }

    private static void PumpUntil(Func<bool> condition, string because, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) Assert.Fail($"Timed out waiting until {because}.");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
    }

    private ClientPaneModel AttachOtherClient() => Task.Run(async () =>
    {
        MuxClient c = await _mux.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(c);
        return await MuxTestHost.AttachPaneAsync(c, id);
    }).GetAwaiter().GetResult();

    /// <summary>Hosts a pane that reopens <paramref name="id"/>: an unhosted TermView has a 0x0 grid and would not spawn.</summary>
    private void ShowRestoringPane(Guid id, bool shared, List<(string Title, string Message)> notices, bool adoptedOrphan = false)
    {
        // "scripted": the same program as the daemon session (MuxTestHost.SpawnAsync), so Task 5's
        // command check lets the restore reach the IfUnattached attach instead of spawning fresh.
        _pane = new TerminalPane("scripted");
        PaneSpawnTestHelpers.DisableShellIntegration(_pane);
        _pane.SessionFactory = _factory;
        _pane.MuxSessionIdToRestore = id;
        _pane.MuxAttachSharedToRestore = shared;
        _pane.MuxAdoptedOrphan = adoptedOrphan;
        _pane.PersistenceNotice += (_, title, message, _) => notices.Add((title, message));
        _window = new Avalonia.Controls.Window { Content = _pane, Width = 900, Height = 500 };
        _window.Show();
    }

    [AvaloniaFact]
    public void A_restore_that_loses_the_attach_race_starts_a_fresh_shell_and_says_so()
    {
        ClientPaneModel other = AttachOtherClient();
        Guid theirs = other.Session.Id;
        var notices = new List<(string Title, string Message)>();

        ShowRestoringPane(theirs, shared: false, notices);

        PumpUntil(() => _pane!.Session is MuxClientSession { IsAttached: true } m && m.Id != theirs, "the pane attached a fresh shell");
        PumpUntil(() => notices.Count > 0, "the notice was raised");
        Assert.Equal((TerminalPane.MuxAttachedElsewhereNoticeTitle, TerminalPane.MuxAttachedElsewhereBanner), Assert.Single(notices));
        Assert.True(other.Session.IsAttached);
        Assert.Equal(1, _mux.Mux(theirs).AttachedClients);
    }

    /// <summary>
    /// Final review: an adopted crash orphan that another instance claimed first starts no shell and
    /// raises no notice; it asks its window to close it, and the other client keeps the shell.
    /// </summary>
    [AvaloniaFact]
    public void An_adopted_orphan_that_loses_the_attach_race_starts_no_shell_and_asks_to_close()
    {
        ClientPaneModel other = AttachOtherClient();
        Guid theirs = other.Session.Id;
        var notices = new List<(string Title, string Message)>();
        int lost = 0;

        ShowRestoringPane(theirs, shared: false, notices, adoptedOrphan: true);
        _pane!.MuxAdoptionLost += _ => lost++;

        PumpUntil(() => lost > 0, "the pane gave the orphan up");
        Assert.Null(_pane.Session);
        Assert.False(_pane.MuxAdoptedOrphan);
        PumpUntil(() => MuxTestText.VisibleText(_pane.Buffer!).Contains(TerminalPane.MuxAdoptionLostBanner, StringComparison.Ordinal), "the fallback banner is written");
        Assert.Equal(1, lost);
        Assert.Empty(notices);
        Assert.Equal(theirs, Assert.Single(_mux.Server.GetSessionIds())); // no new shell
        Assert.True(other.Session.IsAttached);
        Assert.Equal(1, _mux.Mux(theirs).AttachedClients);
    }

    /// <summary>Against a v1 daemon the factory's own check reports AttachedElsewhere; the pane says so after the fresh shell attached.</summary>
    [AvaloniaFact]
    public void On_v1_a_restore_of_a_session_shown_elsewhere_starts_a_fresh_shell_and_says_so()
    {
        StartDaemon(new MuxServerOptions { MaxProtocolVersion = 1, ForceConPtyFiltering = false });
        ClientPaneModel other = AttachOtherClient();
        Guid theirs = other.Session.Id;
        var notices = new List<(string Title, string Message)>();

        ShowRestoringPane(theirs, shared: false, notices);

        PumpUntil(() => _pane!.Session is MuxClientSession { IsAttached: true } m && m.Id != theirs, "the pane attached a fresh shell");
        PumpUntil(() => notices.Count > 0, "the notice was raised");
        Assert.Equal((TerminalPane.MuxAttachedElsewhereNoticeTitle, TerminalPane.MuxAttachedElsewhereBanner), Assert.Single(notices));
        Assert.True(other.Session.IsAttached);
        Assert.Equal(1, _mux.Mux(theirs).AttachedClients);
    }

    /// <summary>Phase 4 carry-over 3: a shared tab is saved as shared, so the next launch rejoins it instead of losing the IfUnattached race.</summary>
    [AvaloniaFact]
    public void Shared_panes_save_and_restore_shared()
    {
        ClientPaneModel other = AttachOtherClient();
        Guid theirs = other.Session.Id;
        var notices = new List<(string Title, string Message)>();
        ShowRestoringPane(theirs, shared: true, notices);
        PumpUntil(() => _pane!.Session is MuxClientSession { IsAttached: true } m && m.Id == theirs, "the pane attached the shared session");
        Assert.True(_pane!.MuxSessionIsShare);

        PaneNode node = Ntilde.Shell.SessionManager.BuildPaneTree(_pane)!;
        Assert.True(node.MuxShared);

        // A pane that has not spawned yet keeps what it was going to do.
        var unspawned = Assert.IsType<TerminalPane>(Ntilde.Shell.SessionManager.RestorePaneTree(node, new TerminalSettings()));
        Assert.True(unspawned.MuxAttachSharedToRestore);
        Assert.True(Ntilde.Shell.SessionManager.BuildPaneTree(unspawned)!.MuxShared);
        unspawned.Dispose();

        // The workspace-style strip drops it with the ids.
        Assert.False(Ntilde.Shell.SessionManager.WithoutMuxIds(new NtildeSession { Tabs = { new TabSession { Root = node } } }).Tabs[0].Root!.MuxShared);

        // The relaunch: the other client still holds the session, and the restored pane joins it shared.
        _pane.Dispose();
        _window!.Close();
        Dispatcher.UIThread.RunJobs();
        _pane = null;
        _window = null;
        var restored = Assert.IsType<TerminalPane>(Ntilde.Shell.SessionManager.RestorePaneTree(node, new TerminalSettings()));
        PaneSpawnTestHelpers.DisableShellIntegration(restored);
        restored.SessionFactory = _factory;
        restored.PersistenceNotice += (_, title, message, _) => notices.Add((title, message));
        _pane = restored;
        _window = new Avalonia.Controls.Window { Content = restored, Width = 900, Height = 500 };
        _window.Show();

        PumpUntil(() => restored.Session is MuxClientSession { IsAttached: true } r && r.Id == theirs, "the restored pane rejoined the shared session");
        PumpUntil(() => _mux.Mux(theirs).AttachedClients == 2, "both clients are attached");
        Assert.Empty(notices);
        Assert.True(restored.MuxSessionIsShare);
    }

    [AvaloniaFact]
    public void A_pane_asked_to_share_attaches_alongside_the_other_client()
    {
        ClientPaneModel other = AttachOtherClient();
        Guid theirs = other.Session.Id;
        var notices = new List<(string Title, string Message)>();

        ShowRestoringPane(theirs, shared: true, notices);

        PumpUntil(() => _pane!.Session is MuxClientSession { IsAttached: true } m && m.Id == theirs, "the pane attached the shared session");
        PumpUntil(() => _mux.Mux(theirs).AttachedClients == 2, "both clients are attached");
        Assert.Empty(notices);
        Assert.False(_pane!.MuxAttachSharedToRestore, "consumed once");
    }
}
