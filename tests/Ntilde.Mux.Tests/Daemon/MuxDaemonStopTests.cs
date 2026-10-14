using System.Diagnostics;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Daemon;

namespace Ntilde.Mux.Tests.Daemon;

/// <summary>
/// kill-server's steps as the App's uninstall hook reuses them (Phase 5 Task 21). The paths a running daemon takes - a
/// recycled pid, a changed start time, a daemon that stops - are covered through <c>MuxCliTests</c>' kill-server and
/// KillByPid tests, which now run on these.
/// </summary>
public sealed class MuxDaemonStopTests
{
    private static readonly string Descriptor = Path.Combine(Path.GetTempPath(), "nmxstop" + Guid.NewGuid().ToString("N")[..8], "mux", "mux-endpoint.json");

    [Fact]
    public void With_nothing_advertised_there_is_nothing_to_ask()
    {
        Assert.Equal(MuxDaemonStop.ShutdownRequest.NotRunning,
            MuxDaemonStop.RequestShutdown(Descriptor, new MuxClientOptions { ClientKind = "test", RequestTimeout = TimeSpan.FromSeconds(1) }));
    }

    [Fact]
    public void This_process_is_never_terminated()
    {
        var self = new MuxEndpointDescriptor { Endpoint = "x", Pid = Environment.ProcessId, ProcessName = System.Diagnostics.Process.GetCurrentProcess().ProcessName };

        Assert.Equal(MuxDaemonStop.TerminateResult.NotTheDaemon, MuxDaemonStop.Terminate(Descriptor, self, TimeSpan.FromSeconds(1), out string? failure));
        Assert.Equal("Refusing to terminate this process.", failure);
    }

    // Release hardening item 8: the kill path needs a readable start time that matches; a name alone could be the GUI,
    // which shares the daemon's process name.
    [Fact]
    public void A_descriptor_without_a_start_time_is_not_terminated()
    {
        using Process standIn = StartLongRunningProcess();
        try
        {
            var described = new MuxEndpointDescriptor { Endpoint = "x", Pid = standIn.Id, ProcessName = standIn.ProcessName };

            Assert.Equal(MuxDaemonStop.TerminateResult.NotTheDaemon, MuxDaemonStop.Terminate(Descriptor, described, TimeSpan.FromSeconds(1), out _));
            Assert.False(standIn.HasExited, "a process named like the daemon, with no start time to check, is never killed");
        }
        finally
        {
            try { standIn.Kill(); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public void A_pid_whose_start_time_differs_is_not_terminated()
    {
        using Process standIn = StartLongRunningProcess();
        try
        {
            var described = new MuxEndpointDescriptor
            {
                Endpoint = "x",
                Pid = standIn.Id,
                ProcessName = standIn.ProcessName,
                // one tick off on Linux (exact compare), 5 s off elsewhere (1 s tolerance)
                StartTime = MuxDiscovery.GetProcessStartToken(standIn)!.Value + (OperatingSystem.IsLinux() ? 1 : TimeSpan.FromSeconds(5).Ticks),
            };

            Assert.Equal(MuxDaemonStop.TerminateResult.NotTheDaemon, MuxDaemonStop.Terminate(Descriptor, described, TimeSpan.FromSeconds(1), out _));
            Assert.False(standIn.HasExited, "a process started at another time is never killed");
        }
        finally
        {
            try { standIn.Kill(); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public void The_liveness_probe_stays_lenient_without_a_start_time()
    {
        using Process self = Process.GetCurrentProcess();

        Assert.True(MuxDiscovery.StartTimeMatches(self, null));
        Assert.False(MuxDiscovery.StartTimeMatches(self, null, requireStartTime: true));
        Assert.True(MuxDiscovery.StartTimeMatches(self, MuxDiscovery.GetProcessStartToken(self), requireStartTime: true));
    }

    private static Process StartLongRunningProcess()
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping", "-n 60 127.0.0.1")
            : new ProcessStartInfo("sleep", "60");
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        return Process.Start(psi)!;
    }
}
