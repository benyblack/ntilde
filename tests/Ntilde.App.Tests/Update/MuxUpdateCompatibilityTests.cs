using System.Diagnostics;
using Ntilde.Mux.Contracts;
using Ntilde.Update;
using Velopack;

namespace Ntilde.Tests.Update;

/// <summary>
/// Phase 5 R10: the release names its multiplexer protocol range in Velopack's release notes, as the marker line
/// <c>&lt;!-- ntilde-mux-protocol: &lt;min&gt;-&lt;max&gt; --&gt;</c>, and applying an update keeps the running daemon only
/// when the ranges overlap and the apply does not kill it - its image is outside the install root (R9).
/// </summary>
public sealed class MuxUpdateCompatibilityTests
{
    private static readonly string Base = OperatingSystem.IsWindows() ? @"C:\Users\u\AppData\Local" : "/home/u/.local/share";
    private static readonly string InstallRoot = Path.Combine(Base, "NtildeApp");

    public enum Image
    {
        /// <summary><c>&lt;root&gt;\current\Ntilde.exe</c>: a daemon a pre-Phase-5 build started.</summary>
        InsideCurrent,

        /// <summary>Another folder under the root: the apply kills the whole root, not only <c>current\</c>.</summary>
        InsideAnotherFolder,

        /// <summary>The daemon's own copy, <c>&lt;app-data&gt;\bin\&lt;version&gt;\Ntilde.exe</c>.</summary>
        OwnCopy,

        /// <summary>A sibling whose name starts with the root's: outside it.</summary>
        SiblingSharingThePrefix,

        /// <summary>The image could not be read.</summary>
        Unknown,
    }

    private static string? PathOf(Image image) => image switch
    {
        Image.InsideCurrent => Path.Combine(InstallRoot, "current", "Ntilde.exe"),
        Image.InsideAnotherFolder => Path.Combine(InstallRoot, "packages", "Ntilde.exe"),
        Image.OwnCopy => Path.Combine(Base, "ntilde", "bin", "0.12.0", "Ntilde.exe"),
        Image.SiblingSharingThePrefix => Path.Combine(Base, "NtildeApp2", "Ntilde.exe"),
        _ => null,
    };

    // ---- the marker

