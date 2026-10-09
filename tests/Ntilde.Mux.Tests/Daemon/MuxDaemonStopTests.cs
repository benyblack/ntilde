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
}
