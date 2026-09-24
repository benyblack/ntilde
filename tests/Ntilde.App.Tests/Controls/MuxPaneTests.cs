using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.Controls;
using Ntilde.Mux;
using Ntilde.Mux.Tests.Support;
using Ntilde.Shell.Mux;
using Ntilde.Tests.Shell.Mux;
using Ntilde.VT;
using Ntilde.VT.Tests.StateTransfer;
using Xunit;

namespace Ntilde.Tests.Controls;

/// <summary>
/// A TerminalPane backed by a real MuxServer over an in-memory transport (Phase 2 spec §8): the
/// pane's buffer and parser must end every scenario equal to the daemon's.
/// </summary>
public sealed class MuxPaneTests : IDisposable
{
    private readonly MuxTestHost _mux = new();
    private readonly MuxConnectionHost _host;
    private readonly MuxTerminalSessionFactory _factory;
    private TerminalPane? _pane;
    private Avalonia.Controls.Window? _window;

    public MuxPaneTests()
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

    private MuxClientSession StartPane()
    {
        _pane = new TerminalPane();
        PaneSpawnTestHelpers.DisableShellIntegration(_pane);
        _pane.SessionFactory = _factory;
        _pane.CreateAndWireParser();
        _pane.InitializeSessionCore("scripted", string.Empty, profile: null, cols: 80, rows: 24);
        var session = Assert.IsType<MuxClientSession>(_pane.Session);
        PumpUntil(() => session.IsAttached, "the pane attached");
        return session;
    }

    /// <summary>
    /// A pane in a shown window, spawned the way a real launch spawns it. Needed wherever the pane
    /// calls InitializeSession itself (Reconnect): an unhosted TermView has a 0x0 grid, and
    /// InitializeSession returns early on that (see TerminalPaneSshDisconnectTests).
    /// </summary>
    private MuxClientSession StartHostedPane()
    {
        _pane = new TerminalPane();
        PaneSpawnTestHelpers.DisableShellIntegration(_pane);
        _pane.SessionFactory = _factory;
        _window = new Avalonia.Controls.Window { Content = _pane, Width = 900, Height = 500 };
        _window.Show();
        PumpUntil(() => _pane.Session is MuxClientSession { IsAttached: true }, "the hosted pane attached");
        return (MuxClientSession)_pane.Session!;
    }

    private void Settle(Guid id)
    {
        MuxClient client = _host.CurrentClient!;
        Task.Run(() => _mux.SettleAsync(id, client)).GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
    }

    private void SettleAndAssertEqual(Guid id, string because)
    {
        Settle(id);
        HeadlessTerminalSession m = _mux.Mux(id);
        TerminalStateAssert.AssertEquivalent($"[{because}]", m.Buffer, m.Parser, _pane!.Buffer!, _pane.Parser!);
    }

    [AvaloniaFact]
    public void Spawn_attach_and_output_leave_the_pane_buffer_equal_to_the_mux()
    {
        MuxClientSession s = StartPane();
        _mux.Fake(s.Id).Emit("hello\r\n\x1b[31mred\x1b[0m world\r\n");
        SettleAndAssertEqual(s.Id, "after output");
        Assert.Contains("red world", BufferText(_pane!.Buffer!));
    }

    [AvaloniaFact]
    public void The_mux_parser_is_built_without_image_support()
    {
        StartPane();
        Assert.Null(_pane!.Parser!.ImageDecoder);
        Assert.False(_pane.Parser.AllowNativeKittyGraphics);
        Assert.Null(_pane.Parser.ReadFileBytes);
        Assert.True(_pane.TermView.DefersBufferResizeToSession);
    }

    [AvaloniaFact]
    public void A_stream_resize_resizes_the_pane_buffer_and_the_view_records_it()
    {
        MuxClientSession s = StartPane();
        _mux.Mux(s.Id).PostResize(100, 30, null);
        PumpUntil(() => _pane!.Buffer!.Cols == 100 && _pane.Buffer.Rows == 30, "the stream resize reached the pane");
        PumpUntil(() => _pane!.TermView.LastDispatchedGridForTest == (100, 30), "the view recorded the session's resize");
        SettleAndAssertEqual(s.Id, "after stream resize");
    }