    [Theory]
    [InlineData("<!-- ntilde-mux-protocol: 1-2 -->", 1, 2)]
    [InlineData("Fixes and features.\n\n<!-- ntilde-mux-protocol: 1-2 -->", 1, 2)]
    [InlineData("<!-- ntilde-mux-protocol: 3-7 -->\n", 3, 7)]
    [InlineData("<!-- ntilde-mux-protocol: 2-2 -->", 2, 2)]
    public void A_marker_line_gives_its_range(string notes, int min, int max)
    {
        Assert.Equal((min, max), MuxUpdateCompatibility.ParseProtocolRange(notes));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Fixes and features.")]
    [InlineData("<!-- another comment -->")]
    [InlineData("ntilde-mux-protocol: 1-2")]
    [InlineData("<!-- ntilde-mux-protocol: 1-2")]
    public void Notes_without_a_marker_give_no_range(string? notes)
    {
        Assert.Null(MuxUpdateCompatibility.ParseProtocolRange(notes));
    }

    [Theory]
    [InlineData("<!-- ntilde-mux-protocol: 1 -->")]
    [InlineData("<!-- ntilde-mux-protocol: -->")]
    [InlineData("<!-- ntilde-mux-protocol: a-b -->")]
    [InlineData("<!-- ntilde-mux-protocol: 1-2-3 -->")]
    [InlineData("<!-- ntilde-mux-protocol: 3-2 -->")]
    [InlineData("<!-- ntilde-mux-protocol: 0-2 -->")]
    [InlineData("<!-- ntilde-mux-protocol: -1-2 -->")]
    [InlineData("<!-- ntilde-mux-protocol: 1.5-2 -->")]
    [InlineData("<!-- ntilde-mux-protocol: 99999999999-99999999999 -->")]
    [InlineData("<!-- ntilde-mux-protocol: 1-\n2 -->")]
    [InlineData("<!-- ntilde-mux-protocol: \u0661-\u0662 -->")]
    public void A_malformed_marker_gives_no_range(string notes)
    {
        Assert.Null(MuxUpdateCompatibility.ParseProtocolRange(notes));
    }

    /// <summary>The first marker decides, even a malformed one: a later line never overrides it.</summary>
    [Fact]
    public void The_first_marker_wins()
    {
        Assert.Equal((3, 4), MuxUpdateCompatibility.ParseProtocolRange("<!-- ntilde-mux-protocol: 3-4 -->\n<!-- ntilde-mux-protocol: 1-2 -->"));
        Assert.Null(MuxUpdateCompatibility.ParseProtocolRange("<!-- ntilde-mux-protocol: x-y -->\n<!-- ntilde-mux-protocol: 1-2 -->"));
    }

    [Theory]
    [InlineData("<!--ntilde-mux-protocol:1-2-->")]
    [InlineData("<!--   ntilde-mux-protocol :  1 - 2   -->")]
    [InlineData("  \t<!-- ntilde-mux-protocol: 1-2 -->  ")]
    [InlineData("<!--\tntilde-mux-protocol:\t1-2\t-->")]
    [InlineData("Notes.\r\n\r\n<!-- ntilde-mux-protocol: 1-2 -->\r\n")]
    public void Whitespace_around_and_inside_the_marker_is_ignored(string notes)
    {
        Assert.Equal((1, 2), MuxUpdateCompatibility.ParseProtocolRange(notes));
    }

    // ---- whether the update keeps the daemon (the daemon speaks 1-2)

    [Theory]
    // An install root: kept only when the ranges overlap (or the new one is unknown) AND the image is known to be outside.
    [InlineData(2, 3, Image.OwnCopy, true, true)]
    [InlineData(1, 2, Image.OwnCopy, true, true)]
    [InlineData(null, null, Image.OwnCopy, true, true)]
    [InlineData(2, 3, Image.SiblingSharingThePrefix, true, true)]
    [InlineData(3, 4, Image.OwnCopy, true, false)]
    [InlineData(2, 3, Image.InsideCurrent, true, false)]
    [InlineData(2, 3, Image.InsideAnotherFolder, true, false)]
    [InlineData(null, null, Image.InsideCurrent, true, false)]
    [InlineData(3, 4, Image.InsideCurrent, true, false)]
    [InlineData(2, 3, Image.Unknown, true, false)]
    [InlineData(null, null, Image.Unknown, true, false)]
    [InlineData(3, 4, Image.Unknown, true, false)]
    // No install root (macOS, Linux, a portable or dev run): the apply kills nothing, so the ranges alone decide.
    [InlineData(2, 3, Image.Unknown, false, true)]
    [InlineData(null, null, Image.Unknown, false, true)]
    [InlineData(2, 3, Image.InsideCurrent, false, true)]
    [InlineData(3, 4, Image.Unknown, false, false)]
    [InlineData(3, 4, Image.OwnCopy, false, false)]
    public void The_update_keeps_the_daemon_only_when_it_can_speak_to_it_and_the_apply_cannot_kill_it(
        int? newMin, int? newMax, Image image, bool installRoot, bool kept)
    {
        (int, int)? newBuild = newMin is int min && newMax is int max ? (min, max) : null;

        Assert.Equal(kept, MuxUpdateCompatibility.KeepsDaemon((1, 2), newBuild, PathOf(image), installRoot ? InstallRoot : null));
    }

    [Fact]
    public void A_root_given_with_a_trailing_separator_still_contains_its_folders()
    {
        Assert.False(MuxUpdateCompatibility.KeepsDaemon((1, 2), null, PathOf(Image.InsideCurrent), InstallRoot + Path.DirectorySeparatorChar));
        Assert.True(MuxUpdateCompatibility.KeepsDaemon((1, 2), null, PathOf(Image.OwnCopy), InstallRoot + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void On_windows_an_image_differing_from_the_root_only_in_case_is_inside_it()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows paths are case-insensitive; the install root exists only there.");

        Assert.False(MuxUpdateCompatibility.KeepsDaemon((1, 2), null, PathOf(Image.InsideCurrent)!.ToUpperInvariant(), InstallRoot.ToLowerInvariant()));
    }

    // ---- the daemon's image, from its descriptor

    [Fact]
    public void This_process_is_never_taken_for_the_daemon()
    {
        using Process self = Process.GetCurrentProcess();
        var descriptor = new MuxEndpointDescriptor
        {
            Endpoint = "test",
            Pid = self.Id,
            ProcessName = self.ProcessName,
            StartTime = MuxDiscovery.GetProcessStartToken(self),
        };

        Assert.Null(MuxUpdateCompatibility.DaemonImagePath(descriptor));
    }

    [Fact]
    public void A_pid_that_names_no_process_has_no_image()
    {
        var descriptor = new MuxEndpointDescriptor { Endpoint = "test", Pid = int.MaxValue, ProcessName = "Ntilde" };

        Assert.Null(MuxUpdateCompatibility.DaemonImagePath(descriptor));
    }

    /// <summary>
    /// The image is read from the very process the descriptor names, and only once it is shown to be that process: the
    /// same name and start time. A recycled pid (another name, or the same name started later) has no image - unknown,
    /// which the update treats as inside the install root.
    /// </summary>
    [Fact]
    public void The_image_is_read_only_from_the_process_the_descriptor_describes()
    {
        Assert.SkipWhen(OperatingSystem.IsMacOS(), "Process.MainModule is not read on macOS, where no install root is ever killed.");
        using Process child = StartSleeper();
        try
        {
            WaitUntilItsImageCanBeRead(child);
            long? start = MuxDiscovery.GetProcessStartToken(child);
            var described = new MuxEndpointDescriptor { Endpoint = "test", Pid = child.Id, ProcessName = child.ProcessName, StartTime = start };

            string? image = MuxUpdateCompatibility.DaemonImagePath(described);

            Assert.NotNull(image);
            Assert.True(Path.IsPathFullyQualified(image), image);
            Assert.Equal(child.ProcessName, Path.GetFileNameWithoutExtension(image), ignoreCase: true);
            Assert.Null(MuxUpdateCompatibility.DaemonImagePath(described with { ProcessName = "Ntilde" }));
            Assert.Null(MuxUpdateCompatibility.DaemonImagePath(described with { StartTime = start + (10 * TimeSpan.TicksPerSecond) }));
        }
        finally
        {
            try { child.Kill(); } catch (InvalidOperationException) { /* already gone */ }
        }
    }

    /// <summary>
    /// Right after it starts, a process's module list cannot be read yet (Windows: the loader has not built it, and
    /// EnumProcessModules fails), so the image is honestly unknown for a moment. A daemon is long-running; the test waits
    /// for the same state, through its own handle, before asking.
    /// </summary>
    private static void WaitUntilItsImageCanBeRead(Process process)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                if (!string.IsNullOrEmpty(process.MainModule?.FileName)) return;
            }
            catch (System.ComponentModel.Win32Exception) when (deadline.Elapsed < TimeSpan.FromSeconds(10))
            {
                // Not yet.
            }

            if (deadline.Elapsed >= TimeSpan.FromSeconds(10)) Assert.Fail("the sleeper's image never became readable");
            Thread.Sleep(20);
        }
    }

