using System.Diagnostics;
using Ntilde.Mux.Contracts;

namespace Ntilde.Mux.Tests.Contracts;

public sealed class MuxDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ntilde-disc-" + Guid.NewGuid().ToString("N"));
    private string DescriptorPath => MuxDiscovery.GetDescriptorPath(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private static MuxEndpointDescriptor Descriptor(int pid, string processName) => new()
    {
        MinVersion = MuxProtocol.MinSupportedVersion,
        MaxVersion = MuxProtocol.MaxSupportedVersion,
        Endpoint = "ep",
        Pid = pid,
        ProcessName = processName,
    };

    [Fact]
    public void Descriptor_path_is_under_the_root_mux_directory()
    {
        Assert.Equal(Path.Combine(_root, "mux", "mux-endpoint.json"), DescriptorPath);
    }

    [Fact]
    public void Write_then_read_round_trips_and_leaves_no_temp_files()
    {
        MuxDiscovery.WriteDescriptor(DescriptorPath, Descriptor(123, "ntilde"));
        Assert.True(MuxDiscovery.TryReadDescriptor(DescriptorPath, out MuxEndpointDescriptor? back));
        Assert.Equal(123, back.Pid);
        Assert.Equal("ep", back.Endpoint);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(DescriptorPath)!));
    }

    [Fact]
    public void The_current_process_under_its_own_name_is_live()
    {
        using Process self = Process.GetCurrentProcess();
        MuxDiscovery.WriteDescriptor(DescriptorPath, Descriptor(self.Id, self.ProcessName));
        Assert.True(MuxDiscovery.TryReadLiveDescriptor(DescriptorPath, out _));
    }

    [Fact]
    public void A_reused_pid_with_a_different_process_name_is_not_live()
    {
        using Process self = Process.GetCurrentProcess();
        MuxDiscovery.WriteDescriptor(DescriptorPath, Descriptor(self.Id, "definitely-not-" + self.ProcessName));
        Assert.False(MuxDiscovery.TryReadLiveDescriptor(DescriptorPath, out _));
    }

    [Fact]
    public void A_dead_pid_is_not_live()
    {
        using Process p = Process.Start(new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            OperatingSystem.IsWindows() ? "/c exit 0" : "-c true")
        { UseShellExecute = false, CreateNoWindow = true })!;
        p.WaitForExit();
        MuxDiscovery.WriteDescriptor(DescriptorPath, Descriptor(p.Id, "cmd"));
        Assert.False(MuxDiscovery.TryReadLiveDescriptor(DescriptorPath, out _));
    }

    [Fact]
    public void Missing_or_garbage_descriptor_reads_as_absent()
    {
        Assert.False(MuxDiscovery.TryReadDescriptor(DescriptorPath, out _));
        Directory.CreateDirectory(Path.GetDirectoryName(DescriptorPath)!);
        File.WriteAllText(DescriptorPath, "{not json");
        Assert.False(MuxDiscovery.TryReadDescriptor(DescriptorPath, out _));
    }

    [Fact]
    public void Delete_if_owned_keeps_another_pids_descriptor()
    {
        MuxDiscovery.WriteDescriptor(DescriptorPath, Descriptor(42, "ntilde"));
        MuxDiscovery.DeleteDescriptorIfOwned(DescriptorPath, pid: 43);
        Assert.True(File.Exists(DescriptorPath));
        MuxDiscovery.DeleteDescriptorIfOwned(DescriptorPath, pid: 42);
        Assert.False(File.Exists(DescriptorPath));
    }

    [Fact]
    public void Different_roots_get_different_endpoints()
    {
        string other = _root + "-b";
        Assert.NotEqual(MuxDiscovery.GetDefaultEndpoint(_root), MuxDiscovery.GetDefaultEndpoint(other));
    }

    [Fact]
    public void Unix_endpoint_falls_back_to_tmp_when_the_socket_path_is_too_long()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows uses pipe names.");
        string longRoot = Path.Combine(Path.GetTempPath(), new string('r', 150));
        string endpoint = MuxDiscovery.GetDefaultEndpoint(longRoot);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(endpoint) <= 103, endpoint);
        Assert.EndsWith("mux.sock", endpoint);
    }

    [Fact]
    public void Windows_endpoint_is_a_bare_pipe_name()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Unix uses socket paths.");
        string endpoint = MuxDiscovery.GetDefaultEndpoint(_root);
        Assert.StartsWith("ntilde-mux-", endpoint);
        Assert.DoesNotContain('\\', endpoint);
    }
}