    [AvaloniaFact]
    public void A_resize_storm_ends_with_equal_state()
    {
        MuxClientSession s = StartPane();
        for (int i = 0; i < 40; i++)
        {
            s.Resize(60 + i % 30, 20 + i % 10);
            _mux.Fake(s.Id).Emit($"line {i}\r\n");
        }

        SettleAndAssertEqual(s.Id, "after the storm");
    }

    [AvaloniaFact]
    public void A_resize_storm_through_a_hosted_TerminalView_ends_with_equal_state()
    {
        // The real path: layout → TerminalView (deferred) → OnResize → Session.Resize → server →
        // ResizeEvent → StreamResize → pane buffer. Ready spawns the session like a real launch.
        MuxClientSession s = StartHostedPane();
        for (int i = 0; i < 25; i++)
        {
            _pane!.TermView.ReleaseResizeThrottleForTest();
            _window!.Width = 500 + (i * 37 % 400);
            _window.Height = 300 + (i * 23 % 200);
            Dispatcher.UIThread.RunJobs();
            _mux.Fake(s.Id).Emit($"storm {i} " + new string('x', i * 3) + "\r\n");
        }

        SettleAndAssertEqual(s.Id, "after a layout-driven resize storm");
        Assert.True(_mux.Fake(s.Id).Resizes.Count > 1, "the layout storm reached the shell as resizes");
        Assert.Equal((_pane!.Buffer!.Cols, _pane.Buffer.Rows), _pane.TermView.LastDispatchedGridForTest);
    }

    [AvaloniaFact]
    public void Disconnect_writes_the_banner_and_does_not_raise_ProcessExited()
    {
        StartPane();
        int exited = 0;
        _pane!.ProcessExited += (_, _) => exited++;
        _host.CurrentClient!.Dispose();
        PumpUntil(() => BufferText(_pane.Buffer!).Contains("[Multiplexer disconnected]"), "the banner is shown");
        Assert.Equal(0, exited);
        Assert.True(TerminalPane.ShouldReconnectOnEnter(_pane.Session));
    }

    [AvaloniaFact]
    public void Enter_after_a_disconnect_reattaches_the_same_session_and_replaces_the_banner()
    {
        MuxClientSession s = StartHostedPane();
        _mux.Fake(s.Id).Emit("before\r\n");
        Settle(s.Id);
        _host.CurrentClient!.Dispose();
        PumpUntil(() => BufferText(_pane!.Buffer!).Contains("[Multiplexer disconnected]"), "the banner is shown");

        _pane!.Reconnect();
        var again = Assert.IsType<MuxClientSession>(_pane.Session);
        Assert.Equal(s.Id, again.Id);
        PumpUntil(() => again.IsAttached, "reattached");
        Assert.DoesNotContain("[Multiplexer disconnected]", BufferText(_pane.Buffer!));
        Assert.Contains("before", BufferText(_pane.Buffer!));
        SettleAndAssertEqual(s.Id, "after reattach");
    }

    [AvaloniaFact]
    public void Disconnect_then_Enter_with_session_gone_spawns_fresh_with_lost_banner()
    {
        MuxClientSession s = StartHostedPane();
        _host.CurrentClient!.Dispose();
        PumpUntil(() => BufferText(_pane!.Buffer!).Contains("[Multiplexer disconnected]"), "the banner is shown");
        _mux.Server.KillAllSessions();

        _pane!.Reconnect();
        var fresh = Assert.IsType<MuxClientSession>(_pane.Session);
        Assert.NotEqual(s.Id, fresh.Id);
        PumpUntil(() => BufferText(_pane.Buffer!).Contains(TerminalPane.MuxPreviousLostBanner), "the lost banner is shown");
    }

