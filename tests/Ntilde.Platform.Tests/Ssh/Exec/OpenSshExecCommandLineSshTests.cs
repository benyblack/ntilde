using System.Diagnostics;
using Ntilde.Platform.Ssh.Exec;
using Ntilde.Platform.Ssh.Launch;

namespace Ntilde.Platform.Tests.Ssh.Exec;

/// <summary>
/// Codex C3, checked against the real OpenSSH client: <c>ssh -G</c> prints the configuration it resolved from an
/// argv and exits - no connection, no network - so it says how ssh itself parses what
/// <see cref="OpenSshExecCommandLine.Build"/> produced from a profile's tricky extra arguments. Skipped where no
/// <c>ssh</c> is installed. It reads an empty config file of its own (<c>-F</c>), never the user's.
/// </summary>
public sealed class OpenSshExecCommandLineSshTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    /// <summary>A profile's extra arguments that would break the exec channel if ssh read them as given.</summary>
    public static TheoryData<string[]> Tricky => new()
    {
        { ["-tv"] },
        { ["-vt"] },
        { ["-tt"] },
        { ["-Nv"] },
        { ["-fN"] },
        { ["-n"] },
        { ["-s"] },
        { ["-W", "h:22"] },
        { ["-Wh:22"] },
        { ["-G"] },
        { ["-V"] },
        { ["-O", "check"] },
        { ["-Q", "cipher"] },
        { ["-M"] },
        { ["-o", "RequestTTY=force"] },
        { ["-oRequestTTY=yes"] },
        { ["-o", "SessionType none"] },
        { ["-o", "sessiontype=none"] },
        { ["-o", "ForkAfterAuthentication=yes"] },
        { ["-o", "StdinNull=yes"] },
        { ["-o", "RemoteCommand=x"] },
        { ["-o", "PermitLocalCommand=yes"] },
        { ["-o", "=SessionType=none"] },
        { ["-o", "\"SessionType\" none"] },
        { ["-o", " = SessionType none"] },
        { ["-o", " =SessionType none"] },
        { ["-o", "\tSessionType none"] },
        { ["-o", "Session\"Type\" none"] },
        { ["-o", "= = SessionType none"] },   // kept: ssh itself ignores it, as sessiontype default shows
        { ["-o", "\"SessionType none"] },     // kept: likewise
        { ["-vo", "RequestTTY=force"] },
        { ["-qMtfNnsv", "-p", "2222", "-o", "ServerAliveInterval=5"] },
    };

    [Theory]
    [MemberData(nameof(Tricky))]
    public void Ssh_reads_no_pty_no_background_no_null_stdin_and_the_channels_own_command(string[] extra)
    {
        string? ssh = FindSsh();
        Assert.SkipUnless(ssh is not null, "No OpenSSH client (ssh) on this machine.");
        string config = Path.GetTempFileName(); // empty: neither the user's nor the system's ssh config is read
        try
        {
            IReadOnlyList<string> argv = OpenSshExecCommandLine.Build([], ["-F", config, "ntilde-exec-check", .. extra], "ntilde-mux proxy --stdio");
            Assert.Equal("--", argv[^2]);

            // Without its "-- command" (-G only prints anyway): a -s left in would then fail ssh, "You must specify a
            // subsystem to invoke", which the exit check below catches.
            Dictionary<string, string> resolved = ResolvedConfiguration(ssh!, [.. argv.Take(argv.Count - 2)]);

            Assert.Equal("false", resolved.GetValueOrDefault("requesttty"));
            Assert.Equal("false", resolved.GetValueOrDefault("controlmaster"));
            Assert.Equal("no", resolved.GetValueOrDefault("permitlocalcommand"));
            Assert.False(resolved.ContainsKey("remotecommand"), $"remotecommand {resolved.GetValueOrDefault("remotecommand")}");
            // OpenSSH 8.7 and later name these three; an older client prints none of them.
            if (resolved.TryGetValue("sessiontype", out string? sessionType)) Assert.Equal("default", sessionType);
            if (resolved.TryGetValue("forkafterauthentication", out string? fork)) Assert.Equal("no", fork);
            if (resolved.TryGetValue("stdinnull", out string? stdinNull)) Assert.Equal("no", stdinNull);
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{string.Join(' ', extra)} -> requesttty {resolved["requesttty"]}, sessiontype {sessionType ?? "(n/a)"}, forkafterauthentication {fork ?? "(n/a)"}, stdinnull {stdinNull ?? "(n/a)"}, controlmaster {resolved["controlmaster"]}");
        }
        finally
        {
            File.Delete(config);
        }
    }

    private static string? FindSsh()
    {
        try
        {
            return SshLaunchPlanner.ResolveSshExecutablePath();
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>ssh -G &lt;argv&gt;</c>'s output as keyword to value. ssh exits 0 having printed it; a query or a version
    /// request left in the argv would print something else and fail the keyword lookups instead.
    /// </summary>
    private static Dictionary<string, string> ResolvedConfiguration(string ssh, string[] argv)
    {
        var startInfo = new ProcessStartInfo(ssh)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-G");
        foreach (string argument in argv) startInfo.ArgumentList.Add(argument);

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("ssh did not start");
        process.StandardInput.Close();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        if (!process.WaitForExit(Bound))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("ssh -G did not exit");
        }

        string output = stdout.GetAwaiter().GetResult();
        Assert.True(process.ExitCode == 0, $"ssh -G exited {process.ExitCode}: {stderr.GetAwaiter().GetResult()}");
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int space = line.IndexOf(' ', StringComparison.Ordinal);
            if (space > 0) resolved.TryAdd(line[..space], line[(space + 1)..]);
        }

        return resolved;
    }
}
