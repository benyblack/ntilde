using System.Net.Sockets;
using Ntilde.Pty;
using Ntilde.Mux;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Shell;
using Ntilde.Shell.Mux;

namespace Ntilde.Tests.Shell.Mux;

/// <summary>PR #489 review 2: the app-side pieces that have no better home.</summary>
public sealed class MuxReview2Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nmxr" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---- item 3: a SocketException must end `mux serve` / a verb with exit 1, not crash it.

    [Fact]
    public void A_socket_exception_is_a_reportable_verb_failure()
    {
        Assert.True(MuxCommand.IsReportableFailure(new SocketException(98)));
        Assert.True(MuxCommand.IsReportableFailure(new IOException("x")));
        Assert.False(MuxCommand.IsReportableFailure(new InvalidOperationException("a bug")));
    }

    [Fact]
    public void A_socket_exception_is_a_daemon_start_failure()
    {
        Assert.True(MuxDaemonProcess.IsStartFailure(new SocketException(98)));
        Assert.True(MuxDaemonProcess.IsStartFailure(new UnauthorizedAccessException("x")));
        Assert.False(MuxDaemonProcess.IsStartFailure(new InvalidOperationException("a bug")));
    }

    [Fact]
    public void A_listener_that_fails_with_a_socket_error_makes_serve_report_and_return_false()
    {
        var server = new MuxServer(new ScriptedSessionFactory(), new MuxServerOptions { ForceConPtyFiltering = false });
        using var host = new MuxDaemonHost(server, new MuxDaemonOptions
        {
            Endpoint = MuxDiscovery.GetDefaultEndpoint(_root),
            DescriptorPath = MuxDiscovery.GetDescriptorPath(_root),
            IdleExitAfter = TimeSpan.Zero,
            ListenerFactory = _ => throw new SocketException(98 /* EADDRINUSE */),
        });
        var stderr = new StringWriter();
        var log = new List<string>();

        Assert.False(MuxDaemonProcess.TryStart(host, stderr, log.Add));

        Assert.Contains("Could not listen", stderr.ToString());
        Assert.Contains(log, l => l.Contains("not starting", StringComparison.Ordinal));
    }

    // ---- item 5: $APPIMAGE is only trusted when we really run from inside $APPDIR.

    private static readonly string AppDir = Path.Combine(Path.GetTempPath(), ".mount_ntildeXYZ");
    private static readonly string InsideExe = Path.Combine(AppDir, "usr", "bin", "Ntilde");
    private static readonly string AppImage = Path.Combine(Path.GetTempPath(), "Ntilde.AppImage");
    private static readonly string OutsideExe = Path.Combine(Path.GetTempPath(), "elsewhere", "Ntilde");

    [Fact]
    public void AppImage_is_used_when_the_process_runs_inside_APPDIR()
    {
        Assert.Equal(AppImage, ProcessMuxDaemonSpawner.ResolveDaemonExecutable(AppImage, AppDir, InsideExe, _ => true));
        Assert.Equal(AppImage, ProcessMuxDaemonSpawner.ResolveDaemonExecutable(AppImage, AppDir + Path.DirectorySeparatorChar, InsideExe, _ => true));
    }

    [Theory]
    [InlineData("no-appdir")]
    [InlineData("outside-appdir")]
    [InlineData("sibling-prefix")]
    [InlineData("appimage-missing")]
    [InlineData("no-appimage")]
    public void AppImage_is_ignored_unless_it_is_really_ours(string kind)
    {
        (string? appImage, string? appDir, string process, bool exists) = kind switch
        {
            "no-appdir" => ((string?)AppImage, (string?)null, InsideExe, true),
            "outside-appdir" => (AppImage, AppDir, OutsideExe, true),
            // "/tmp/.mount_ntildeXYZ-evil/..." starts with "/tmp/.mount_ntildeXYZ" but is not under it.
            "sibling-prefix" => (AppImage, AppDir, Path.Combine(AppDir + "-evil", "Ntilde"), true),
            "appimage-missing" => (AppImage, AppDir, InsideExe, false),
            _ => (null, AppDir, InsideExe, true),
        };

        Assert.Equal(process, ProcessMuxDaemonSpawner.ResolveDaemonExecutable(appImage, appDir, process, _ => exists));
    }

    [Fact]
    public void APPDIR_matching_is_case_sensitive_on_linux()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux paths are case-sensitive; Windows/macOS are not.");
        string upperDir = AppDir.ToUpperInvariant();
        Assert.Equal(InsideExe, ProcessMuxDaemonSpawner.ResolveDaemonExecutable(AppImage, upperDir, InsideExe, _ => true));
    }

    // ---- item 6: one daemon session id per restored pane.

    private static PaneNode Leaf(string? muxId) => new() { Type = NodeType.Leaf, MuxSessionId = muxId, MuxEndpoint = muxId is null ? null : "ep" };

    [Fact]
    public void A_duplicated_mux_id_is_kept_only_on_its_first_pane()
    {
        string a = Guid.NewGuid().ToString("D");
        string b = Guid.NewGuid().ToString("D");
        PaneNode first = Leaf(a), splitDup = Leaf(a), other = Leaf(b), secondTabDup = Leaf(a), bDup = Leaf(b.ToUpperInvariant());
        var session = new NtildeSession
        {
            Tabs =
            {
                new TabSession { Root = new PaneNode { Type = NodeType.Split, Children = { first, splitDup, other } } },
                new TabSession { Root = new PaneNode { Type = NodeType.Split, Children = { secondTabDup, bDup, Leaf(null) } } },
            },
        };

        SessionManager.DedupeMuxIds(session);

        Assert.Equal(a, first.MuxSessionId);
        Assert.Equal("ep", first.MuxEndpoint);
        Assert.Equal(b, other.MuxSessionId);
        Assert.Null(splitDup.MuxSessionId);
        Assert.Null(splitDup.MuxEndpoint);
        Assert.Null(secondTabDup.MuxSessionId); // across tabs too: one restore pass is the whole file
        Assert.Null(bDup.MuxSessionId);         // the same id in another spelling is the same session
    }
}
