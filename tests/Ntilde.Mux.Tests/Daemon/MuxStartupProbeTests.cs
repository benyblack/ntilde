using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Daemon;
using Ntilde.Mux.Tests.Support;

namespace Ntilde.Mux.Tests.Daemon;

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
        // Off Windows a timeout always counts as live (Unix socket refusals are immediate), so the
        // pipeExists lookup is never consulted.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The pipe-absent check applies on Windows only.");

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

    [Theory]
    [InlineData(SocketError.ConnectionRefused, true, true)]
    [InlineData(SocketError.AccessDenied, true, false)]
    [InlineData(SocketError.TryAgain, true, false)]
    [InlineData(SocketError.AddressFamilyNotSupported, true, false)]
    public void Only_a_refusal_or_a_missing_socket_is_a_genuine_refusal(SocketError code, bool fileExists, bool expected)
    {
        var ex = new IOException("x", new SocketException((int)code));
        Assert.Equal(expected, MuxStartupProbe.IsGenuineRefusal(ex, "/e", isWindows: false, _ => fileExists));
    }

    [Fact]
    public void A_plain_IOException_is_a_refusal_only_when_the_socket_file_is_missing()
    {
        var ex = new IOException("x");
        Assert.False(MuxStartupProbe.IsGenuineRefusal(ex, "/e", isWindows: false, _ => true));
        Assert.True(MuxStartupProbe.IsGenuineRefusal(ex, "/e", isWindows: false, _ => false));
    }

    [Fact]
    public void On_Windows_only_FileNotFound_is_a_genuine_refusal()
    {
        Assert.True(MuxStartupProbe.IsGenuineRefusal(new FileNotFoundException(), "e", isWindows: true, _ => true));
        Assert.False(MuxStartupProbe.IsGenuineRefusal(new IOException("x"), "e", isWindows: true, _ => false));
        Assert.False(MuxStartupProbe.IsGenuineRefusal(
            new IOException("x", new SocketException((int)SocketError.ConnectionRefused)), "e", isWindows: true, _ => false));
    }

    [Theory]
    [InlineData(SocketError.ConnectionRefused, true, false, false)]
    [InlineData(SocketError.AccessDenied, true, true, true)]
    [InlineData(SocketError.TryAgain, true, true, true)]
    [InlineData(SocketError.AddressFamilyNotSupported, true, true, true)]
    public void Only_a_refusal_deletes_the_descriptor(SocketError code, bool fileExists, bool expectedLive, bool expectedKept)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix socket semantics; the pure function is covered on every OS.");
        WriteRecycledPidDescriptor();

        bool live = MuxStartupProbe.IsDaemonLive(DescriptorPath, TimeSpan.FromMilliseconds(200),
            (_, _) => throw new IOException("x", new SocketException((int)code)), fileExists: _ => fileExists);

        Assert.Equal(expectedLive, live);
        Assert.Equal(expectedKept, File.Exists(DescriptorPath));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    public void A_plain_IOException_deletes_the_descriptor_only_when_the_socket_is_missing(bool fileExists, bool expectedLive, bool expectedKept)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix socket semantics; the pure function is covered on every OS.");
        WriteRecycledPidDescriptor();

        bool live = MuxStartupProbe.IsDaemonLive(DescriptorPath, TimeSpan.FromMilliseconds(200),
            (_, _) => throw new IOException("x"), fileExists: _ => fileExists);

        Assert.Equal(expectedLive, live);
        Assert.Equal(expectedKept, File.Exists(DescriptorPath));
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
