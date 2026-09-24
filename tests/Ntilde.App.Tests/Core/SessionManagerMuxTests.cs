using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.Controls;
using Ntilde.Mux;
using Ntilde.Mux.Tests.Support;
using Ntilde.Pty;
using Ntilde.Shell;
using Ntilde.Shell.Mux;
using Ntilde.Tests.Controls; // FakeTerminalSession, RecordingSessionFactory, PaneSpawnTestHelpers

namespace Ntilde.Tests.Core;

/// <summary>Spec §9: the saved session names each pane's daemon session, and restore hands it back to the pane.</summary>
public sealed class SessionManagerMuxTests : IClassFixture<TestAppDataRoot>
{
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

    [AvaloniaFact]
    public void A_mux_pane_round_trips_its_session_id_and_endpoint()
    {
        using var mux = new MuxTestHost();
        using var host = new MuxConnectionHost(ct => MuxClient.ConnectAsync(mux.Listener.Connect(), null, ct), "ep-1", null);
        using var pane = new TerminalPane();
        PaneSpawnTestHelpers.DisableShellIntegration(pane);
        pane.SessionFactory = new MuxTerminalSessionFactory(host, new RecordingSessionFactory(new FakeTerminalSession()), null);
        pane.CreateAndWireParser();
        pane.InitializeSessionCore("scripted", "", profile: null, cols: 80, rows: 24);
        var session = Assert.IsType<MuxClientSession>(pane.Session);
        PumpUntil(() => session.IsAttached, "the pane attached");

        PaneNode node = SessionManager.BuildPaneTree(pane)!;
        Assert.Equal(session.Id.ToString(), node.MuxSessionId);
        Assert.Equal("ep-1", node.MuxEndpoint);

        var restored = Assert.IsType<TerminalPane>(SessionManager.RestorePaneTree(node, new TerminalSettings()));
        Assert.Equal(session.Id, restored.MuxSessionIdToRestore);
        restored.Dispose();
    }

    [AvaloniaFact]
    public void A_restored_pane_not_yet_spawned_keeps_its_mux_id()
    {
        // A hydrated deferred tab never shown (or an adopted background tab) has no Session yet: a
        // save then must not drop the daemon session it will reattach to.
        Guid id = Guid.NewGuid();
        var saved = new PaneNode { Type = NodeType.Leaf, Command = "pwsh.exe", MuxSessionId = id.ToString(), MuxEndpoint = "ep-1" };
        var restored = Assert.IsType<TerminalPane>(SessionManager.RestorePaneTree(saved, new TerminalSettings()));
        Assert.Null(restored.Session);

        PaneNode node = SessionManager.BuildPaneTree(restored)!;

        Assert.Equal(id.ToString("D"), node.MuxSessionId);
        restored.Dispose();
    }

    [AvaloniaFact]
    public void A_plain_pane_writes_no_mux_fields()
    {
        using var pane = new TerminalPane();
        PaneSpawnTestHelpers.DisableShellIntegration(pane);
        pane.SessionFactory = new RecordingSessionFactory(new FakeTerminalSession());
        pane.CreateAndWireParser();
        pane.InitializeSessionCore("fake", "", profile: null, cols: 80, rows: 24);

        PaneNode node = SessionManager.BuildPaneTree(pane)!;

        Assert.Null(node.MuxSessionId);
        Assert.Null(node.MuxEndpoint);
    }

    [AvaloniaFact]
    public void A_leaf_with_a_garbage_mux_id_restores_as_a_plain_pane()
    {
        var node = new PaneNode { Type = NodeType.Leaf, Command = "pwsh.exe", MuxSessionId = "not-a-guid" };

        var restored = Assert.IsType<TerminalPane>(SessionManager.RestorePaneTree(node, new TerminalSettings()));

        Assert.Null(restored.MuxSessionIdToRestore);
        restored.Dispose();
    }

    [Fact]
    public void Deferred_tabs_keep_their_mux_ids_in_the_startup_plan()
    {
        Guid id = Guid.NewGuid();
        var session = new NtildeSession
        {
            ActiveTabIndex = 0,
            Tabs =
            {
                new TabSession { Title = "a" },
                new TabSession { Title = "b", Root = new PaneNode { MuxSessionId = id.ToString() } },
            },
        };
        StartupRestorePlan plan = StartupRestorePlan.Create(session);
        Assert.Equal(id.ToString(), plan.DeferredTabs.Single().Tab.Root!.MuxSessionId);
    }
}
