using Ntilde.Mux.Contracts;
using Ntilde.Update;

namespace Ntilde.Tests.Update;

/// <summary>
/// Final-fix item 2: Velopack's apply-on-startup must not bypass the in-app apply path, which asks
/// before closing multiplexed sessions and shuts the daemon down first. Since Phase 5 R10 that holds
/// only for a live daemon the apply would kill (<see cref="MuxUpdateCompatibility.StartupApplyHoldFor"/>).
/// </summary>
public sealed class StartupAutoApplyTests
{
    [Theory]
    [InlineData(new[] { "mux", "serve" }, false)]
    [InlineData(new[] { "mux", "serve" }, true)]
    [InlineData(new[] { "mux", "ls" }, false)]
    [InlineData(new[] { "MUX", "kill-server" }, true)]
    public void Mux_cli_modes_never_auto_apply(string[] args, bool daemonLive)
    {
        Assert.False(Program.ShouldAutoApplyUpdateOnStartup(args, () => daemonLive));
    }

    [Fact]
    public void Gui_start_with_a_live_daemon_does_not_auto_apply()
    {
        Assert.False(Program.ShouldAutoApplyUpdateOnStartup([], () => true));
    }

    [Fact]
    public void Gui_start_without_a_daemon_keeps_auto_apply()
    {
        Assert.True(Program.ShouldAutoApplyUpdateOnStartup([], () => false));
    }

    /// <summary>
    /// The askpass helper is this executable too, and ssh starts it unattended - during an automatic reconnect, with nobody
    /// at the screen - with the prompt as its only argument and the mode in its environment (<c>NTILDE_SSH_ASKPASS=1</c>).
    /// A staged update must never be applied in such a run, whichever way it is put in askpass mode; and it never probes.
    /// </summary>
    [Fact]
    public void An_askpass_run_never_auto_applies_by_flag_or_environment()
    {
        int probes = 0;
        Func<bool> live = () =>
        {
            probes++;
            return false;
        };

        Assert.False(Program.ShouldAutoApplyUpdateOnStartup(["--ssh-askpass", "Password:"], live, _ => null));
        Assert.False(Program.ShouldAutoApplyUpdateOnStartup(["ops@prod.internal's password: "], live, name => name == "NTILDE_SSH_ASKPASS" ? "1" : null));
        Assert.Equal(0, probes);
        Assert.True(Program.ShouldAutoApplyUpdateOnStartup(["ops@prod.internal's password: "], live, _ => null));
        Assert.Equal(1, probes);
    }

    [Fact]
    public void A_mux_cli_mode_does_not_even_probe_for_a_daemon()
    {
        int probes = 0;
        Program.ShouldAutoApplyUpdateOnStartup(["mux", "serve"], () => { probes++; return false; });
        Assert.Equal(0, probes);
    }

    // ---- Phase 5 R10: only a live daemon the apply would kill - its image inside the install root - is in the way.
    // With SessionPersistence Off nothing changes from before Phase 5: any live daemon is in the way.

    private static readonly string InstallRoot = Path.Combine(OperatingSystem.IsWindows() ? @"C:\Users\u\AppData\Local" : "/home/u/.local/share", "NtildeApp");

    private static readonly string OwnCopy = Path.Combine(Path.GetDirectoryName(InstallRoot)!, "ntilde", "bin", "0.12.0", "Ntilde.exe");

    private static readonly MuxEndpointDescriptor Daemon = new() { Endpoint = "test", Pid = 4242, ProcessName = "Ntilde", MinVersion = 1, MaxVersion = 2 };

    private static readonly Func<bool> KeepOnClose = () => false;

    private static readonly Func<bool> NotRead = () => throw new InvalidOperationException("the setting is not read");

    /// <summary>Whether the startup gate holds the apply: its answer is a reason, and any but None holds.</summary>
    private static bool Blocks(string? installRoot, Func<bool> daemonLive, Func<MuxEndpointDescriptor?> readDescriptor,
        Func<MuxEndpointDescriptor, string?> daemonImagePath, Func<bool> persistenceOff) =>
        MuxUpdateCompatibility.StartupApplyHoldFor(installRoot, daemonLive, readDescriptor, daemonImagePath, persistenceOff) != StartupApplyHold.None;

