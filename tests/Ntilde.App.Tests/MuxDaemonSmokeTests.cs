using System.Diagnostics;
using Ntilde.Mux;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Pty;
using Ntilde.Shell.Mux;
using Ntilde.Tests.Core;
using Ntilde.Tests.Shell.Mux;

namespace Ntilde.Tests;

/// <summary>
/// The one end-to-end check of the real daemon: the launcher starts <c>Ntilde mux serve</c> as a
/// separate process, a real shell runs under it, and the shell outlives the client that spawned it.
/// </summary>
[Trait("Category", "PtySmoke")]
[Collection(PtyRealShellCollection.Name)]
public sealed class MuxDaemonSmokeTests
{
    private const string Marker = "mux-smoke-marker";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The app exe next to the tests (apphost) or <c>dotnet Ntilde.dll</c>.</summary>
    private static ProcessMuxDaemonSpawner Spawner()
    {
        string dir = AppContext.BaseDirectory;
        string apphost = Path.Combine(dir, OperatingSystem.IsWindows() ? "Ntilde.exe" : "Ntilde");
        if (File.Exists(apphost)) return new ProcessMuxDaemonSpawner(apphost, []);

        string dll = Path.Combine(dir, "Ntilde.dll");
        string? dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrEmpty(dotnet) || !File.Exists(dll))
        {
            Assert.Fail($"No way to start the daemon: no apphost at {apphost}, and DOTNET_HOST_PATH ('{dotnet}') or {dll} is missing.");
        }

        return new ProcessMuxDaemonSpawner(dotnet!, [dll]);
    }

    [Fact]
    public async Task A_real_daemon_keeps_a_shell_across_a_client_death_and_kill_server_leaves_nothing_behind()
    {
        // parallelizeTestCollections is off for this assembly, so the process-wide root is ours
        // until disposed; the daemon inherits it through the environment at spawn.
        using var root = new TestAppDataRoot();
        string descriptorPath = MuxDiscovery.GetDescriptorPath(root.RootPath);
        var presentation = MuxTestHost.DefaultPresentation with { Cols = 100, Rows = 30 };
        var launcher = new MuxDaemonLauncher(descriptorPath, Spawner()) { SpawnTimeout = TimeSpan.FromSeconds(30) };
        int? daemonPid = null, shellPid = null;
        try
        {
            MuxClient first = await launcher.EnsureConnectedAsync(Ct);
            Assert.True(MuxDiscovery.TryReadLiveDescriptor(descriptorPath, out MuxEndpointDescriptor? d));
            daemonPid = d.Pid;
            Assert.NotEqual(Environment.ProcessId, daemonPid);

            string shell = ShellHelper.GetDefaultShell();
            Guid id = await first.SpawnAsync(new SpawnParams
            {
                Command = shell,
                Cols = 100,
                Rows = 30,
                SkipPowerShellPostLaunchInit = true,
                Title = "daemon-smoke",
            }, Ct);
            var pane = new ClientPaneModel(first.OpenSession(id, shell));
            await pane.Session.AttachAsync(1000, presentation, Ct);
            shellPid = (await pane.Session.RefreshSessionInfoAsync(Ct)).Pid;

            // Let the shell draw its prompt before typing (early input can be eaten by line editors).
            await TestWait.UntilAsync(() => ScreenText(pane).Length > 0, "the shell drew a prompt", TimeSpan.FromSeconds(30));
            await Task.Delay(500, Ct);
            pane.Session.SendInput($"echo {Marker}\r");
            // The typed command and its output: the marker appears twice.
            await TestWait.UntilAsync(() => CountMarker(pane) >= 2, "the shell echoed the marker", TimeSpan.FromSeconds(30));

            first.Dispose(); // the client side dies; the daemon keeps the session

            MuxClient second = await launcher.EnsureConnectedAsync(Ct);
            Assert.True(MuxDiscovery.TryReadLiveDescriptor(descriptorPath, out MuxEndpointDescriptor? d2));
            Assert.Equal(daemonPid, d2.Pid); // no second daemon
            SessionSummary summary = Assert.Single(await second.ListSessionsAsync(Ct));
            Assert.Equal(id, summary.SessionId);
            Assert.True(summary.Running);

            var again = new ClientPaneModel(second.OpenSession(id, shell));
            await again.Session.AttachAsync(1000, presentation, Ct);
            await TestWait.UntilAsync(() => CountMarker(again) >= 2, "the reattached snapshot shows the earlier output", TimeSpan.FromSeconds(15));

            await second.ShutdownServerAsync(Ct);
            second.Dispose();
            using Process daemon = Process.GetProcessById(daemonPid.Value);
            Assert.True(daemon.WaitForExit(15_000), "the daemon exited");
            Assert.False(File.Exists(descriptorPath), "the daemon deleted its descriptor");
            if (shellPid is int sp) await TestWait.UntilAsync(() => !IsAlive(sp), "the shell is gone", TimeSpan.FromSeconds(15));
        }
        finally
        {
            if (daemonPid is int dp && IsAlive(dp)) { try { using var p = Process.GetProcessById(dp); p.Kill(entireProcessTree: true); } catch (Exception) { } }
            if (shellPid is int sp && IsAlive(sp)) { try { using var p = Process.GetProcessById(sp); p.Kill(); } catch (Exception) { } }
        }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string ScreenText(ClientPaneModel pane) => MuxTestText.VisibleText(pane.Buffer);

    private static int CountMarker(ClientPaneModel pane) => ScreenText(pane).Split(Marker).Length - 1;
}
