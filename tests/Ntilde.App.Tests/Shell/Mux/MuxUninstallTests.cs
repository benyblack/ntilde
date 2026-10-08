using System.Diagnostics;
using Ntilde.Mux.Contracts;
using Ntilde.Shell.Mux;

namespace Ntilde.Tests.Shell.Mux;

/// <summary>
/// Velopack's uninstall hook for the multiplexer (Phase 5 Task 21 review fix 2). Since the daemon runs from its own copy
/// outside the install root, uninstalling no longer stops it: the hook does, as <c>kill-server --force</c> would, and
/// then removes the copies. Fakes for the daemon and the file system; the hook's wiring is pinned in
/// Architecture.Tests (<c>CliCommandDispatchTests.The_uninstall_hook_stops_the_multiplexer_and_removes_its_copies</c>).
/// </summary>
public sealed class MuxUninstallTests
{
    private static readonly string DataRoot = Path.Combine(Path.GetTempPath(), "nmxu-fake", "data");
    private static readonly string Bin = Path.Combine(DataRoot, "bin");

    private static readonly MuxEndpointDescriptor Running = new() { Endpoint = "ntilde-mux-test", Pid = 4242, ProcessName = "Ntilde" };

    /// <summary>Short, so a test that waits one out stays fast.</summary>
    private static readonly MuxUninstall.Waits Short = new(
        Request: TimeSpan.FromMilliseconds(300), Shutdown: TimeSpan.FromMilliseconds(300), Terminate: TimeSpan.FromMilliseconds(300), RetryDelete: TimeSpan.FromMilliseconds(50));

    private readonly List<string> _log = new();

    private void Log(string line)
    {
        lock (_log) _log.Add(line);
    }

    private static FakeImageFileSystem WithCopies()
    {
        var fs = new FakeImageFileSystem();
        foreach (string v in new[] { "0.11.0", "0.12.0" })
        {
            fs.AddFile(Path.Combine(Bin, v, "Ntilde.exe"), "exe " + v);
            fs.AddFile(Path.Combine(Bin, v, "conpty.dll"), "conpty " + v);
            fs.AddFile(Path.Combine(Bin, v, "x64", "OpenConsole.exe"), "host " + v);
            fs.AddFile(Path.Combine(Bin, v, MuxDaemonImage.CompleteFileName), "");
        }

        fs.AddFile(Path.Combine(Bin, ".0.12.0." + Guid.NewGuid().ToString("N") + ".tmp", "Ntilde.exe"), "half");
        return fs;
    }

    private void Run(FakeDaemon daemon, FakeImageFileSystem fs) => MuxUninstall.StopDaemonAndRemoveCopies(DataRoot, daemon, fs, Log, Short);

    [Fact]
    public void With_no_daemon_running_nothing_is_asked_and_the_copies_go()
    {
        var daemon = new FakeDaemon { Live = null };
        FakeImageFileSystem fs = WithCopies();

        Run(daemon, fs);

        Assert.Equal("read", string.Join(", ", daemon.Calls));
        Assert.False(fs.DirectoryExists(Bin));
    }

    [Fact]
    public void A_daemon_that_stops_when_asked_is_not_terminated_and_its_copies_go_after_it()
    {
        FakeImageFileSystem fs = WithCopies();
        bool copiesStillThereWhileItStopped = false;
        var daemon = new FakeDaemon { Live = Running, OnWait = () => copiesStillThereWhileItStopped = fs.DirectoryExists(Bin) };

        Run(daemon, fs);

        Assert.Equal("read, shutdown, wait 4242", string.Join(", ", daemon.Calls));
        Assert.Equal(Short.Shutdown, daemon.WaitedFor);
        Assert.True(copiesStillThereWhileItStopped);
        Assert.False(fs.DirectoryExists(Bin));
    }