    /// <summary><see cref="Blocks"/> through Program's composition under an app-data root.</summary>
    private static bool BlocksAt(string appDataRoot, string? installRoot, Func<string, TimeSpan, Stream>? connect = null,
        Func<MuxEndpointDescriptor, string?>? daemonImagePath = null) =>
        Program.StartupApplyHoldAt(appDataRoot, installRoot, connect, daemonImagePath) != StartupApplyHold.None;

    [Fact]
    public void A_daemon_running_from_its_own_copy_does_not_block_auto_apply()
    {
        Assert.False(Blocks(InstallRoot, () => true, () => Daemon, _ => OwnCopy, KeepOnClose));
    }

    [Fact]
    public void A_daemon_running_from_inside_the_install_root_blocks_auto_apply_whatever_the_setting()
    {
        string inside = Path.Combine(InstallRoot, "current", "Ntilde.exe");

        Assert.True(Blocks(InstallRoot, () => true, () => Daemon, _ => inside, NotRead));
    }

    /// <summary>Never "outside" without evidence: an image that cannot be read, or a descriptor gone after the probe, blocks.</summary>
    [Fact]
    public void A_live_daemon_whose_image_cannot_be_told_blocks_auto_apply()
    {
        Assert.True(Blocks(InstallRoot, () => true, () => Daemon, _ => null, NotRead));
        Assert.True(Blocks(InstallRoot, () => true, () => null, _ => throw new InvalidOperationException("no descriptor to look up"), NotRead));
    }

    /// <summary>The common start: no live daemon. Nothing else is read - neither its image nor the setting.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void No_live_daemon_never_reads_its_image_or_the_setting(bool installRoot)
    {
        Assert.False(Blocks(
            installRoot ? InstallRoot : null, () => false, () => throw new InvalidOperationException("not read"),
            _ => throw new InvalidOperationException("not looked up"), NotRead));
    }

    /// <summary>Off a Windows Velopack install the apply kills nothing: a live daemon is in the way only with persistence off.</summary>
    [Fact]
    public void Without_an_install_root_a_live_daemon_does_not_block_and_its_image_is_not_read()
    {
        Assert.False(Blocks(null, () => true, () => Daemon, _ => throw new InvalidOperationException("not looked up"), KeepOnClose));
    }

    /// <summary>
    /// SessionPersistence Off behaves as before Phase 5: any live daemon holds the update back for the in-app apply, which
    /// asks - one running from its own copy, and one on an install with no root to kill under (macOS, Linux).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void With_persistence_off_any_live_daemon_blocks_auto_apply(bool installRoot)
    {
        Assert.True(Blocks(installRoot ? InstallRoot : null, () => true, () => Daemon, _ => OwnCopy, () => true));
    }

    // ---- Program's wiring of the gate: the descriptor and settings.json under one app-data root, and the install root.