    private static Process StartSleeper()
    {
        var info = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1")
            : new ProcessStartInfo("sleep", "30");
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardOutput = true;
        return Process.Start(info) ?? throw new InvalidOperationException("the sleeper did not start");
    }

    // ---- where the notes come from

    [Fact]
    public async Task The_coordinator_hands_on_the_release_notes_only_while_an_update_is_staged()
    {
        var service = new NotesUpdateService { Notes = "<!-- ntilde-mux-protocol: 1-2 -->" };
        var coordinator = new UpdateCoordinator(service, () => true, _ => { }, _ => { });
        Assert.Null(coordinator.StagedReleaseNotes);

        await coordinator.RunManualCheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal("<!-- ntilde-mux-protocol: 1-2 -->", coordinator.StagedReleaseNotes);
    }

    [Fact]
    public void The_velopack_service_reads_the_notes_of_the_release_it_downloaded()
    {
        var service = new VelopackUpdateService(VelopackUpdateService.DefaultRepoUrl, _ => { });
        Assert.Null(service.StagedReleaseNotes);

        var release = new VelopackAsset { PackageId = "NtildeApp", NotesMarkdown = "Notes.\n\n<!-- ntilde-mux-protocol: 1-2 -->" };
        typeof(VelopackUpdateService).GetField("_downloaded", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(service, new UpdateInfo(release, false, null, []));

        Assert.Equal("Notes.\n\n<!-- ntilde-mux-protocol: 1-2 -->", service.StagedReleaseNotes);
    }

    private sealed class NotesUpdateService : IUpdateService
    {
        public string? Notes { get; init; }
        public bool IsSupported => true;
        public string? StagedReleaseNotes => Notes;
        public Task<UpdateAvailability> CheckAndDownloadAsync(CancellationToken ct) => Task.FromResult(new UpdateAvailability(true, "99.0.0"));
        public void ApplyAndRestart() { }
    }
}
