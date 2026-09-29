using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.Controls;
using Ntilde.Mux;
using Ntilde.Mux.Tests.Support;
using Ntilde.Shell.Mux;
using Ntilde.Tests.Shell.Mux;

namespace Ntilde.Tests.Controls;

/// <summary>Phase 3 spec §7.3, §7.5.</summary>
public sealed class MuxPaneSharingTests : IDisposable
{
    private readonly MuxTestHost _mux = new();
    private readonly MuxConnectionHost _host;
    private readonly MuxTerminalSessionFactory _factory;
    private TerminalPane? _pane;
    private TextBox? _decoy;
    private Window? _window;

    public MuxPaneSharingTests()
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

    /// <summary>A hosted pane (it spawns on first layout) beside a focusable decoy, so focus can move away and back.</summary>
    private MuxClientSession StartHostedPane()
    {
        _pane = new TerminalPane();
        PaneSpawnTestHelpers.DisableShellIntegration(_pane);
        _pane.SessionFactory = _factory;
        _decoy = new TextBox();
        var root = new DockPanel();
        DockPanel.SetDock(_decoy, Avalonia.Controls.Dock.Top);
        root.Children.Add(_decoy);
        root.Children.Add(_pane);
        _window = new Window { Content = root, Width = 900, Height = 500 };
        _window.Show();
        PumpUntil(() => _pane.Session is MuxClientSession { IsAttached: true }, "the hosted pane attached");
        return (MuxClientSession)_pane.Session!;
    }

    private ClientPaneModel AttachOther(Guid id, Ntilde.Mux.Contracts.MuxPresentation? presentation = null) => Task.Run(async () =>
    {
        MuxClient c = await _mux.ConnectClientAsync();
        return await MuxTestHost.AttachPaneAsync(c, id, presentation);
    }).GetAwaiter().GetResult();

    [AvaloniaFact]
    public void The_headless_host_raises_GotFocus_on_the_terminal_view()
    {
        StartHostedPane();
        int got = 0;
        _pane!.TermView.GotFocus += (_, _) => got++;
        _decoy!.Focus();
        Dispatcher.UIThread.RunJobs();

        _pane.TermView.Focus();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, got);
    }

    [AvaloniaFact]
    public void The_indicator_follows_SessionChanged()
    {
        MuxClientSession s = StartHostedPane();
        Assert.False(_pane!.MuxSharedIndicator.IsVisible);
        int raised = 0;
        _pane.MuxSharingChanged += _ => raised++;

        ClientPaneModel other = AttachOther(s.Id);
        PumpUntil(() => _pane.MuxSharedIndicator.IsVisible, "the indicator appeared");
        Assert.Equal("shared with 1", _pane.MuxSharedText.Text);
        Assert.Equal(1, _pane.MuxOtherClients);

        other.Session.Dispose();
        PumpUntil(() => !_pane.MuxSharedIndicator.IsVisible, "the indicator went away");
        Assert.Equal(0, _pane.MuxOtherClients);
        Assert.Equal(2, raised);
    }

    [AvaloniaFact]
    public void The_indicator_hides_on_connection_loss_and_a_late_SessionChanged_does_not_bring_it_back()
    {
        MuxClientSession s = StartHostedPane();
        AttachOther(s.Id);
        PumpUntil(() => _pane!.MuxSharedIndicator.IsVisible, "the indicator appeared");

        s.DeliverDisconnected("test: connection dropped");
        PumpUntil(() => !_pane!.MuxSharedIndicator.IsVisible, "the indicator went away with the connection");

        // A notification already in flight when the connection dropped is delivered afterwards.
        s.DeliverSessionChanged(new Ntilde.Mux.Contracts.SessionChangedNotification { SessionId = s.Id, AttachedClients = 2 });
        Dispatcher.UIThread.RunJobs();

        Assert.False(_pane!.MuxSharedIndicator.IsVisible);
        Assert.Equal(0, _pane.MuxOtherClients);
    }

    [AvaloniaFact]
    public void The_exit_banner_says_the_shell_ended_elsewhere_after_a_foreign_kill()
    {
        MuxClientSession s = StartHostedPane();
        MuxClient killer = Task.Run(() => _mux.ConnectClientAsync()).GetAwaiter().GetResult();

        Task.Run(() => killer.KillAsync(s.Id)).GetAwaiter().GetResult();
        PumpUntil(() => !s.IsProcessRunning, "the pane saw the exit");
        Assert.True(s.WasKilledElsewhere);
        _pane!.WriteLocalExitBanner(-1);

        PumpUntil(() => MuxTestText.VisibleText(_pane.Buffer!).Contains(TerminalPane.MuxKilledElsewhereBanner, StringComparison.Ordinal), "the banner is on screen");
        Assert.DoesNotContain("[Shell exited]", MuxTestText.VisibleText(_pane.Buffer!), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void Gaining_focus_takes_the_grid_back_from_another_client()
    {
        MuxClientSession s = StartHostedPane();
        (int cols, int rows) = (_pane!.TermView.Cols, _pane.TermView.Rows);
        PumpUntil(() => _mux.Mux(s.Id).Cols == cols && _mux.Mux(s.Id).Rows == rows, "the session has the pane's grid");
        _decoy!.Focus();
        Dispatcher.UIThread.RunJobs();

        AttachOther(s.Id, MuxTestHost.DefaultPresentation with { Cols = cols - 10, Rows = rows - 3 }); // latest resize wins
        PumpUntil(() => _pane.Buffer!.Cols == cols - 10, "the pane follows the other client (letterboxed)");

        _pane.TermView.Focus();
        PumpUntil(() => _mux.Mux(s.Id).Cols == cols && _mux.Mux(s.Id).Rows == rows, "focus re-requested the pane's own grid");
    }
}