    [Fact]
    public void A_daemon_that_does_not_stop_in_time_is_terminated()
    {
        var daemon = new FakeDaemon { Live = Running, ExitsWhenAsked = false };
        FakeImageFileSystem fs = WithCopies();

        Run(daemon, fs);

        Assert.Equal("read, shutdown, wait 4242, terminate 4242", string.Join(", ", daemon.Calls));
        Assert.Equal(Short.Terminate, daemon.TerminateWait);
        Assert.Contains(_log, l => l.Contains("4242", StringComparison.Ordinal) && l.Contains("terminat", StringComparison.OrdinalIgnoreCase));
        Assert.False(fs.DirectoryExists(Bin));
    }

    /// <summary>Another protocol version, or not reachable: it cannot be asked, so it is terminated at once.</summary>
    [Fact]
    public void A_daemon_that_cannot_be_asked_is_terminated_without_waiting()
    {
        var daemon = new FakeDaemon { Live = Running, Shutdown = () => false };

        Run(daemon, new FakeImageFileSystem());

        Assert.Equal("read, shutdown, terminate 4242", string.Join(", ", daemon.Calls));
    }

    [Fact]
    public void A_shutdown_request_that_throws_is_logged_and_the_daemon_terminated()
    {
        var daemon = new FakeDaemon { Live = Running, Shutdown = () => throw new IOException("The pipe has been ended.") };

        Run(daemon, new FakeImageFileSystem());

        Assert.Equal("read, shutdown, terminate 4242", string.Join(", ", daemon.Calls));
        Assert.Contains(_log, l => l.Contains("The pipe has been ended.", StringComparison.Ordinal));
    }

    /// <summary>Velopack gives a fast callback about 30 s: a daemon that never answers must not hold the uninstall.</summary>
    [Fact]
    public void A_shutdown_request_that_hangs_is_given_up_on_in_time()
    {
        var released = new ManualResetEventSlim();
        var daemon = new FakeDaemon { Live = Running, Shutdown = () => { released.Wait(TimeSpan.FromSeconds(30)); return true; } };
        var clock = Stopwatch.StartNew();
        try
        {
            Run(daemon, new FakeImageFileSystem());

            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
            Assert.Contains("terminate 4242", daemon.Calls);
        }
        finally
        {
            released.Set(); // the abandoned request ends; not disposed, as its thread may still be waking
        }
    }

    [Fact]
    public void A_failed_termination_is_logged_and_the_copies_still_go_where_they_can()
    {
        var daemon = new FakeDaemon { Live = Running, ExitsWhenAsked = false, TerminateFailure = "Could not terminate pid 4242: Access is denied." };
        FakeImageFileSystem fs = WithCopies();
        fs.InUse.Add(Path.Combine(Bin, "0.12.0", "Ntilde.exe")); // still running from it

        Run(daemon, fs);

        Assert.Contains(_log, l => l.Contains("Access is denied.", StringComparison.Ordinal));
        Assert.False(fs.DirectoryExists(Path.Combine(Bin, "0.11.0")));
        Assert.True(fs.FileExists(Path.Combine(Bin, "0.12.0", "x64", "OpenConsole.exe"))); // kept whole
        Assert.True(fs.DirectoryExists(Bin));
        Assert.Contains(_log, l => l.Contains("0.12.0", StringComparison.Ordinal) && l.Contains("kept", StringComparison.Ordinal));
    }

    /// <summary>Review fix round 2: <c>bin\</c> may be a junction into a shared folder; only our copies are ever deleted.</summary>
    [Fact]
    public void Removing_the_copies_leaves_a_folder_that_is_not_a_copy_alone()
    {
        FakeImageFileSystem fs = WithCopies();
        string stranger = Path.Combine(Bin, "photos");
        string unlisted = Path.Combine(Bin, "2.0.0"); // a version's name, but no list
        fs.AddFile(Path.Combine(stranger, "keep.me"), "someone else's");
        fs.AddFile(Path.Combine(unlisted, "keep.me"), "someone else's");

        Run(new FakeDaemon { Live = null }, fs);

        Assert.False(fs.DirectoryExists(Path.Combine(Bin, "0.11.0")));
        Assert.False(fs.DirectoryExists(Path.Combine(Bin, "0.12.0")));
        Assert.True(fs.FileExists(Path.Combine(stranger, "keep.me")));
        Assert.True(fs.FileExists(Path.Combine(unlisted, "keep.me")));
        Assert.True(fs.DirectoryExists(Bin));
        Assert.Contains(_log, l => l.Contains(stranger, StringComparison.Ordinal));
    }

