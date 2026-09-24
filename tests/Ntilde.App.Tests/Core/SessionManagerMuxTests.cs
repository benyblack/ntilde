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

    /// <summary>
    /// Final-fix item 3: only the startup session file names daemon sessions. A capture for a
    /// workspace/template/bundle (the default) carries none - including the restored-placeholder
    /// fallback path, whose shared Tag tree must not be mutated by the strip.
    /// </summary>
    [AvaloniaFact]
    public void Only_the_session_file_capture_keeps_mux_ids()
    {
        Guid live = Guid.NewGuid(), placeholder = Guid.NewGuid();
        var pane = Assert.IsType<TerminalPane>(SessionManager.RestorePaneTree(
            new PaneNode { Type = NodeType.Leaf, Command = "pwsh.exe", MuxSessionId = live.ToString(), MuxEndpoint = "ep-1" }, new TerminalSettings()));
        var restoredTab = new TabSession
        {
            Title = "deferred",
            Root = new PaneNode { Type = NodeType.Leaf, Command = "pwsh.exe", MuxSessionId = placeholder.ToString(), MuxEndpoint = "ep-1" },
        };
        var tabs = new Avalonia.Controls.TabControl();
        tabs.Items.Add(new Avalonia.Controls.TabItem { Header = "Terminal", Content = pane });
        tabs.Items.Add(new Avalonia.Controls.TabItem { Header = "deferred", Content = new Avalonia.Controls.Border(), Tag = restoredTab });
        var window = new Avalonia.Controls.Window { Content = tabs };

        NtildeSession forWorkspace = SessionManager.CaptureSession(window, tabs);
        NtildeSession forSessionFile = SessionManager.CaptureSession(window, tabs, includeMuxIds: true);

        Assert.All(forWorkspace.Tabs, t => { Assert.Null(t.Root!.MuxSessionId); Assert.Null(t.Root.MuxEndpoint); });
        Assert.Equal(live.ToString("D"), forSessionFile.Tabs[0].Root!.MuxSessionId);
        Assert.Equal(placeholder.ToString(), forSessionFile.Tabs[1].Root!.MuxSessionId);
        Assert.Equal(placeholder.ToString(), restoredTab.Root.MuxSessionId); // the Tag tree is untouched
        Assert.Equal("pwsh.exe", forWorkspace.Tabs[1].Root!.Command); // the rest of the layout survives
        pane.Dispose();
    }

    [Fact]
    public void A_bundle_export_embeds_no_mux_ids()
    {
        var session = new NtildeSession
        {
            Tabs = { new TabSession { Title = "t", Root = new PaneNode { Type = NodeType.Leaf, Command = "pwsh.exe", MuxSessionId = Guid.NewGuid().ToString(), MuxEndpoint = "ep-1" } } },
        };
        string path = Path.Combine(AppPaths.RootDirectory, "bundle-" + Guid.NewGuid().ToString("N") + ".ntildews.json");

        Assert.True(WorkspaceManager.ExportWorkspaceBundle("mux-strip", session, path));

        string text = File.ReadAllText(path);
        Assert.DoesNotContain("muxsessionid", text.ToLowerInvariant());
        Assert.DoesNotContain("ep-1", text);
        Assert.NotNull(session.Tabs[0].Root!.MuxSessionId); // the caller's snapshot is not mutated
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