    [AvaloniaFact]
    public void Late_snapshot_after_pane_dispose_is_ignored()
    {
        // Controller ruling R1: detach first and let nothing be in flight, so the only thing that can
        // resize the buffer afterwards is the late HandleMuxSnapshot call itself.
        MuxClientSession s = StartPane();
        TerminalBuffer buffer = _pane!.Buffer!;
        _pane.DetachFromUiThread()?.Dispose();
        Settle(s.Id);
        (int cols, int rows) = (buffer.Cols, buffer.Rows);

        HeadlessTerminalSession m = _mux.Mux(s.Id);
        m.PostResize(120, 40, null);
        Task.Run(() => m.FlushAsync()).GetAwaiter().GetResult();
        TerminalStateSnapshot snapshot = Task.Run(() => m.InvokeAsync(() => m.CaptureSnapshot(100))).GetAwaiter().GetResult();
        Assert.Equal((120, 40), (snapshot.Cols, snapshot.Rows));
        Assert.NotEqual((120, 40), (cols, rows));

        _pane.HandleMuxSnapshot(s, snapshot); // must not touch the buffer
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((cols, rows), (buffer.Cols, buffer.Rows));
    }

    [AvaloniaFact]
    public void An_unreachable_daemon_gives_a_local_session_and_the_unavailable_banner()
    {
        using var deadHost = new MuxConnectionHost(_ => throw new MuxUnavailableException("down"), "x", null);
        _pane = new TerminalPane();
        PaneSpawnTestHelpers.DisableShellIntegration(_pane);
        _pane.SessionFactory = new MuxTerminalSessionFactory(deadHost, new RecordingSessionFactory(new FakeTerminalSession()), null)
        { ConnectTimeout = TimeSpan.FromMilliseconds(300) };
        _pane.CreateAndWireParser();
        _pane.InitializeSessionCore("scripted", string.Empty, profile: null, cols: 80, rows: 24);
        Assert.IsType<FakeTerminalSession>(_pane.Session);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(TerminalPane.MuxUnavailableBanner, BufferText(_pane.Buffer!));
        Assert.False(_pane.TermView.DefersBufferResizeToSession);
        Assert.Null(_pane.MuxEndpoint);
    }

    [AvaloniaFact]
    public void A_real_exit_goes_through_the_normal_exit_path()
    {
        MuxClientSession s = StartPane();
        int exited = 0;
        _pane!.ProcessExited += (_, _) => exited++;
        _mux.Fake(s.Id).Exit(0);
        PumpUntil(() => exited == 1, "ProcessExited fired");
    }

    [AvaloniaFact]
    public void The_restore_id_is_passed_once_as_ExistingMuxSessionId()
    {
        MuxClientSession first = StartPane();
        Guid id = first.Id;
        Assert.Equal("test", _pane!.MuxEndpoint);
        _pane.Dispose();

        _pane = new TerminalPane { MuxSessionIdToRestore = id };
        PaneSpawnTestHelpers.DisableShellIntegration(_pane);
        _pane.SessionFactory = _factory;
        _pane.CreateAndWireParser();
        _pane.InitializeSessionCore("scripted", string.Empty, profile: null, cols: 80, rows: 24);
        Assert.Equal(id, Assert.IsType<MuxClientSession>(_pane.Session).Id);
        Assert.Null(_pane.MuxSessionIdToRestore);
    }

    [AvaloniaFact]
    public void A_successful_attach_raises_PersistentSessionAttached_once()
    {
        _pane = new TerminalPane();
        PaneSpawnTestHelpers.DisableShellIntegration(_pane);
        _pane.SessionFactory = _factory;
        int attached = 0;
        _pane.PersistentSessionAttached += _ => attached++;
        _pane.CreateAndWireParser();
        _pane.InitializeSessionCore("scripted", string.Empty, profile: null, cols: 80, rows: 24);
        PumpUntil(() => attached == 1, "PersistentSessionAttached fired");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, attached);
    }

    private static string BufferText(TerminalBuffer buffer) => MuxTestText.VisibleText(buffer);
}