    /// <summary>
    /// Review fix round 2: right after a forced stop the daemon's console hosts may still be exiting, so a copy's delete can
    /// fail once on <c>x64\OpenConsole.exe</c>; uninstall tries it again after a moment instead of leaving half a copy.
    /// </summary>
    [Fact]
    public void A_copy_whose_delete_fails_once_is_tried_again()
    {
        FakeImageFileSystem fs = WithCopies();
        string host = Path.Combine(Bin, "0.12.0", "x64", "OpenConsole.exe");
        int refusals = 0;
        fs.FailDelete = path => path == host && refusals++ == 0 ? new UnauthorizedAccessException($"Access to the path '{path}' is denied.") : null;

        Run(new FakeDaemon { Live = null }, fs);

        Assert.Equal(2, refusals);
        Assert.False(fs.DirectoryExists(Path.Combine(Bin, "0.12.0")));
        Assert.False(fs.DirectoryExists(Bin));
    }

    [Fact]
    public void Removing_the_copies_never_touches_a_linked_folder()
    {
        FakeImageFileSystem fs = WithCopies();
        string linked = Path.Combine(Bin, "0.11.0");
        fs.ReparsePoints.Add(linked);

        Run(new FakeDaemon { Live = null }, fs);

        Assert.True(fs.FileExists(Path.Combine(linked, "Ntilde.exe")));
        Assert.DoesNotContain(fs.Operations, o => o.Contains(linked, StringComparison.OrdinalIgnoreCase));
        Assert.False(fs.DirectoryExists(Path.Combine(Bin, "0.12.0")));
        Assert.True(fs.DirectoryExists(Bin)); // not empty: the link is still in it
    }

    [Fact]
    public void A_linked_bin_folder_is_emptied_of_copies_but_not_itself_removed()
    {
        FakeImageFileSystem fs = WithCopies();
        fs.ReparsePoints.Add(Bin); // the user moved bin\ to another drive with a junction

        Run(new FakeDaemon { Live = null }, fs);

        Assert.True(fs.DirectoryExists(Bin));
        Assert.Empty(fs.GetDirectories(Bin));
    }

    [Fact]
    public void With_nothing_to_do_nothing_happens()
    {
        var fs = new FakeImageFileSystem();

        Run(new FakeDaemon { Live = null }, fs);

        Assert.Empty(fs.Operations);
        Assert.Empty(_log);
    }

    private sealed class FakeDaemon : IMuxUninstallDaemon
    {
        private readonly List<string> _calls = new();

        public MuxEndpointDescriptor? Live { get; init; }
        public Func<bool> Shutdown { get; init; } = () => true;
        public bool ExitsWhenAsked { get; init; } = true;
        public string? TerminateFailure { get; init; }
        public Action? OnWait { get; init; }
        public TimeSpan? WaitedFor { get; private set; }
        public TimeSpan? TerminateWait { get; private set; }

        public string[] Calls
        {
            get { lock (_calls) return _calls.ToArray(); }
        }

        private void Call(string call)
        {
            lock (_calls) _calls.Add(call);
        }

        public MuxEndpointDescriptor? ReadLive()
        {
            Call("read");
            return Live;
        }

        public bool RequestShutdown()
        {
            Call("shutdown");
            return Shutdown();
        }

        public bool WaitForExit(MuxEndpointDescriptor descriptor, TimeSpan timeout)
        {
            Call($"wait {descriptor.Pid}");
            WaitedFor = timeout;
            OnWait?.Invoke();
            return ExitsWhenAsked;
        }

        public bool Terminate(MuxEndpointDescriptor descriptor, TimeSpan wait, out string? failure)
        {
            Call($"terminate {descriptor.Pid}");
            TerminateWait = wait;
            failure = TerminateFailure;
            return failure is null;
        }
    }
}
