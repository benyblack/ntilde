using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ntilde.Platform.Ssh.Exec;
using Ntilde.Platform.Ssh.Models;

namespace Ntilde.Platform.Tests.Ssh.Exec;

/// <summary>
/// <see cref="OpenSshExecTransport"/>'s process plumbing (Phase 4 spec §8.2), against a real process.
/// </summary>
/// <remarks>
/// The stand-in for ssh is the platform shell, through the transport's internal argv seam: ssh's fixed
/// layout (<c>-T -o … &lt;plan&gt; -- &lt;command&gt;</c>) is not something a shell can run - <c>cmd.exe</c>
/// tries to execute <c>--</c>, and <c>sh</c> rejects <c>-T</c> - so the seam swaps only the argument list
/// and everything else (redirection, environment, the stderr thread, the channel) is the production
/// path. The argv itself is pinned by <see cref="OpenSshExecCommandLineTests"/> and
/// <see cref="CreateStartInfo_runs_ssh_directly_with_the_exec_argv_and_every_stream_piped"/>.
/// </remarks>
public sealed class OpenSshExecTransportProcessTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static SshProfile Profile() => new()
    {
        Id = Guid.Parse("531619ce-5685-4f74-9538-daf8a194ec39"),
        Name = "Prod",
        User = "alice",
        Host = "example.com",
        Port = 2200,
    };

    private static OpenSshExecTransport ShellTransport(string? askPassHelperPath = null)
    {
        (string shell, string[] leading) = Shell();
        return new OpenSshExecTransport(Profile(), shell, command => [.. leading, command], askPassHelperPath, log: _ => { });
    }

    private static (string Shell, string[] Leading) Shell() => OperatingSystem.IsWindows()
        ? (Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } comSpec ? comSpec : "cmd.exe", new[] { "/d", "/c" })
        : ("/bin/sh", new[] { "-c" });

    private static string Pick(string windows, string unix) => OperatingSystem.IsWindows() ? windows : unix;

    private static async Task<string> ReadToEndAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, TestContext.Current.CancellationToken);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    [Fact]
    public async Task Pipes_stdio_and_reports_the_exit_code()
    {
        using ISshExecChannel channel = ShellTransport().Start(Pick("findstr x & exit /b 5", "grep x; exit 5"), CancellationToken.None);

        channel.Stdin.Write("x\n"u8);
        channel.Stdin.Dispose();
        string stdout = await ReadToEndAsync(channel.Stdout).WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.Contains("x", stdout, StringComparison.Ordinal);
        Assert.Equal(5, await channel.Completion.WaitAsync(Bound, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Stdin_writes_reach_the_process_without_a_flush_or_eof()
    {
        // The process reads one line and exits; stdin stays open. A small write that sat in a
        // stdin buffer would leave it waiting forever - and a mux frame is a small write.
        using ISshExecChannel channel = ShellTransport().Start(Pick("set /p line= & exit /b 9", "read line; exit 9"), CancellationToken.None);

        channel.Stdin.Write("x\r\n"u8);

        Assert.Equal(9, await channel.Completion.WaitAsync(Bound, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Stderr_lands_in_the_tail_before_Completion_reports_ssh_failure_255()
    {
        using ISshExecChannel channel = ShellTransport().Start(Pick("echo oops 1>&2 & exit /b 255", "echo oops >&2; exit 255"), CancellationToken.None);

        Assert.Equal(255, await channel.Completion.WaitAsync(Bound, TestContext.Current.CancellationToken));
        Assert.Contains("oops", channel.StderrTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_chatty_stderr_never_blocks_the_process_and_the_tail_keeps_the_last_8_KiB()
    {
        using ISshExecChannel channel = ShellTransport().Start(
            Pick(
                "(for /l %i in (1,1,3000) do @echo line%i 1>&2) & exit /b 3",
                "i=1; while [ $i -le 3000 ]; do echo line$i >&2; i=$((i+1)); done; exit 3"),
            CancellationToken.None);

        Assert.Equal(3, await channel.Completion.WaitAsync(Bound, TestContext.Current.CancellationToken));
        string tail = channel.StderrTail;
        Assert.InRange(Encoding.UTF8.GetByteCount(tail), 4096, 8192);
        Assert.EndsWith("line3000", tail.TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Askpass_environment_reaches_the_process()
    {
        using ISshExecChannel channel = ShellTransport(askPassHelperPath: "/opt/ntilde/ntilde").Start(
            Pick("echo %SSH_ASKPASS_REQUIRE%:%NTILDE_SSH_ASKPASS_PROFILE_HOST%", "echo \"$SSH_ASKPASS_REQUIRE:$NTILDE_SSH_ASKPASS_PROFILE_HOST\""),
            CancellationToken.None);
        channel.Stdin.Dispose();

        string stdout = await ReadToEndAsync(channel.Stdout).WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.Contains("force:example.com", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dispose_after_stdin_eof_lets_the_process_finish_and_keeps_its_exit_code()
    {
        // 3 is the proxy's "the daemon's process is gone" (§7.3, §8.1): it must never read as unknown.
        ISshExecChannel channel = ShellTransport().Start(Pick("findstr x >nul & exit /b 3", "cat >/dev/null; exit 3"), CancellationToken.None);

        channel.Dispose();

        // Not killed (that would be null): closing stdin was enough, within the grace period.
        Assert.Equal(3, await channel.Completion.WaitAsync(Bound, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_kill_reads_as_unknown_but_an_exit_that_beat_the_kill_keeps_its_code()
    {
        // The race in Dispose: the grace period ran out, and the process exited on its own just
        // before the kill landed (the kill then throws, or on Windows silently does nothing). The
        // status is told apart by its code; a process test cannot hit that window deterministically.
        Assert.Null(OpenSshExecChannel.ReportedExitCode(OpenSshExecChannel.KilledExitCode, killIssued: true));
        Assert.Equal(3, OpenSshExecChannel.ReportedExitCode(3, killIssued: true));
        Assert.Equal(0, OpenSshExecChannel.ReportedExitCode(0, killIssued: true));
        Assert.Equal(255, OpenSshExecChannel.ReportedExitCode(255, killIssued: false));
        Assert.Equal(OpenSshExecChannel.KilledExitCode, OpenSshExecChannel.ReportedExitCode(OpenSshExecChannel.KilledExitCode, killIssued: false));
    }

    [Fact]
    public void Cancellation_while_starting_stops_the_process_before_Start_throws()
    {
        using var cts = new CancellationTokenSource();
        var log = new ConcurrentQueue<string>();
        (string shell, string[] leading) = Shell();

        // The argv seam runs after Start's own token check and before the spawn: cancelling there is
        // exactly "cancelled while starting", with no timing involved.
        var transport = new OpenSshExecTransport(Profile(), shell, command =>
        {
            cts.Cancel();
            return [.. leading, command];
        }, askPassHelperPath: null, log: log.Enqueue);

        Assert.ThrowsAny<OperationCanceledException>(() => transport.Start(Pick("ping -n 60 127.0.0.1 >nul", "sleep 60"), cts.Token));

        int pid = log.Select(line => Regex.Match(line, @"\(pid (\d+)\)")).Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).Single();
        Assert.True(HasExited(pid), $"pid {pid} is still running after a cancelled Start");
        Assert.Contains(log, line => line.Contains("stopped it", StringComparison.Ordinal));
    }

    [Fact]
    public void A_pty_flag_in_ExtraSshArgs_never_reaches_ssh_and_is_logged()
    {
        var log = new List<string>();
        var transport = new OpenSshExecTransport(Profile(), "/usr/bin/ssh", ["-F", "cfg", "alias", "-tt"], askPassHelperPath: null, log: log.Add);

        ProcessStartInfo startInfo = transport.CreateStartInfo("ntilde-mux proxy --stdio");

        Assert.DoesNotContain("-tt", startInfo.ArgumentList);
        Assert.DoesNotContain("-t", startInfo.ArgumentList);
        Assert.Contains(log, line => line.Contains("'-t'", StringComparison.Ordinal)); // codex C3: each dropped letter is named
    }

    [Fact]
    public async Task Stdin_flush_is_a_no_op_once_disposed()
    {
        // DuplexStdioStream flushes after every write; a writer racing Dispose must see at most the
        // write fail, never the flush.
        var stdin = new ProcessStdinStream(new MemoryStream());

        stdin.Dispose();

        Assert.Null(Record.Exception(stdin.Flush));
        Assert.Null(await Record.ExceptionAsync(() => stdin.FlushAsync(TestContext.Current.CancellationToken)));
        Assert.Throws<ObjectDisposedException>(() => stdin.Write("x"u8));
    }

    [Fact]
    public async Task Dispose_unblocks_a_reader_blocked_on_stdout_of_a_long_running_process()
    {
        ISshExecChannel channel = ShellTransport().Start(Pick("ping -n 60 127.0.0.1 >nul", "sleep 60"), CancellationToken.None);
        var readerDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new Thread(() =>
        {
            try { _ = channel.Stdout.Read(new byte[64]); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { /* a closed pipe is a return too */ }
            readerDone.SetResult();
        })
        { IsBackground = true, Name = "ExecTestReader" };
        reader.Start();
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.False(readerDone.Task.IsCompleted, "the reader should be blocked on a silent process");

        var clock = Stopwatch.StartNew();
        channel.Dispose();
        Task first = await Task.WhenAny(readerDone.Task, Task.Delay(Bound, TestContext.Current.CancellationToken));

        Assert.Same(readerDone.Task, first);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"the reader returned only after {clock.Elapsed}");
        Assert.Null(await channel.Completion.WaitAsync(Bound, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Final review F4: a cancelled connect's channel is killed at once - no stdin EOF and grace period, which
    /// an exiting app never waits out - and has been when Abort returns.
    /// </summary>
    [Fact]
    public async Task Abort_kills_the_process_at_once_and_its_status_is_unknown()
    {
        var log = new ConcurrentQueue<string>();
        (string shell, string[] leading) = Shell();
        ISshExecChannel channel = new OpenSshExecTransport(Profile(), shell, command => [.. leading, command], askPassHelperPath: null, log: log.Enqueue)
            .Start(Pick("ping -n 60 127.0.0.1 >nul", "sleep 60"), CancellationToken.None);
        int pid = log.Select(line => Regex.Match(line, @"\(pid (\d+)\)")).Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).Single();

        var clock = Stopwatch.StartNew();
        channel.Abort();
        TimeSpan took = clock.Elapsed;

        Assert.True(HasExited(pid), $"pid {pid} is still running after Abort returned");
        Assert.True(took < OpenSshExecChannel.ExitGrace, $"Abort took {took}: it waited for an EOF grace period");
        Assert.Null(await channel.Completion.WaitAsync(Bound, TestContext.Current.CancellationToken));
        channel.Abort();   // idempotent
        channel.Dispose(); // nothing left to end
    }

    /// <summary>Final review F4: an Abort while a Dispose waits out its grace period ends that wait now.</summary>
    [Fact]
    public async Task Abort_cuts_a_disposes_grace_period_short()
    {
        ISshExecChannel channel = ShellTransport().Start(Pick("ping -n 60 127.0.0.1 >nul", "sleep 60"), CancellationToken.None);
        Task dispose = Task.Run(channel.Dispose, TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);   // inside the grace period: stdin's EOF does not end it

        var clock = Stopwatch.StartNew();
        channel.Abort();
        await dispose.WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"Dispose returned {clock.Elapsed} after the Abort: its grace period ran on");
        Assert.Null(await channel.Completion.WaitAsync(Bound, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Start_with_a_cancelled_token_starts_nothing()
    {
        var transport = new OpenSshExecTransport(Profile(), MissingExecutable(), ["-F", "cfg", "alias"], askPassHelperPath: null, log: _ => { });

        // A missing executable would throw Win32Exception if Start got as far as spawning it.
        Assert.ThrowsAny<OperationCanceledException>(() => transport.Start("true", new CancellationToken(canceled: true)));
    }

    [Fact]
    public void Start_of_a_missing_ssh_throws()
    {
        var transport = new OpenSshExecTransport(Profile(), MissingExecutable(), ["-F", "cfg", "alias"], askPassHelperPath: null, log: _ => { });

        Assert.Throws<Win32Exception>(() => transport.Start("true", CancellationToken.None));
    }

    [Fact]
    public void CreateStartInfo_runs_ssh_directly_with_the_exec_argv_and_every_stream_piped()
    {
        string[] plan = ["-F", "/home/u/.config/ntilde/ssh_config", "ntilde_0123"];
        var transport = new OpenSshExecTransport(Profile(), "/usr/bin/ssh", plan, askPassHelperPath: null, diagnosticsArguments: ["-v"], log: _ => { });

        ProcessStartInfo startInfo = transport.CreateStartInfo("ntilde-mux proxy --stdio");

        Assert.Equal("/usr/bin/ssh", startInfo.FileName);
        Assert.Equal(OpenSshExecCommandLine.Build(["-v"], plan, "ntilde-mux proxy --stdio"), startInfo.ArgumentList);
        Assert.Equal(string.Empty, startInfo.Arguments);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.True(startInfo.CreateNoWindow);
    }

    [Fact]
    public void CreateStartInfo_applies_askpass_only_when_a_helper_is_given()
    {
        var withHelper = new OpenSshExecTransport(Profile(), "/usr/bin/ssh", ["-F", "cfg", "alias"], askPassHelperPath: "/opt/ntilde/ntilde", log: _ => { });
        var withoutHelper = new OpenSshExecTransport(Profile(), "/usr/bin/ssh", ["-F", "cfg", "alias"], askPassHelperPath: null, log: _ => { });

        ProcessStartInfo helped = withHelper.CreateStartInfo("true");
        ProcessStartInfo unhelped = withoutHelper.CreateStartInfo("true");

        Assert.Equal("/opt/ntilde/ntilde", helped.Environment["SSH_ASKPASS"]);
        Assert.Equal("force", helped.Environment["SSH_ASKPASS_REQUIRE"]);
        Assert.Equal("1", helped.Environment[SshAskPassEnvironment.ModeVariable]);
        Assert.Equal("531619ce-5685-4f74-9538-daf8a194ec39", helped.Environment[SshAskPassEnvironment.ProfileIdVariable]);
        Assert.Equal("2200", helped.Environment[SshAskPassEnvironment.ProfilePortVariable]);
        Assert.False(unhelped.Environment.ContainsKey(SshAskPassEnvironment.ModeVariable));
        Assert.False(unhelped.Environment.ContainsKey("SSH_ASKPASS_REQUIRE"));
    }

    /// <summary>
    /// A batch-mode transport is for attempts nobody is waiting on: ssh gets BatchMode=yes, our askpass
    /// helper is not wired up even when one is given, and any askpass ssh would inherit from the user's
    /// environment is switched off too - a ProxyJump hop's ssh does not inherit BatchMode, and on a
    /// desktop with a system askpass it could otherwise put a dialog up.
    /// </summary>
    [Fact]
    public void Batch_mode_runs_ssh_with_BatchMode_yes_and_no_askpass_at_all()
    {
        string[] plan = ["-F", "cfg", "alias"];
        var batch = new OpenSshExecTransport(Profile(), "/usr/bin/ssh", plan, askPassHelperPath: "/opt/ntilde/ntilde", log: _ => { }, batchMode: true);
        var interactive = new OpenSshExecTransport(Profile(), "/usr/bin/ssh", plan, askPassHelperPath: "/opt/ntilde/ntilde", log: _ => { });

        ProcessStartInfo startInfo = batch.CreateStartInfo("true");

        Assert.True(batch.BatchMode);
        Assert.False(interactive.BatchMode);
        Assert.Equal(OpenSshExecCommandLine.Build([], plan, "true", batchMode: true), startInfo.ArgumentList);
        Assert.False(startInfo.Environment.ContainsKey(SshAskPassEnvironment.ModeVariable));
        Assert.False(startInfo.Environment.ContainsKey(SshAskPassEnvironment.AskPassVariable));
        Assert.False(startInfo.Environment.ContainsKey(SshAskPassEnvironment.DisplayVariable));
        Assert.Equal("never", startInfo.Environment[SshAskPassEnvironment.AskPassRequireVariable]);
        Assert.Equal("/opt/ntilde/ntilde", interactive.CreateStartInfo("true").Environment["SSH_ASKPASS"]);
        Assert.False(startInfo.Environment.ContainsKey(SshAskPassEnvironment.VaultOnlyVariable));
        Assert.False(interactive.CreateStartInfo("true").Environment.ContainsKey(SshAskPassEnvironment.VaultOnlyVariable));
        Assert.False(batch.SavedPasswordOnly);
        Assert.False(interactive.SavedPasswordOnly);
    }

    /// <summary>
    /// An automatic reconnect of a profile whose password is saved: ssh may prompt, once for a password, and every
    /// prompt goes to the helper in its vault-only mode, which answers the target's password from the vault and turns
    /// everything else away without showing anything.
    /// </summary>
    [Fact]
    public void Saved_password_only_runs_ssh_with_one_password_prompt_and_the_helper_in_vault_only_mode()
    {
        string[] plan = ["-F", "cfg", "alias"];
        var transport = new OpenSshExecTransport(Profile(), "/usr/bin/ssh", plan, askPassHelperPath: "/opt/ntilde/ntilde", log: _ => { }, savedPasswordOnly: true);

        ProcessStartInfo startInfo = transport.CreateStartInfo("true");

        Assert.True(transport.SavedPasswordOnly);
        Assert.False(transport.BatchMode);
        Assert.Equal(OpenSshExecCommandLine.Build([], plan, "true", savedPasswordOnly: true), startInfo.ArgumentList);
        Assert.Equal("/opt/ntilde/ntilde", startInfo.Environment[SshAskPassEnvironment.AskPassVariable]);
        Assert.Equal("force", startInfo.Environment[SshAskPassEnvironment.AskPassRequireVariable]);
        Assert.Equal("1", startInfo.Environment[SshAskPassEnvironment.ModeVariable]);
        Assert.Equal("1", startInfo.Environment[SshAskPassEnvironment.VaultOnlyVariable]);
        Assert.Equal("531619ce-5685-4f74-9538-daf8a194ec39", startInfo.Environment[SshAskPassEnvironment.ProfileIdVariable]);
    }

    /// <summary>Without a helper nothing could answer the password: the attempt runs in batch mode, and never prompts.</summary>
    [Fact]
    public void Saved_password_only_without_a_helper_runs_in_batch_mode()
    {
        string[] plan = ["-F", "cfg", "alias"];
        var transport = new OpenSshExecTransport(Profile(), "/usr/bin/ssh", plan, askPassHelperPath: null, log: _ => { }, savedPasswordOnly: true);

        ProcessStartInfo startInfo = transport.CreateStartInfo("true");

        Assert.False(transport.SavedPasswordOnly);
        Assert.True(transport.BatchMode);
        Assert.Equal(OpenSshExecCommandLine.Build([], plan, "true", batchMode: true), startInfo.ArgumentList);
        Assert.False(startInfo.Environment.ContainsKey(SshAskPassEnvironment.AskPassVariable));
        Assert.Equal("never", startInfo.Environment[SshAskPassEnvironment.AskPassRequireVariable]);
    }

    /// <summary>
    /// A user's attempt after the host's saved password was refused: an interactive ssh (BatchMode=no, the usual
    /// number of prompts) whose helper skips the vault and asks the user at once.
    /// </summary>
    [Fact]
    public void Without_saved_password_runs_an_interactive_ssh_whose_helper_skips_the_vault()
    {
        string[] plan = ["-F", "cfg", "alias"];
        var transport = new OpenSshExecTransport(Profile(), "/usr/bin/ssh", plan, askPassHelperPath: "/opt/ntilde/ntilde", log: _ => { }, withoutSavedPassword: true);

        ProcessStartInfo startInfo = transport.CreateStartInfo("true");

        Assert.True(transport.WithoutSavedPassword);
        Assert.False(transport.BatchMode);
        Assert.False(transport.SavedPasswordOnly);
        Assert.Equal(OpenSshExecCommandLine.Build([], plan, "true"), startInfo.ArgumentList);
        Assert.Equal("/opt/ntilde/ntilde", startInfo.Environment[SshAskPassEnvironment.AskPassVariable]);
        Assert.Equal("1", startInfo.Environment[SshAskPassEnvironment.NoVaultVariable]);
        Assert.False(startInfo.Environment.ContainsKey(SshAskPassEnvironment.VaultOnlyVariable));
        Assert.False(new OpenSshExecTransport(Profile(), "/usr/bin/ssh", plan, "/opt/ntilde/ntilde", log: _ => { })
            .CreateStartInfo("true").Environment.ContainsKey(SshAskPassEnvironment.NoVaultVariable));
    }

    /// <summary>Every ssh the transport starts carries a session token of its own, for the helper's fill-once rule.</summary>
    [Fact]
    public void Every_ssh_started_gets_its_own_askpass_session_token()
    {
        var transport = new OpenSshExecTransport(Profile(), "/usr/bin/ssh", ["-F", "cfg", "alias"], askPassHelperPath: "/opt/ntilde/ntilde", log: _ => { });

        string? first = transport.CreateStartInfo("true").Environment[SshAskPassEnvironment.SessionVariable];
        string? second = transport.CreateStartInfo("true").Environment[SshAskPassEnvironment.SessionVariable];

        Assert.Matches("^[0-9a-f]{32}$", first);
        Assert.Matches("^[0-9a-f]{32}$", second);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Without_saved_password_is_for_a_user_attempt_only()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new OpenSshExecTransport(Profile(), "/usr/bin/ssh", ["alias"], askPassHelperPath: "/opt/ntilde/ntilde", log: _ => { }, batchMode: true, withoutSavedPassword: true));
        Assert.ThrowsAny<ArgumentException>(() =>
            new OpenSshExecTransport(Profile(), "/usr/bin/ssh", ["alias"], askPassHelperPath: "/opt/ntilde/ntilde", log: _ => { }, savedPasswordOnly: true, withoutSavedPassword: true));
    }

    [Fact]
    public void Batch_mode_and_saved_password_only_are_not_both_possible()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new OpenSshExecTransport(Profile(), "/usr/bin/ssh", ["alias"], askPassHelperPath: "/opt/ntilde/ntilde", log: _ => { }, batchMode: true, savedPasswordOnly: true));
    }

    [Fact]
    public void DisplayName_is_user_at_host_or_the_bare_host()
    {
        var bare = new SshProfile { Host = "example.com" };

        Assert.Equal("alice@example.com", new OpenSshExecTransport(Profile(), "ssh", ["alias"], null).DisplayName);
        Assert.Equal("example.com", new OpenSshExecTransport(bare, "ssh", ["alias"], null).DisplayName);
    }

    private static bool HasExited(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true; // no such process
        }
    }

    private static string MissingExecutable() =>
        Path.Combine(Path.GetTempPath(), "ntilde-missing-" + Guid.NewGuid().ToString("N"), OperatingSystem.IsWindows() ? "ssh.exe" : "ssh");
}
