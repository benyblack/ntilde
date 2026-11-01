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
    [InlineData(false)]
    [InlineData(true)]
    public void AttachCompletionDoesNotUndoNewerStreamGeometry(bool applySettings)
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
        var attach = (Task)typeof(TerminalPane).GetMethod("AttachMuxAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(pane, [session, false])!;
        attach.GetAwaiter().GetResult();
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
    }
}
