using System.Reflection;
using System.Threading;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.Controls;
using Ntilde.Mux;
using Ntilde.Mux.Tests.Support;
using Ntilde.Shell;
using Ntilde.Shell.Mux;

namespace Ntilde.Tests.Controls;

public sealed class MuxAttachCompletionTests
{
    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void AttachCompletionDoesNotUndoNewerStreamGeometry(bool applySettings, bool changeHistoryDuringAttach)
    {
        using var mux = new MuxTestHost();
        using var host = new MuxConnectionHost(ct => MuxClient.ConnectAsync(mux.Listener.Connect(), null, ct), "test", null);
        var factory = new MuxTerminalSessionFactory(host, new RecordingSessionFactory(new FakeTerminalSession()), null);
        using var pane = new TerminalPane();
        PaneSpawnTestHelpers.DisableShellIntegration(pane);
        if (applySettings) pane.ApplySettings(new TerminalSettings { CommandAssistShellIntegrationEnabled = false });
        pane.SessionFactory = factory;
        pane.CreateAndWireParser();
        int attached = 0;
        pane.PersistentSessionAttached += _ => attached++;
        pane.InitializeSessionCore("scripted", string.Empty, null, 80, 24);
        var session = Assert.IsType<MuxClientSession>(pane.Session);
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (attached == 0)
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(5), "Initial attach completed.");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }
        // Wait for a fresh successful attach to POST its UI completion, keeping that callback queued.
        if (changeHistoryDuringAttach) AttachAgainWithHistoryChange(mux, pane, session);
        else AttachAgain(pane, session).GetAwaiter().GetResult();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var ct = TestContext.Current.CancellationToken;
        var resizeEvent = typeof(MuxClientSession).GetField("StreamResize", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var deliverToPane = (Action<int, int>?)resizeEvent.GetValue(session);
        Action<int, int> holdNewGeometry = (cols, _) =>
        {
            if (cols != 100) return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5), ct)) throw new TimeoutException("Resize was not released.");
        };
        // Hold delivery of the newer geometry, as a delayed transport can, before the pane sees it.
        resizeEvent.SetValue(session, holdNewGeometry + deliverToPane);
        mux.Mux(session.Id).PostResize(100, 30, null);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5), ct));
            Dispatcher.UIThread.RunJobs();
        }
        finally { release.Set(); }
        Task.Run(async () =>
        {
            await host.CurrentClient!.PingAsync();
            await mux.Mux(session.Id).FlushAsync();
            await mux.SettleAsync(session.Id, host.CurrentClient);
        }).GetAwaiter().GetResult();

        Assert.Equal(100, mux.Mux(session.Id).Buffer.Cols);
        Assert.Equal(30, mux.Mux(session.Id).Buffer.Rows);
        if (changeHistoryDuringAttach) Assert.Equal(60_000, mux.Mux(session.Id).HistoryRetentionRows);
    }

    private static Task AttachAgain(TerminalPane pane, MuxClientSession session) =>
        (Task)typeof(TerminalPane).GetMethod("AttachMuxAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(pane, [session, false])!;

    private static void AttachAgainWithHistoryChange(MuxTestHost mux, TerminalPane pane, MuxClientSession session)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var ct = TestContext.Current.CancellationToken;
        int calls = 0;
        mux.Fake(session.Id).OnResizeCalled = (_, _) =>
        {
            if (Interlocked.Increment(ref calls) != 1) return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5), ct)) throw new TimeoutException("Attach was not released.");
        };
        Task attach;
        try
        {
            mux.Mux(session.Id).PostResize(81, 24, null);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5), ct));
            // Model a fresh attach whose snapshot has not yet been published, without
            // disposing this session (Detach permanently disposes and unregisters it).
            typeof(MuxClientSession).GetField("_expectedOffset", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, -1L);
            attach = AttachAgain(pane, session);
            Assert.False(session.IsAttached);
            pane.ApplySettings(new TerminalSettings { MaxHistory = 60_000, CommandAssistShellIntegrationEnabled = false });
        }
        finally { release.Set(); }
        attach.GetAwaiter().GetResult();
        mux.Fake(session.Id).OnResizeCalled = null;
    }
}
