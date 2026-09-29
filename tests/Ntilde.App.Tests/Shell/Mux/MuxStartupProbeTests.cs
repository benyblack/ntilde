using System.Diagnostics;
using System.IO.Pipes;
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

    /// <summary>
    /// Scripted so it is deterministic on every OS: the real Windows check (below) only fires for a
    /// real <see cref="TimeoutException"/> off a real connect, so this pins "busy" (pipe present) via
    /// a stubbed <c>pipeExists</c> rather than relying on a pipe this test never actually created.
    /// </summary>
    [Fact]
    public void A_connect_that_times_out_counts_as_live_and_keeps_the_descriptor()
    {
        WriteRecycledPidDescriptor();

        bool live = MuxStartupProbe.IsDaemonLive(DescriptorPath, TimeSpan.FromMilliseconds(200),
            (_, _) => throw new TimeoutException("scripted"), pipeExists: _ => true);

        Assert.True(live);
        Assert.True(File.Exists(DescriptorPath));
    }

    [Fact]
    public void A_connect_that_times_out_with_no_pipe_present_is_refused_and_its_descriptor_is_deleted()
    {
        WriteRecycledPidDescriptor();

        bool live = MuxStartupProbe.IsDaemonLive(DescriptorPath, TimeSpan.FromMilliseconds(200),
            (_, _) => throw new TimeoutException("scripted"), pipeExists: _ => false);

        Assert.False(live);
        Assert.False(File.Exists(DescriptorPath));
    }

    [Fact]
    public void The_default_pipe_lookup_finds_a_live_pipe_and_stops_finding_it_once_disposed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "\\\\.\\pipe\\ enumeration is Windows-only.");

        string pipeName = "ntilde-mux-test-" + Guid.NewGuid().ToString("N")[..8];
        using (var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1))
        {
            Assert.True(MuxStartupProbe.DefaultPipeExists(pipeName));
        }

        Assert.False(MuxStartupProbe.DefaultPipeExists(pipeName));
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