    /// <summary>
    /// A live daemon (a real descriptor under a scratch app-data root, naming this test process; the probe's connect is
    /// stubbed) with settings.json under the same root. Off blocks, as before Phase 5; the default or KeepOnClose does
    /// not. With an install root the image decides first: this process's own pid has no image, so it blocks unread.
    /// </summary>
    [Theory]
    [InlineData("Off", false, true)]
    [InlineData("KeepOnClose", false, false)]
    [InlineData(null, false, false)]
    [InlineData("KeepOnClose", true, true)]
    public void The_gate_reads_the_descriptor_and_the_setting_under_the_app_data_root(string? persistence, bool installRoot, bool blocks)
    {
        string root = ScratchRoot();
        try
        {
            WriteDescriptorNamingThisProcess(root);
            if (persistence is not null) File.WriteAllText(Path.Combine(root, "settings.json"), $"{{\"SessionPersistence\":\"{persistence}\"}}");
            int connects = 0;

            bool blocked = BlocksAt(root, installRoot ? InstallRoot : null, (_, _) =>
            {
                connects++;
                return new MemoryStream();
            });

            Assert.Equal(blocks, blocked);
            Assert.Equal(1, connects);
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    /// <summary>
    /// PR #511 heads-up: the default flip (SessionPersistence on) leans on the update-survival work. With a settings file
    /// that does not name SessionPersistence - the default, so on - and a live daemon running from its own copy outside the
    /// install root, the startup apply goes ahead; one running from inside the root holds it, as the apply would kill it.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void With_the_default_setting_only_a_daemon_inside_the_install_root_holds_the_startup_apply(bool imageInsideRoot, bool blocks)
    {
        string root = ScratchRoot();
        try
        {
            WriteDescriptorNamingThisProcess(root);
            File.WriteAllText(Path.Combine(root, "settings.json"), "{\"FontSize\":14,\"Theme\":\"Default\"}");
            string image = imageInsideRoot ? Path.Combine(InstallRoot, "current", "Ntilde.exe") : OwnCopy;
            int connects = 0;
            Stream Connect(string endpoint, TimeSpan timeout)
            {
                connects++;
                return new MemoryStream();
            }

            Assert.Equal(blocks, BlocksAt(root, InstallRoot, Connect, _ => image));
            Assert.Equal(imageInsideRoot ? StartupApplyHold.InstallFolder : StartupApplyHold.None, Program.StartupApplyHoldAt(root, InstallRoot, Connect, _ => image));
            Assert.Equal(2, connects); // the daemon was live both times: the gate decided on its image and the setting
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    /// <summary>The startup gate's reason, which Program.Main writes to debug.log once the log is up (PR #511 heads-up).</summary>
    [Fact]
    public void The_gate_says_why_it_holds_the_startup_apply()
    {
        string inside = Path.Combine(InstallRoot, "current", "Ntilde.exe");

        Assert.Equal(StartupApplyHold.None, MuxUpdateCompatibility.StartupApplyHoldFor(InstallRoot, () => false, () => Daemon, _ => inside, () => true));
        Assert.Equal(StartupApplyHold.None, MuxUpdateCompatibility.StartupApplyHoldFor(InstallRoot, () => true, () => Daemon, _ => OwnCopy, KeepOnClose));
        Assert.Equal(StartupApplyHold.InstallFolder, MuxUpdateCompatibility.StartupApplyHoldFor(InstallRoot, () => true, () => Daemon, _ => inside, NotRead));
        Assert.Equal(StartupApplyHold.UnknownImage, MuxUpdateCompatibility.StartupApplyHoldFor(InstallRoot, () => true, () => Daemon, _ => null, NotRead));
        Assert.Equal(StartupApplyHold.UnknownImage, MuxUpdateCompatibility.StartupApplyHoldFor(InstallRoot, () => true, () => null, _ => inside, NotRead));
        Assert.Equal(StartupApplyHold.PersistenceOff, MuxUpdateCompatibility.StartupApplyHoldFor(InstallRoot, () => true, () => Daemon, _ => OwnCopy, () => true));
        Assert.Equal(StartupApplyHold.PersistenceOff, MuxUpdateCompatibility.StartupApplyHoldFor(null, () => true, () => Daemon, _ => inside, () => true));

        Assert.Null(MuxUpdateCompatibility.DescribeStartupApplyHold(StartupApplyHold.None));
        Assert.Equal(
            "[Update] a staged update, if any, was not applied at startup: the multiplexer is running from the install folder, so applying it would stop every shell; the in-app update asks first",
            MuxUpdateCompatibility.DescribeStartupApplyHold(StartupApplyHold.InstallFolder));
        Assert.Equal(
            "[Update] a staged update, if any, was not applied at startup: where the multiplexer is running from could not be checked, so applying it might stop every shell; the in-app update asks first",
            MuxUpdateCompatibility.DescribeStartupApplyHold(StartupApplyHold.UnknownImage));
        Assert.Equal(
            "[Update] a staged update, if any, was not applied at startup: session persistence is off and the multiplexer is running, as before Phase 5; the in-app update asks first",
            MuxUpdateCompatibility.DescribeStartupApplyHold(StartupApplyHold.PersistenceOff));
    }

    [Fact]
    public void The_gate_finds_no_daemon_without_a_descriptor_under_the_root_whatever_the_setting()
    {
        string root = ScratchRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "settings.json"), "{\"SessionPersistence\":\"Off\"}");

            Assert.False(BlocksAt(root, null, (_, _) => throw new InvalidOperationException("nothing to connect to")));
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    private static string ScratchRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ntilde_gate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void WriteDescriptorNamingThisProcess(string root)
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        MuxDiscovery.WriteDescriptor(MuxDiscovery.GetDescriptorPath(root), new MuxEndpointDescriptor
        {
            Endpoint = MuxDiscovery.GetDefaultEndpoint(root),
            Pid = self.Id,
            ProcessName = self.ProcessName,
            StartTime = MuxDiscovery.GetProcessStartToken(self),
            MinVersion = 1,
            MaxVersion = 2,
        });
    }

    private static void DeleteQuietly(string root)
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }
}
