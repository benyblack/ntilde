using System.Diagnostics;
using Ntilde.Mux;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Shell.Mux;

namespace Ntilde.Tests.Shell.Mux;

public sealed class MuxStartupProbeTests : IDisposable
{
    // Short on purpose: the Unix socket path must fit sun_path (see MuxDiscovery.GetDefaultEndpoint).
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nmxp" + Guid.NewGuid().ToString("N")[..8]);
    private MuxDaemonHost? _host;

    public void Dispose()
    {
        _host?.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string DescriptorPath => MuxDiscovery.GetDescriptorPath(_root);

    /// <summary>A descriptor naming THIS live process (pid and name match) with nobody listening: a recycled pid.</summary>
    private void WriteRecycledPidDescriptor()
    {
        using Process self = Process.GetCurrentProcess();
        MuxDiscovery.WriteDescriptor(DescriptorPath, new MuxEndpointDescriptor
        {
            MinVersion = MuxProtocol.MinSupportedVersion,
            MaxVersion = MuxProtocol.MaxSupportedVersion,
            Endpoint = MuxDiscovery.GetDefaultEndpoint(_root),
            Pid = self.Id,
            ProcessName = self.ProcessName,
        });
    }

    [Fact]
    public void No_descriptor_is_not_live() =>
        Assert.False(MuxStartupProbe.IsDaemonLive(DescriptorPath, TimeSpan.FromMilliseconds(200)));

    [Fact]
    public void A_recycled_pid_whose_endpoint_refuses_is_not_live_and_its_descriptor_is_deleted()
    {
        WriteRecycledPidDescriptor();

        Assert.False(MuxStartupProbe.IsDaemonLive(DescriptorPath, TimeSpan.FromMilliseconds(200)));
        Assert.False(File.Exists(DescriptorPath));
    }

    [Fact]
    public void A_connect_that_times_out_counts_as_refused()
    {
        WriteRecycledPidDescriptor();

        bool live = MuxStartupProbe.IsDaemonLive(DescriptorPath, TimeSpan.FromMilliseconds(200),
            (_, _) => throw new TimeoutException("scripted"));

        Assert.False(live);
        Assert.False(File.Exists(DescriptorPath));
    }

    [Fact]
    public void A_listening_daemon_is_live_and_its_descriptor_is_kept()
    {
        var server = new MuxServer(new ScriptedSessionFactory(), new MuxServerOptions { ForceConPtyFiltering = false });
        _host = new MuxDaemonHost(server, new MuxDaemonOptions
        {
            Endpoint = MuxDiscovery.GetDefaultEndpoint(_root),
            DescriptorPath = DescriptorPath,
            IdleExitAfter = TimeSpan.Zero,
        });
        _host.Start();

        Assert.True(MuxStartupProbe.IsDaemonLive(DescriptorPath, TimeSpan.FromSeconds(2)));
        Assert.True(File.Exists(DescriptorPath));
    }
}
