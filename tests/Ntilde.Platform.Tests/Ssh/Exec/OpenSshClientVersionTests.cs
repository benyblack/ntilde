using System.Diagnostics;
using Ntilde.Platform.Ssh.Exec;
using Ntilde.Platform.Ssh.Launch;

namespace Ntilde.Platform.Tests.Ssh.Exec;

/// <summary>
/// The OpenSSH client's version (<see cref="OpenSshClientVersion"/>): what <c>ssh -V</c> prints, read; the probe that runs
/// it, against a real process; and the cache that runs the probe once per executable and last-write time
/// (<see cref="OpenSshClientVersionCache"/>). Before 8.4 a keyboard-interactive prompt does not say which user@host it is
/// for, so the vault-only askpass cannot recognise the target's password in it.
/// </summary>
public sealed class OpenSshClientVersionTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData("OpenSSH_for_Windows_8.1p1, LibreSSL 3.0.2", 8, 1)]                          // Windows 10's inbox client
    [InlineData("OpenSSH_8.2p1 Ubuntu-4ubuntu0.11, OpenSSL 1.1.1f  31 Mar 2020", 8, 2)]      // Ubuntu 20.04
    [InlineData("OpenSSH_9.5p1, LibreSSL 3.8.2", 9, 5)]
    [InlineData("OpenSSH_for_Windows_9.5p2, LibreSSL 3.8.2\r\n", 9, 5)]
    [InlineData("OpenSSH_10.0p2, OpenSSL 3.2.4 11 Feb 2025\n", 10, 0)]
    [InlineData("OpenSSH_8.4p1 Debian-5+deb11u3, OpenSSL 1.1.1w  11 Sep 2023", 8, 4)]
    [InlineData("OpenSSH_8.3", 8, 3)]
    [InlineData("OpenSSH_8.4p1-hpn15v2, OpenSSL 1.1.1k", 8, 4)]
    [InlineData("ssh: warning: no version information available\nOpenSSH_9.6p1 Ubuntu-3ubuntu13.5, OpenSSL 3.0.13 30 Jan 2024", 9, 6)]
    public void Parse_reads_the_clients_version(string output, int major, int minor)
    {
        Assert.Equal(new Version(major, minor), OpenSshClientVersion.Parse(output));
    }

    /// <summary>Anything else is no version, which callers treat as a client too old to trust (fail closed).</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n")]
    [InlineData("Sun_SSH_1.1, SSH protocols 1.5/2.0, OpenSSL 0x0090704f")]
    [InlineData("dropbear v2022.83")]
    [InlineData("ssh: command not found")]
    [InlineData("OpenSSH_")]
    [InlineData("OpenSSH_8")]
    [InlineData("OpenSSH_8.")]
    [InlineData("OpenSSH_x.y")]
    [InlineData("OpenSSH_8.4.1")]
    [InlineData("OpenSSH_for_Windows_")]
    [InlineData("openssh_9.5p1")]
    [InlineData("Version: OpenSSH_9.5p1")]
    [InlineData("OpenSSH_99999999999.1")]
    public void Parse_finds_no_version_in_anything_else(string? output)
    {
        Assert.Null(OpenSshClientVersion.Parse(output));
    }

    [Theory]
    [InlineData("8.1", false)]
    [InlineData("8.2", false)]
    [InlineData("8.3", false)]
    [InlineData("7.4", false)]
    [InlineData("8.4", true)]
    [InlineData("8.9", true)]
    [InlineData("9.5", true)]
    [InlineData("10.0", true)]
    [InlineData(null, false)]
    public void Keyboard_interactive_prompts_name_the_target_from_8_4(string? version, bool prefixes)
    {
        Assert.Equal(prefixes, OpenSshClientVersion.PrefixesKeyboardInteractivePrompts(version is null ? null : Version.Parse(version)));
    }

    private static (string Shell, string[] Leading) Shell() => OperatingSystem.IsWindows()
        ? (Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } comSpec ? comSpec : "cmd.exe", new[] { "/d", "/c" })
        : ("/bin/sh", new[] { "-c" });

    private static string Pick(string windows, string unix) => OperatingSystem.IsWindows() ? windows : unix;

    /// <summary>The probe, with the platform shell standing in for ssh and running <paramref name="script"/> instead of <c>-V</c>.</summary>
    private static OpenSshClientProbe ProbeShell(string script, TimeSpan timeout, TimeProvider? time = null, Func<ProcessStartInfo, Process?>? start = null)
    {
        (string shell, string[] leading) = Shell();
        return OpenSshClientVersion.Probe(shell, [.. leading, script], timeout, time, start);
    }

    /// <summary>ssh -V writes its version to stderr.</summary>
    [Fact]
    public void The_probe_reads_the_version_ssh_writes_to_stderr()
    {
        OpenSshClientProbe probe = ProbeShell(
            Pick("echo OpenSSH_for_Windows_8.1p1, LibreSSL 3.0.2 1>&2", "echo 'OpenSSH_8.2p1 Ubuntu-4ubuntu0.11, OpenSSL 1.1.1f  31 Mar 2020' >&2"),
            Bound);

        Assert.Equal(OpenSshClientProbeKind.Version, probe.Kind);
        Assert.Equal(Pick("8.1", "8.2"), probe.Version?.ToString());
    }

    /// <summary>
    /// R4-b: nothing the probe runs can wait on a prompt. Its stdin is closed at once: a stand-in that reads a line first
    /// gets end-of-file and goes on, instead of waiting out the timeout.
    /// </summary>
    [Fact]
    public void The_probes_stdin_is_closed_so_nothing_waits_on_it()
    {
        var timer = Stopwatch.StartNew();
        OpenSshClientProbe probe = ProbeShell(
            Pick("set /p line= & echo OpenSSH_9.5p1, LibreSSL 3.8.2 1>&2", "read line; echo 'OpenSSH_9.5p1, LibreSSL 3.8.2' >&2"),
            Bound);

        Assert.Equal(new Version(9, 5), probe.Version);
        Assert.True(timer.Elapsed < Bound, $"took {timer.Elapsed}");
    }

    /// <summary>
    /// R4-a, R4-b: a client that does not answer within the timeout is stopped. That says nothing about the client - a
    /// loaded machine just woken from sleep - so the answer is indeterminate, with why.
    /// </summary>
    [Fact]
    public void A_probe_that_runs_past_its_timeout_is_stopped_and_indeterminate()
    {
        var timer = Stopwatch.StartNew();
        OpenSshClientProbe probe = ProbeShell(
            Pick("ping -n 60 127.0.0.1 >nul & echo OpenSSH_9.5p1 1>&2", "sleep 60; echo OpenSSH_9.5p1 >&2"),
            TimeSpan.FromMilliseconds(500));

        Assert.Equal(OpenSshClientProbeKind.Indeterminate, probe.Kind);
        Assert.Null(probe.Version);
        Assert.Contains("did not exit within", probe.Reason, StringComparison.Ordinal);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(15), $"the probe took {timer.Elapsed}");
    }

    /// <summary>
    /// The timeout counts from the moment the process runs, not from before its start: a slow exec (an antivirus scan of
    /// ssh.exe on its first run) is not the client being slow to answer. The clock here moves a minute while the process
    /// starts, which would use up the whole budget if the start counted.
    /// </summary>
    [Fact]
    public void A_slow_start_does_not_count_against_the_probes_timeout()
    {
        var clock = new ManualClock();
        OpenSshClientProbe probe = ProbeShell(
            Pick("echo OpenSSH_9.5p1, LibreSSL 3.8.2 1>&2", "echo 'OpenSSH_9.5p1, LibreSSL 3.8.2' >&2"),
            Bound,
            clock,
            startInfo =>
            {
                clock.Advance(TimeSpan.FromMinutes(1));
                return Process.Start(startInfo);
            });

        Assert.Equal(OpenSshClientProbeKind.Version, probe.Kind);
        Assert.Equal(new Version(9, 5), probe.Version);
    }

    /// <summary>R4-a: ssh -V exits 0; a run that exits otherwise is not an OpenSSH client this can read, whatever it printed.</summary>
    [Fact]
    public void A_probe_that_exits_with_an_error_is_definitively_not_OpenSSH()
    {
        OpenSshClientProbe probe = ProbeShell(Pick("echo OpenSSH_9.5p1 1>&2 & exit /b 1", "echo OpenSSH_9.5p1 >&2; exit 1"), Bound);

        Assert.Equal(OpenSshClientProbeKind.NotOpenSsh, probe.Kind);
        Assert.True(probe.IsDefinitive);
        Assert.Equal("it exited with code 1", probe.Reason);
    }

    /// <summary>R4-a: another client (here, one that prints Dropbear's version) ran and answered: definitively not OpenSSH.</summary>
    [Fact]
    public void A_probe_that_prints_no_OpenSSH_version_is_definitively_not_OpenSSH()
    {
        OpenSshClientProbe probe = ProbeShell(Pick("echo dropbear v2022.83 1>&2", "echo 'dropbear v2022.83' >&2"), Bound);

        Assert.Equal(OpenSshClientProbeKind.NotOpenSsh, probe.Kind);
        Assert.Equal("it printed no OpenSSH version", probe.Reason);
    }

    /// <summary>R4-a: no executable there, or one that cannot start, is no version - indeterminate, not an exception.</summary>
    [Fact]
    public void A_client_that_cannot_start_is_indeterminate()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"ntilde-no-ssh-{Guid.NewGuid():N}", OperatingSystem.IsWindows() ? "ssh.exe" : "ssh");

        OpenSshClientProbe probe = OpenSshClientVersion.Probe(missing);

        Assert.Equal(OpenSshClientProbeKind.Indeterminate, probe.Kind);
        Assert.StartsWith("it could not start", probe.Reason, StringComparison.Ordinal);
    }

    /// <summary>The real client, where there is one: its own <c>-V</c> output parses.</summary>
    [Fact]
    public void The_real_ssh_has_a_version()
    {
        string? ssh;
        try
        {
            ssh = SshLaunchPlanner.ResolveSshExecutablePath();
        }
        catch (FileNotFoundException)
        {
            ssh = null;
        }

        Assert.SkipUnless(ssh is not null, "No OpenSSH client (ssh) on this machine.");
        OpenSshClientProbe probe = OpenSshClientVersion.Probe(ssh!);
        TestContext.Current.TestOutputHelper?.WriteLine($"{ssh}: {probe}");

        Assert.Equal(OpenSshClientProbeKind.Version, probe.Kind);
        Assert.NotNull(probe.Version);
    }

    private static readonly DateTime Monday = new(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>The cache over a counting probe that answers the next of <paramref name="answers"/> (the last once they run out).</summary>
    private static OpenSshClientVersionCache Counting(List<string> probed, Func<string, DateTime> stamps, params OpenSshClientProbe[] answers)
    {
        if (answers.Length == 0) answers = [OpenSshClientProbe.Found(new Version(9, 5))];
        return new(
            path =>
            {
                lock (probed)
                {
                    probed.Add(path);
                    return answers[Math.Min(probed.Count - 1, answers.Length - 1)];
                }
            },
            stamps);
    }

    /// <summary>Once per plan: one probe per executable and last-write time, and only the first lookup probed it.</summary>
    [Fact]
    public void The_cache_probes_an_executable_once_until_it_changes()
    {
        var probed = new List<string>();
        DateTime stamp = Monday;
        OpenSshClientVersionCache cache = Counting(probed, _ => stamp, OpenSshClientProbe.Found(new Version(8, 1)));
        string ssh = Path.Combine(Path.GetTempPath(), "bin", "ssh");

        OpenSshClientProbe first = cache.Lookup(ssh, out bool firstProbed);
        OpenSshClientProbe again = cache.Lookup(ssh, out bool againProbed);
        stamp = Monday.AddDays(1);   // upgraded in place
        OpenSshClientProbe upgraded = cache.Lookup(ssh, out bool upgradedProbed);
        OpenSshClientProbe afterUpgrade = cache.Lookup(ssh, out bool afterUpgradeProbed);

        Assert.Equal(new[] { ssh, ssh }, probed);
        Assert.Equal(new Version(8, 1), first.Version);
        Assert.Equal(first, again);
        Assert.Equal(first, upgraded);
        Assert.Equal(first, afterUpgrade);
        Assert.Equal((true, false, true, false), (firstProbed, againProbed, upgradedProbed, afterUpgradeProbed));
    }

    /// <summary>A definitive "not OpenSSH" holds for the executable as it is: kept, like a version.</summary>
    [Fact]
    public void A_definitive_not_OpenSSH_answer_is_kept()
    {
        var probed = new List<string>();
        OpenSshClientVersionCache cache = Counting(probed, _ => Monday, OpenSshClientProbe.NotOpenSsh("it exited with code 1"), OpenSshClientProbe.Found(new Version(9, 5)));
        string ssh = Path.Combine(Path.GetTempPath(), "bin", "ssh");

        OpenSshClientProbe first = cache.Lookup(ssh, out _);
        OpenSshClientProbe again = cache.Lookup(ssh, out bool againProbed);

        Assert.Equal(OpenSshClientProbeKind.NotOpenSsh, first.Kind);
        Assert.Equal(first, again);
        Assert.False(againProbed);
        Assert.Single(probed);
    }

    /// <summary>
    /// An indeterminate answer (a timeout, a start that failed) fails closed for the lookup that got it, and is not kept:
    /// the next lookup probes again, and a healthy client's version then comes back.
    /// </summary>
    [Fact]
    public void An_indeterminate_answer_is_not_kept_and_the_next_lookup_probes_again()
    {
        var probed = new List<string>();
        OpenSshClientVersionCache cache = Counting(
            probed, _ => Monday, OpenSshClientProbe.Indeterminate("it did not exit within 5 s"), OpenSshClientProbe.Found(new Version(9, 5)));
        string ssh = Path.Combine(Path.GetTempPath(), "bin", "ssh");

        OpenSshClientProbe first = cache.Lookup(ssh, out bool firstProbed);
        OpenSshClientProbe second = cache.Lookup(ssh, out bool secondProbed);
        OpenSshClientProbe third = cache.Lookup(ssh, out bool thirdProbed);

        Assert.Equal((OpenSshClientProbeKind.Indeterminate, "it did not exit within 5 s"), (first.Kind, first.Reason));
        Assert.Equal(new Version(9, 5), second.Version);
        Assert.Equal(second, third);
        Assert.Equal((true, true, false), (firstProbed, secondProbed, thirdProbed));
        Assert.Equal(2, probed.Count);
    }

    /// <summary>Each executable has its own version: a second ssh on the PATH is probed on its own.</summary>
    [Fact]
    public void The_cache_probes_each_executable()
    {
        var probed = new List<string>();
        OpenSshClientVersionCache cache = Counting(probed, _ => Monday);
        string one = Path.Combine(Path.GetTempPath(), "one", "ssh");
        string two = Path.Combine(Path.GetTempPath(), "two", "ssh");

        cache.Lookup(one, out _);
        cache.Lookup(two, out _);
        cache.Lookup(one, out _);

        Assert.Equal(new[] { one, two }, probed);
    }

    /// <summary>The probe runs the path the attempt runs; the cache keys it by its full path, so spellings of one file share it.</summary>
    [Fact]
    public void The_cache_keys_by_full_path_and_probes_the_path_as_given()
    {
        var probed = new List<string>();
        OpenSshClientVersionCache cache = Counting(probed, _ => Monday);
        string direct = Path.Combine(Path.GetTempPath(), "bin", "ssh");
        string roundabout = Path.Combine(Path.GetTempPath(), "bin", "..", "bin", "ssh");

        cache.Lookup(direct, out bool directProbed);
        cache.Lookup(roundabout, out bool roundaboutProbed);

        Assert.Equal(new[] { direct }, probed);
        Assert.Equal((true, false), (directProbed, roundaboutProbed));
    }

    /// <summary>
    /// Hosts reconnecting at once share one probe in flight: the others wait for its answer instead of running their own,
    /// and only the lookup that started it says it probed. That holds for an indeterminate answer too; the lookup after
    /// them probes again.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Concurrent_lookups_share_one_probe(bool definitive)
    {
        var probed = new List<string>();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        OpenSshClientProbe answer = definitive ? OpenSshClientProbe.Found(new Version(9, 5)) : OpenSshClientProbe.Indeterminate("it did not exit within 5 s");
        var cache = new OpenSshClientVersionCache(
            path =>
            {
                lock (probed) probed.Add(path);
                started.Set();
                release.Wait(Bound);
                return answer;
            },
            _ => Monday);
        string ssh = Path.Combine(Path.GetTempPath(), "bin", "ssh");
        int probers = 0;

        Task<OpenSshClientProbe>[] lookups = [.. Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            OpenSshClientProbe probe = cache.Lookup(ssh, out bool probedHere);
            if (probedHere) Interlocked.Increment(ref probers);
            return probe;
        }))];
        Assert.True(started.Wait(Bound, TestContext.Current.CancellationToken), "the probe started");
        await Task.Delay(100, TestContext.Current.CancellationToken);   // let the other lookups find it in flight
        release.Set();
        OpenSshClientProbe[] answers = await Task.WhenAll(lookups).WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.All(answers, a => Assert.Equal(answer, a));
        Assert.Single(probed);
        Assert.Equal(1, probers);

        cache.Lookup(ssh, out bool laterProbed);
        Assert.Equal(!definitive, laterProbed);
    }

    /// <summary>R4-a: a probe that throws is indeterminate, with why, and is probed again next time.</summary>
    [Fact]
    public void A_probe_that_throws_is_indeterminate()
    {
        int probes = 0;
        var cache = new OpenSshClientVersionCache(
            _ =>
            {
                probes++;
                throw new InvalidOperationException("the probe broke");
            },
            _ => Monday);
        string ssh = Path.Combine(Path.GetTempPath(), "bin", "ssh");

        OpenSshClientProbe first = cache.Lookup(ssh, out _);
        OpenSshClientProbe second = cache.Lookup(ssh, out _);

        Assert.Equal(OpenSshClientProbeKind.Indeterminate, first.Kind);
        Assert.Contains("the probe broke", first.Reason, StringComparison.Ordinal);
        Assert.Equal(first, second);
        Assert.Equal(2, probes);
    }

    /// <summary>A clock that moves only when told to.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }
}
