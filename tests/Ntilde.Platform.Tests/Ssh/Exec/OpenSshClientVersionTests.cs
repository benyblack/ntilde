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
    private static Version? ProbeShell(string script, TimeSpan timeout)
    {
        (string shell, string[] leading) = Shell();
        return OpenSshClientVersion.Probe(shell, [.. leading, script], timeout);
    }

    /// <summary>ssh -V writes its version to stderr.</summary>
    [Fact]
    public void The_probe_reads_the_version_ssh_writes_to_stderr()
    {
        Version? version = ProbeShell(
            Pick("echo OpenSSH_for_Windows_8.1p1, LibreSSL 3.0.2 1>&2", "echo 'OpenSSH_8.2p1 Ubuntu-4ubuntu0.11, OpenSSL 1.1.1f  31 Mar 2020' >&2"),
            Bound);

        Assert.Equal(Pick("8.1", "8.2"), version?.ToString());
    }

    /// <summary>
    /// R4-b: nothing the probe runs can wait on a prompt. Its stdin is closed at once: a stand-in that reads a line first
    /// gets end-of-file and goes on, instead of waiting out the timeout.
    /// </summary>
    [Fact]
    public void The_probes_stdin_is_closed_so_nothing_waits_on_it()
    {
        var timer = Stopwatch.StartNew();
        Version? version = ProbeShell(
            Pick("set /p line= & echo OpenSSH_9.5p1, LibreSSL 3.8.2 1>&2", "read line; echo 'OpenSSH_9.5p1, LibreSSL 3.8.2' >&2"),
            Bound);

        Assert.Equal(new Version(9, 5), version);
        Assert.True(timer.Elapsed < Bound, $"took {timer.Elapsed}");
    }

    /// <summary>R4-a, R4-b: a client that does not answer within the timeout is stopped, and has no version.</summary>
    [Fact]
    public void A_probe_that_runs_past_its_timeout_is_stopped_and_reads_as_no_version()
    {
        var timer = Stopwatch.StartNew();
        Version? version = ProbeShell(
            Pick("ping -n 60 127.0.0.1 >nul & echo OpenSSH_9.5p1 1>&2", "sleep 60; echo OpenSSH_9.5p1 >&2"),
            TimeSpan.FromMilliseconds(500));

        Assert.Null(version);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(15), $"the probe took {timer.Elapsed}");
    }

    /// <summary>R4-a: ssh -V exits 0; a run that fails says nothing about the client, whatever it printed.</summary>
    [Fact]
    public void A_probe_that_exits_with_an_error_reads_as_no_version()
    {
        Assert.Null(ProbeShell(Pick("echo OpenSSH_9.5p1 1>&2 & exit /b 1", "echo OpenSSH_9.5p1 >&2; exit 1"), Bound));
    }

    /// <summary>R4-a: no executable there is no version, not an exception.</summary>
    [Fact]
    public void A_missing_client_reads_as_no_version()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"ntilde-no-ssh-{Guid.NewGuid():N}", OperatingSystem.IsWindows() ? "ssh.exe" : "ssh");

        Assert.Null(OpenSshClientVersion.Probe(missing));
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
        Version? version = OpenSshClientVersion.Probe(ssh!);
        TestContext.Current.TestOutputHelper?.WriteLine($"{ssh}: {version?.ToString() ?? "(none)"}");

        Assert.NotNull(version);
    }

    private static readonly DateTime Monday = new(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>The cache over a counting probe, with the executables' last-write times as <paramref name="stamps"/> says.</summary>
    private static OpenSshClientVersionCache Counting(List<string> probed, Func<string, DateTime> stamps, Version? version = null) =>
        new(
            path =>
            {
                lock (probed) probed.Add(path);
                return version ?? new Version(9, 5);
            },
            stamps);

    /// <summary>Once per plan: one probe per executable and last-write time, and only the first lookup is the first.</summary>
    [Fact]
    public void The_cache_probes_an_executable_once_until_it_changes()
    {
        var probed = new List<string>();
        DateTime stamp = Monday;
        OpenSshClientVersionCache cache = Counting(probed, _ => stamp, new Version(8, 1));
        string ssh = Path.Combine(Path.GetTempPath(), "bin", "ssh");

        Version? first = cache.VersionOf(ssh, out bool firstLookup);
        Version? again = cache.VersionOf(ssh, out bool againFirst);
        stamp = Monday.AddDays(1);   // upgraded in place
        Version? upgraded = cache.VersionOf(ssh, out bool upgradedFirst);
        Version? afterUpgrade = cache.VersionOf(ssh, out bool afterUpgradeFirst);

        Assert.Equal(new[] { ssh, ssh }, probed);
        Assert.Equal(new Version(8, 1), first);
        Assert.Equal(first, again);
        Assert.Equal(first, upgraded);
        Assert.Equal(first, afterUpgrade);
        Assert.Equal((true, false, true, false), (firstLookup, againFirst, upgradedFirst, afterUpgradeFirst));
    }

    /// <summary>Each executable has its own version: a second ssh on the PATH is probed on its own.</summary>
    [Fact]
    public void The_cache_probes_each_executable()
    {
        var probed = new List<string>();
        OpenSshClientVersionCache cache = Counting(probed, _ => Monday);
        string one = Path.Combine(Path.GetTempPath(), "one", "ssh");
        string two = Path.Combine(Path.GetTempPath(), "two", "ssh");

        cache.VersionOf(one, out _);
        cache.VersionOf(two, out _);
        cache.VersionOf(one, out _);

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

        cache.VersionOf(direct, out bool directFirst);
        cache.VersionOf(roundabout, out bool roundaboutFirst);

        Assert.Equal(new[] { direct }, probed);
        Assert.Equal((true, false), (directFirst, roundaboutFirst));
    }

    /// <summary>Hosts reconnecting at once share one probe: the others wait for its answer instead of running their own.</summary>
    [Fact]
    public async Task Concurrent_lookups_share_one_probe()
    {
        var probed = new List<string>();
        using var release = new ManualResetEventSlim();
        var cache = new OpenSshClientVersionCache(
            path =>
            {
                lock (probed) probed.Add(path);
                release.Wait(Bound);
                return new Version(9, 5);
            },
            _ => Monday);
        string ssh = Path.Combine(Path.GetTempPath(), "bin", "ssh");
        int firsts = 0;

        Task<Version?>[] lookups = [.. Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            Version? version = cache.VersionOf(ssh, out bool first);
            if (first) Interlocked.Increment(ref firsts);
            return version;
        }))];
        await Task.Delay(100, TestContext.Current.CancellationToken);
        release.Set();
        Version?[] versions = await Task.WhenAll(lookups).WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.All(versions, v => Assert.Equal(new Version(9, 5), v));
        Assert.Single(probed);
        Assert.Equal(1, firsts);
    }

    /// <summary>R4-a: a probe that throws is no version, and is remembered as such until the executable changes.</summary>
    [Fact]
    public void A_probe_that_throws_is_no_version()
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

        Assert.Null(cache.VersionOf(ssh, out _));
        Assert.Null(cache.VersionOf(ssh, out _));
        Assert.Equal(1, probes);
    }
}
