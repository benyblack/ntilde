using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.Controls;
using Ntilde.Mux;
using Ntilde.Mux.Tests.Support;
using Ntilde.Shell.Mux;

namespace Ntilde.Tests.Controls;

/// <summary>Phase 3 spec §7.1, §7.2: restore is exclusive by protocol; a deliberate share joins.</summary>
public sealed class MuxPaneRestoreTests : IDisposable
{
    private readonly MuxTestHost _mux = new();
    private readonly MuxConnectionHost _host;
    private readonly MuxTerminalSessionFactory _factory;
    private TerminalPane? _pane;
    private Avalonia.Controls.Window? _window;

    public MuxPaneRestoreTests()
    {
        _host = new MuxConnectionHost(ct => MuxClient.ConnectAsync(_mux.Listener.Connect(), null, ct), "test", null);
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
    private void ShowRestoringPane(Guid id, bool shared, List<(string Title, string Message)> notices)
    {
        // "scripted": the same program as the daemon session (MuxTestHost.SpawnAsync), so Task 5's
        // command check lets the restore reach the IfUnattached attach instead of spawning fresh.
        _pane = new TerminalPane("scripted");
        PaneSpawnTestHelpers.DisableShellIntegration(_pane);
        _pane.SessionFactory = _factory;
        _pane.MuxSessionIdToRestore = id;
        _pane.MuxAttachSharedToRestore = shared;
        _pane.PersistenceNotice += (_, title, message) => notices.Add((title, message));
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
