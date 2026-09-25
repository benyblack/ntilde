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

    /// <summary>
    /// PR #489 CI regression: on Linux/macOS the socket lives in the descriptor's directory and the
    /// daemon refuses one that already exists at any mode but 0700, so a descriptor written before
    /// the daemon starts (a leftover, a client) must create that directory 0700, not 0755.
    /// </summary>
    [Fact]
    public void Writing_a_descriptor_creates_a_missing_directory_owner_only_on_unix()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX directory modes are a Linux/macOS concern.");
        MuxDiscovery.WriteDescriptor(DescriptorPath, Descriptor(42, "ntilde"));
#pragma warning disable CA1416 // skipped on Windows above
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(Path.GetDirectoryName(DescriptorPath)!));
#pragma warning restore CA1416
    }

    /// <summary>
    /// PR #489 CI regression: kill-server polls the descriptor while the daemon deletes it. On
    /// Windows a reader opened without FILE_SHARE_DELETE makes DeleteFile (and the atomic replace
    /// in WriteDescriptor) fail with a sharing violation; the delete is best-effort, so the
    /// descriptor stayed behind naming a live pid and kill-server waited out its full 5 s. Readers
    /// must never block the owner's delete or replace. Unix unlink/rename ignore open handles, so
    /// there this simply passes.
    /// </summary>
    [Fact]
    public void A_concurrent_reader_never_blocks_the_owners_replace_or_delete()
    {
        using var stop = new CancellationTokenSource();
        Directory.CreateDirectory(Path.GetDirectoryName(DescriptorPath)!);
        var reader = new Thread(() =>
        {
            while (!stop.IsCancellationRequested) MuxDiscovery.TryReadDescriptor(DescriptorPath, out _);
        })
        { IsBackground = true };
        reader.Start();
        try
        {
            for (int i = 0; i < 300; i++)
            {
                MuxDiscovery.WriteDescriptor(DescriptorPath, Descriptor(42, "ntilde"));
                MuxDiscovery.WriteDescriptor(DescriptorPath, Descriptor(42, "ntilde")); // replace over a live reader
                MuxDiscovery.DeleteDescriptorIfOwned(DescriptorPath, pid: 42);
                Assert.False(File.Exists(DescriptorPath), $"iteration {i}: the delete lost to a concurrent reader");
            }
        }
        finally
        {
            stop.Cancel();
            reader.Join();
        }
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

    /// <summary>
    /// PR #489 review 2, item 9: the long-path fallback used the world-writable temp directory, where
    /// the socket directory's name is predictable. $XDG_RUNTIME_DIR (per-user, 0700) comes first.
    /// Runs in this class, whose tests run one at a time, so the variable change stays local.
    /// </summary>
    [Fact]
    public void Unix_long_path_fallback_prefers_an_existing_XDG_RUNTIME_DIR()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows uses pipe names.");
        string longRoot = Path.Combine(Path.GetTempPath(), new string('r', 150));
        string runtimeDir = Path.Combine(Path.GetTempPath(), "nxdg" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(runtimeDir);
        string? saved = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        try
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", runtimeDir);
            string endpoint = MuxDiscovery.GetDefaultEndpoint(longRoot);
            Assert.Equal(runtimeDir, Path.GetDirectoryName(Path.GetDirectoryName(endpoint)));
            Assert.EndsWith("mux.sock", endpoint);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", saved);
            Directory.Delete(runtimeDir);
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("relative")]
    [InlineData("empty")]
    public void Unix_long_path_fallback_uses_tmp_when_XDG_RUNTIME_DIR_is_unusable(string kind)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows uses pipe names.");
        string longRoot = Path.Combine(Path.GetTempPath(), new string('r', 150));
        string value = kind switch
        {
            "missing" => Path.Combine(Path.GetTempPath(), "nxdg-absent-" + Guid.NewGuid().ToString("N")),
            "relative" => "relative/run",
            _ => "",
        };
        string? saved = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        try
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", value);
            string endpoint = MuxDiscovery.GetDefaultEndpoint(longRoot);
            Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetTempPath()), Path.GetDirectoryName(Path.GetDirectoryName(endpoint)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", saved);
        }
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
