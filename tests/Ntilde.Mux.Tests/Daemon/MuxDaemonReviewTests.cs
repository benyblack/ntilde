using System.Net.Sockets;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Daemon;
using Ntilde.Mux.Tests.Support;

namespace Ntilde.Mux.Tests.Daemon;

/// <summary>
/// PR #489 review 2, the daemon-core rows (moved from App.Tests' <c>MuxReview2Tests</c> with the code
/// they cover, Phase 4 spec §6): serve's start failures and the <c>$APPIMAGE</c> rule.
/// </summary>
public sealed class MuxDaemonReviewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nmxr" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---- item 3: a SocketException must end `mux serve` with exit 1, not crash it.

    [Fact]
    public void A_socket_exception_is_a_daemon_start_failure()
    {
        Assert.True(MuxServeHost.IsStartFailure(new SocketException(98)));
        Assert.True(MuxServeHost.IsStartFailure(new UnauthorizedAccessException("x")));
        Assert.False(MuxServeHost.IsStartFailure(new InvalidOperationException("a bug")));
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

        Assert.False(MuxServeHost.TryStart(host, stderr, log.Add, out int exitCode));

        Assert.Equal(1, exitCode);
        Assert.Contains("Could not listen", stderr.ToString());
        Assert.Contains(log, l => l.Contains("not starting", StringComparison.Ordinal));
    }

    /// <summary>Task 22: "another multiplexer owns the lock" is its own exit code, which the launcher reads.</summary>
    [Fact]
    public void A_start_refused_by_the_lock_exits_with_the_lock_held_code()
    {
        MuxDaemonHost NewHost(int pid) => new(
            new MuxServer(new ScriptedSessionFactory(), new MuxServerOptions { ForceConPtyFiltering = false }),
            new MuxDaemonOptions
            {
                Endpoint = MuxDiscovery.GetDefaultEndpoint(_root),
                DescriptorPath = MuxDiscovery.GetDescriptorPath(_root),
                IdleExitAfter = TimeSpan.Zero,
                Pid = pid,
            });
        using MuxDaemonHost first = NewHost(Environment.ProcessId);
        Assert.True(MuxServeHost.TryStart(first, null, _ => { }, out _));
        using MuxDaemonHost second = NewHost(Environment.ProcessId + 100_000);

        Assert.False(MuxServeHost.TryStart(second, null, _ => { }, out int exitCode));

        Assert.Equal(MuxServeHost.LockHeldExitCode, exitCode);
        Assert.Equal(3, MuxServeHost.LockHeldExitCode);
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
}
