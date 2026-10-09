using Ntilde.Mux.Cli;
using Ntilde.Platform.Ssh.Exec;
using Ntilde.Platform.Ssh.Models;
using Ntilde.Shell.Mux.Remote;

namespace Ntilde.Tests.Shell.Mux.Remote;

/// <summary>
/// <see cref="RemoteMuxInstaller"/> (Phase 4 spec §9): probe, asset, upload and trial run over the exec
/// channel, validate, commit or discard, verify, record. The host is a <see cref="RecordingExecTransport"/>
/// that keeps every command and the bytes its stdin got; the asset is a fake.
/// </summary>
public sealed class RemoteMuxInstallerTests
{
    private const string UbuntuProbe = "Linux x86_64\nldd (Ubuntu GLIBC 2.35-0ubuntu3.8) 2.35\nHOME=/home/nova\n";
    private const string InstalledPath = "/home/nova/.local/share/ntilde/bin/ntilde-mux";
    private const string InstalledJson =
        "{\"version\":\"0.11.0\",\"protocolMin\":1,\"protocolMax\":2,\"rid\":\"linux-x64\",\"path\":\"" + InstalledPath + "\"}\n";

    /// <summary>A binary that runs but shares no protocol with this app (1-2): a local file of another version.</summary>
    private const string IncompatibleJson =
        "{\"version\":\"0.20.0\",\"protocolMin\":3,\"protocolMax\":4,\"rid\":\"linux-x64\",\"path\":\"" + InstalledPath + "\"}\n";

    private static readonly MuxVersionInfo Installed = new("0.11.0", 1, 2, "linux-x64", InstalledPath);
    private static readonly byte[] Binary = MakeBinary(150 * 1024 + 17);
    private static readonly Guid Token = new("0f1e2d3c4b5a69788796a5b4c3d2e1f0");

    private static readonly string Trial = RemoteMuxInstallCommands.UploadForTrial(Binary.Length, Token);
    private static readonly string Commit = RemoteMuxInstallCommands.CommitUpload(Token);
    private static readonly string Discard = RemoteMuxInstallCommands.DiscardUpload(Token);

    private readonly List<(RemoteMuxInstallStep Step, string Message)> _steps = [];
    private readonly List<RemoteMuxInstallProgress> _progress = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] MakeBinary(int size)
    {
        byte[] bytes = new byte[size];
        new Random(7).NextBytes(bytes);
        return bytes;
    }

    /// <summary>
    /// A host that answers the probe with <paramref name="probe"/>, the upload's trial run with
    /// <paramref name="upload"/>, the commit with <paramref name="commit"/> (the upload's reply when none),
    /// and a discard with success.
    /// </summary>
    private static RecordingExecTransport Host(FakeExecReply upload, string probe = UbuntuProbe, FakeExecReply? commit = null) =>
        new((command, _) =>
            command == RemoteHostProbe.Command ? new FakeExecReply(probe)
            : command == Commit ? commit ?? upload
            : command == Discard ? new FakeExecReply()
            : upload);

    private RemoteMuxInstaller Installer(RecordingExecTransport host, IMuxDaemonAssetSource source) =>
        new(host, source, (step, message) => { lock (_steps) _steps.Add((step, message)); })
        {
            Progress = new SyncProgress(p => { lock (_progress) _progress.Add(p); }),
            NewUploadToken = () => Token,
        };

    /// <summary>Whether any command the host ran could have replaced its installed ntilde-mux.</summary>
    private static bool AnythingMovedOver(RecordingExecTransport host) =>
        host.Commands.Any(c => c.Contains("mv -f", StringComparison.Ordinal) || c.Contains("> \"$d/ntilde-mux\"", StringComparison.Ordinal));

    [Fact]
    public async Task Installs_through_the_exec_channel_and_verifies()
    {
        RecordingExecTransport host = Host(new FakeExecReply(InstalledJson));
        var source = new FakeAssetSource(new MuxDaemonAsset(Binary, "ab12", "https://example.test/ntilde-mux-linux-x64"));

        RemoteMuxInstallResult result = await Installer(host, source).InstallAsync(Ct);

        Assert.True(result.Success, result.Message);
        Assert.Equal(Installed, result.Installed);
        Assert.Equal($"ntilde-mux 0.11.0 installed at {InstalledPath}", result.Message);
        Assert.Equal([RemoteHostProbe.Command, Trial, Commit], host.Commands);
        Assert.Empty(host.Runs[0].Stdin);
        Assert.Equal(Binary, host.Runs[1].Stdin);
        Assert.Empty(host.Runs[2].Stdin);
        Assert.Equal(["linux-x64"], source.RequestedRids);
        Assert.Equal(
            [RemoteMuxInstallStep.Probing, RemoteMuxInstallStep.Downloading, RemoteMuxInstallStep.Uploading, RemoteMuxInstallStep.Verifying, RemoteMuxInstallStep.Done],
            _steps.Select(s => s.Step).Distinct());
        Assert.Contains(_steps, s => s.Step == RemoteMuxInstallStep.Downloading && s.Message.Contains("ab12", StringComparison.Ordinal));
    }

    /// <summary>
    /// Phase 5 Task 23 ("the same rule for remote daemons"): an update - the same flow as an install, over a host whose
    /// ntilde-mux is already running - replaces the binary by a rename, which the running daemon survives on its old
    /// inode, and never stops that daemon: its shells keep running, and the "from a previous version" notice offers the
    /// restart when the user is ready. Every word of every command the flow ran is checked, not just its known lines.
    /// </summary>
    [Fact]
    public async Task An_update_leaves_the_running_daemon_alone()
    {
        RecordingExecTransport host = Host(new FakeExecReply(InstalledJson));

        RemoteMuxInstallResult result = await Installer(host, new FakeAssetSource(new MuxDaemonAsset(Binary, "ab12", "x"))).InstallAsync(Ct);

        Assert.True(result.Success, result.Message);
        Assert.Contains(host.Commands, c => c.Contains("mv -f", StringComparison.Ordinal)); // the binary was replaced
        string[] stops = ["kill-server", "shutdown", "kill", "pkill", "killall"];
        foreach (string command in host.Commands)
        {
            string[] words = System.Text.RegularExpressions.Regex.Split(command, @"[\s;'""&|(){}]+");
            Assert.False(words.Any(stops.Contains), $"a command of the update stops the daemon: {command}");
        }
    }

    [Fact]
    public async Task Upload_progress_counts_the_bytes_written()
    {
        RecordingExecTransport host = Host(new FakeExecReply(InstalledJson));

        await Installer(host, new FakeAssetSource(new MuxDaemonAsset(Binary, "ab12", "x"))).InstallAsync(Ct);

        RemoteMuxInstallProgress[] upload = _progress.Where(p => p.Step == RemoteMuxInstallStep.Uploading).ToArray();
        Assert.Equal([65536L, 131072L, Binary.Length], upload.Select(p => p.Done));
        Assert.All(upload, p => Assert.Equal(Binary.Length, p.Total));
        Assert.Contains(new RemoteMuxInstallProgress(RemoteMuxInstallStep.Downloading, Binary.Length, 0), _progress);
    }

    [Fact]
    public void Record_writes_the_path_version_and_rid_and_nothing_else()
    {
        var options = new SshMuxOptions { Enabled = true, PersistRemoteSessions = false, ControlPath = "/tmp/cm" };

        RemoteMuxInstaller.Record(options, Installed);

        Assert.Equal(InstalledPath, options.RemoteDaemonPath);
        Assert.Equal("0.11.0", options.RemoteDaemonVersion);
        Assert.Equal("linux-x64", options.RemoteDaemonRid);
        Assert.False(options.PersistRemoteSessions);
        Assert.True(options.Enabled);
        Assert.Equal("/tmp/cm", options.ControlPath);
    }

    [Fact]
    public async Task A_refused_host_gets_no_download_and_no_upload()
    {
        RecordingExecTransport host = Host(new FakeExecReply(InstalledJson), probe: "Linux x86_64\nmusl libc (x86_64)\nHOME=/root\n");
        var source = new FakeAssetSource(new MuxDaemonAsset(Binary, "ab12", "x"));

        RemoteMuxInstallResult result = await Installer(host, source).InstallAsync(Ct);

        Assert.Equal(new RemoteMuxInstallResult(false, "musl libc is not supported", null), result);
        Assert.Equal([RemoteHostProbe.Command], host.Commands);
        Assert.Empty(source.RequestedRids);
    }

    [Fact]
    public async Task Upload_exit_nonzero_reports_stderr()
    {
        RecordingExecTransport host = Host(new FakeExecReply(
            Stderr: "mkdir: cannot create directory '/home/nova/.local/share/ntilde': Permission denied\n", ExitCode: 1));

        RemoteMuxInstallResult result = await Installer(host, new FakeAssetSource(new MuxDaemonAsset(Binary, "ab12", "x"))).InstallAsync(Ct);

        Assert.False(result.Success);
        Assert.Null(result.Installed);
        Assert.Equal(
            "The upload to nova@fake-host failed (exit 1): mkdir: cannot create directory '/home/nova/.local/share/ntilde': Permission denied",
            result.Message);
        Assert.DoesNotContain(_steps, s => s.Step == RemoteMuxInstallStep.Done);

        // The upload script's own trap removed its temp file: there is nothing to commit or discard.
        Assert.Equal([RemoteHostProbe.Command, Trial], host.Commands);
    }

    [Fact]
    public async Task A_lost_link_during_the_upload_is_a_failure()
    {
        RecordingExecTransport host = Host(new FakeExecReply(Stderr: "native ssh: connection lost\n", ExitCode: null));

        RemoteMuxInstallResult result = await Installer(host, new FakeAssetSource(new MuxDaemonAsset(Binary, "ab12", "x"))).InstallAsync(Ct);

        Assert.Equal(new RemoteMuxInstallResult(false, "The upload to nova@fake-host failed (no exit status): native ssh: connection lost", null), result);
        Assert.Equal([RemoteHostProbe.Command, Trial], host.Commands);
    }

    /// <summary>
    /// The greptile P1 on PR #504: a binary that runs but shares no protocol with this app (a local file of
    /// another version) is turned away while it is still the upload's temp file. The installed binary is
    /// never touched: no commit is sent, and the discard drops the upload.
    /// </summary>
    [Fact]
    public async Task An_incompatible_trial_is_discarded_and_never_replaces_the_installed_binary()
    {
        RecordingExecTransport host = Host(new FakeExecReply(IncompatibleJson), commit: new FakeExecReply(InstalledJson));

        RemoteMuxInstallResult result = await Installer(host, new FakeAssetSource(new MuxDaemonAsset(Binary, "ab12", "x"))).InstallAsync(Ct);

        Assert.Equal(new RemoteMuxInstallResult(false, "ntilde-mux 0.20.0 speaks protocol 3-4; this app speaks 1-2", null), result);
        Assert.DoesNotContain(_steps, s => s.Step == RemoteMuxInstallStep.Done);
        Assert.False(AnythingMovedOver(host), string.Join("\n", host.Commands));
        Assert.Equal([RemoteHostProbe.Command, Trial, Discard], host.Commands);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ntilde-mux 0.11.0 (protocol 1-2, linux-x64)\n")]
    [InlineData("{\"version\":\"0.11.0\",\"protocolMin\":1}\n")]
    [InlineData("{not json\n")]
    public async Task A_trial_that_does_not_report_a_version_is_discarded(string stdout)
    {
        RecordingExecTransport host = Host(new FakeExecReply(stdout), commit: new FakeExecReply(InstalledJson));

        RemoteMuxInstallResult result = await Installer(host, new FakeAssetSource(new MuxDaemonAsset(Binary, "ab12", "x"))).InstallAsync(Ct);

        Assert.False(result.Success);
        Assert.Null(result.Installed);
        Assert.StartsWith("ntilde-mux did not report its version", result.Message, StringComparison.Ordinal);
        Assert.False(AnythingMovedOver(host), string.Join("\n", host.Commands));
        Assert.Equal([RemoteHostProbe.Command, Trial, Discard], host.Commands);
    }

    /// <summary>What is verified and recorded is the commit's report: the trial ran under the temp name.</summary>
    [Fact]
    public async Task What_is_recorded_is_what_the_committed_binary_reports()
    {
        string trialJson = InstalledJson.Replace("/ntilde-mux\"", "/.ntilde-mux.upload-" + Token.ToString("N") + "\"", StringComparison.Ordinal);
        Assert.NotEqual(InstalledJson, trialJson);
        RecordingExecTransport host = Host(new FakeExecReply(trialJson), commit: new FakeExecReply(InstalledJson));

        RemoteMuxInstallResult result = await Installer(host, new FakeAssetSource(new MuxDaemonAsset(Binary, "ab12", "x"))).InstallAsync(Ct);

        Assert.True(result.Success, result.Message);
        Assert.Equal(Installed, result.Installed);
        Assert.Equal([RemoteHostProbe.Command, Trial, Commit], host.Commands);
    }

    [Fact]
    public async Task A_commit_that_fails_is_the_result_and_discards_the_upload()
    {
        RecordingExecTransport host = Host(
            new FakeExecReply(InstalledJson),
            commit: new FakeExecReply(Stderr: "mv: cannot move '/home/nova/.local/share/ntilde/bin/x': Permission denied\n", ExitCode: 1));

        RemoteMuxInstallResult result = await Installer(host, new FakeAssetSource(new MuxDaemonAsset(Binary, "ab12", "x"))).InstallAsync(Ct);

        Assert.Equal(
            new RemoteMuxInstallResult(false, "Installing ntilde-mux on nova@fake-host failed (exit 1): mv: cannot move '/home/nova/.local/share/ntilde/bin/x': Permission denied", null),
            result);
        Assert.Equal([RemoteHostProbe.Command, Trial, Commit, Discard], host.Commands);
    }

    /// <summary>A commit that ran moved the upload: its verification fails as the single upload's did, with nothing left to discard.</summary>
    [Fact]
    public async Task A_committed_binary_that_does_not_report_a_version_fails_as_before()
    {
        RecordingExecTransport host = Host(new FakeExecReply(InstalledJson), commit: new FakeExecReply("garbage\n"));

        RemoteMuxInstallResult result = await Installer(host, new FakeAssetSource(new MuxDaemonAsset(Binary, "ab12", "x"))).InstallAsync(Ct);

        Assert.Equal(new RemoteMuxInstallResult(false, "ntilde-mux did not report its version: garbage", null), result);
        Assert.Equal([RemoteHostProbe.Command, Trial, Commit], host.Commands);
    }

    /// <summary>
    /// After the upload's trial run the host keeps the temp file; a cancel from then on, before the commit or
    /// while it still runs (its result unknown), still discards it (spec §9 step 3), and the caller sees the
    /// cancellation.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_cancel_after_the_upload_discards_it_and_throws(bool duringTheCommit)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var inner = new RecordingExecTransport((command, _) =>
            command == RemoteHostProbe.Command ? new FakeExecReply(UbuntuProbe)
            : command == Discard ? new FakeExecReply()
            : new FakeExecReply(InstalledJson));
        var host = new CommitHost(inner, cts, commitFinishes: false);
        var installer = new RemoteMuxInstaller(
            host,
            new FakeAssetSource(new MuxDaemonAsset(Binary, "ab12", "x")),
            (step, _) => { if (step == RemoteMuxInstallStep.Verifying && !duringTheCommit) cts.Cancel(); })
        {
            NewUploadToken = () => Token,
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.InstallAsync(cts.Token));

        Assert.Equal(duringTheCommit, host.CommitRan);
        Assert.Equal([RemoteHostProbe.Command, Trial, Discard], inner.Commands);
    }

    /// <summary>
    /// A cancel that lands once the commit's result is in changes nothing: the upload already replaced ntilde-mux, so the
    /// install is verified and reported for the caller to record, and nothing is discarded. Here the commit's output and
    /// exit status are in before the cancel - the order a loaded CI runner produced.
    /// </summary>
    [Fact]
    public async Task A_cancel_that_lands_after_the_commit_finished_keeps_the_install()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var inner = new RecordingExecTransport((command, _) =>
            command == RemoteHostProbe.Command ? new FakeExecReply(UbuntuProbe)
            : command == Discard ? new FakeExecReply()
            : new FakeExecReply(InstalledJson));
        var host = new CommitHost(inner, cts, commitFinishes: true);
        var installer = new RemoteMuxInstaller(host, new FakeAssetSource(new MuxDaemonAsset(Binary, "ab12", "x")), (_, _) => { })
        {
            NewUploadToken = () => Token,
        };

        RemoteMuxInstallResult result = await installer.InstallAsync(cts.Token);

        Assert.True(cts.IsCancellationRequested);
        Assert.True(result.Success, result.Message);
        Assert.Equal(Installed, result.Installed);
        Assert.True(host.CommitRan);
        Assert.Equal([RemoteHostProbe.Command, Trial], inner.Commands);
    }

    [Fact]
    public async Task A_cancel_during_the_upload_leaves_the_cleanup_to_its_trap()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        RecordingExecTransport host = Host(new FakeExecReply(InstalledJson));
        var installer = new RemoteMuxInstaller(host, new FakeAssetSource(new MuxDaemonAsset(Binary, "ab12", "x")), (_, _) => { })
        {
            // The first chunk is written; the cancel stops the rest and ends the channel before stdin's EOF.
            Progress = new SyncProgress(p => { if (p.Step == RemoteMuxInstallStep.Uploading) cts.Cancel(); }),
            NewUploadToken = () => Token,
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.InstallAsync(cts.Token));

        Assert.Equal([RemoteHostProbe.Command], host.Commands); // the upload never reached EOF; no commit, no discard
    }

    [Fact]
    public async Task A_discard_that_cannot_run_leaves_the_real_reason()
    {
        var host = new RecordingExecTransport((command, _) =>
            command == RemoteHostProbe.Command ? new FakeExecReply(UbuntuProbe) : new FakeExecReply(IncompatibleJson))
        {
            OnStart = command =>
            {
                if (command == Discard) throw new InvalidOperationException("ssh was not found");
            },
        };

        RemoteMuxInstallResult result = await Installer(host, new FakeAssetSource(new MuxDaemonAsset(Binary, "ab12", "x"))).InstallAsync(Ct);

        Assert.Equal(new RemoteMuxInstallResult(false, "ntilde-mux 0.20.0 speaks protocol 3-4; this app speaks 1-2", null), result);
        Assert.Equal([RemoteHostProbe.Command, Trial], host.Commands);
    }

    /// <summary>Each install uploads under a token of its own, and its commit or discard names that one.</summary>
    [Fact]
    public async Task Each_install_s_steps_share_a_fresh_upload_token()
    {
        static RecordingExecTransport Incompatible() => new((command, _) =>
            command == RemoteHostProbe.Command ? new FakeExecReply(UbuntuProbe) : new FakeExecReply(IncompatibleJson));

        RecordingExecTransport first = Incompatible();
        RecordingExecTransport second = Incompatible();
        var asset = new FakeAssetSource(new MuxDaemonAsset(Binary, "ab12", "x"));
        await new RemoteMuxInstaller(first, asset, (_, _) => { }).InstallAsync(Ct);
        await new RemoteMuxInstaller(second, asset, (_, _) => { }).InstallAsync(Ct);

        Guid TokenOf(RecordingExecTransport host)
        {
            string upload = host.Commands[1];
            string temp = "t=\"$d/" + RemoteMuxInstallCommands.UploadTempPrefix;
            int at = upload.IndexOf(temp, StringComparison.Ordinal) + temp.Length;
            var token = Guid.ParseExact(upload.Substring(at, 32), "N");
            Assert.Equal([RemoteHostProbe.Command, RemoteMuxInstallCommands.UploadForTrial(Binary.Length, token), RemoteMuxInstallCommands.DiscardUpload(token)], host.Commands);
            return token;
        }

        Assert.NotEqual(TokenOf(first), TokenOf(second));
    }

    [Fact]
    public async Task An_overlapping_protocol_range_is_enough()
    {
        RecordingExecTransport host = Host(new FakeExecReply(
            "{\"version\":\"0.12.0\",\"protocolMin\":2,\"protocolMax\":3,\"rid\":\"linux-x64\",\"path\":\"" + InstalledPath + "\"}\n"));

        RemoteMuxInstallResult result = await Installer(host, new FakeAssetSource(new MuxDaemonAsset(Binary, "ab12", "x"))).InstallAsync(Ct);

        Assert.True(result.Success, result.Message);
        Assert.Equal(new MuxVersionInfo("0.12.0", 2, 3, "linux-x64", InstalledPath), result.Installed);
    }

    [Fact]
    public async Task A_binary_that_reports_another_platform_installs_with_a_warning()
    {
        RecordingExecTransport host = Host(new FakeExecReply(InstalledJson.Replace("linux-x64", "linux-arm64", StringComparison.Ordinal)));

        RemoteMuxInstallResult result = await Installer(host, new FakeAssetSource(new MuxDaemonAsset(Binary, "ab12", "x"))).InstallAsync(Ct);

        Assert.True(result.Success, result.Message);
        Assert.Equal(
            $"ntilde-mux 0.11.0 installed at {InstalledPath} (warning: it reports linux-arm64, but the host is linux-x64)",
            result.Message);
        Assert.Contains(_steps, s => s.Message == "Warning: it reports linux-arm64, but the host is linux-x64");
        Assert.Equal([RemoteHostProbe.Command, Trial, Commit], host.Commands); // it ran, so it is committed, as before
    }

    [Fact]
    public async Task What_the_binary_reports_is_quoted_in_the_message()
    {
        RecordingExecTransport host = Host(new FakeExecReply(
            "{\"version\":\"0.11.0\\u001b[2J\",\"protocolMin\":1,\"protocolMax\":2,\"rid\":\"linux-x64\",\"path\":\"/home/nova/x\\ny\"}\n"));

        RemoteMuxInstallResult result = await Installer(host, new FakeAssetSource(new MuxDaemonAsset(Binary, "ab12", "x"))).InstallAsync(Ct);

        Assert.True(result.Success, result.Message);
        Assert.Equal("ntilde-mux 0.11.0[2J installed at /home/nova/xy", result.Message);
    }

    [Fact]
    public async Task Rc_file_noise_before_the_version_json_is_skipped()
    {
        RecordingExecTransport host = Host(new FakeExecReply("Welcome to box!\n\n" + InstalledJson));

        RemoteMuxInstallResult result = await Installer(host, new FakeAssetSource(new MuxDaemonAsset(Binary, "ab12", "x"))).InstallAsync(Ct);

        Assert.True(result.Success, result.Message);
        Assert.Equal(Installed, result.Installed);
    }

    [Fact]
    public async Task A_source_failure_is_the_result_and_nothing_is_uploaded()
    {
        RecordingExecTransport host = Host(new FakeExecReply(InstalledJson));
        var source = new FakeAssetSource(new MuxReleaseNotFoundException("0.10.0"));

        RemoteMuxInstallResult result = await Installer(host, source).InstallAsync(Ct);

        Assert.Equal(
            new RemoteMuxInstallResult(false, "No ntilde-mux release for 0.10.0 \u2014 choose a file or copy the install command", null) { ReleaseMissing = true },
            result);
        Assert.Equal([RemoteHostProbe.Command], host.Commands);
    }

    [Fact]
    public async Task An_empty_binary_is_never_uploaded()
    {
        RecordingExecTransport host = Host(new FakeExecReply(InstalledJson));

        RemoteMuxInstallResult result = await Installer(host, new FakeAssetSource(new MuxDaemonAsset([], "e3b0", "/tmp/empty"))).InstallAsync(Ct);

        Assert.Equal(new RemoteMuxInstallResult(false, "The ntilde-mux binary from /tmp/empty is empty", null), result);
        Assert.Equal([RemoteHostProbe.Command], host.Commands);
    }

    [Fact]
    public async Task Cancelling_throws_and_uploads_nothing()
    {
        RecordingExecTransport host = Host(new FakeExecReply(InstalledJson));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var source = new FakeAssetSource(new MuxDaemonAsset(Binary, "ab12", "x")) { OnGet = cts.Cancel };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Installer(host, source).InstallAsync(cts.Token));

        Assert.Equal([RemoteHostProbe.Command], host.Commands);
    }

    [Fact]
    public async Task A_transport_that_cannot_start_is_a_failure_not_an_exception()
    {
        var host = new RecordingExecTransport((_, _) => new FakeExecReply(UbuntuProbe))
        {
            OnStart = _ => throw new InvalidOperationException("ssh was not found"),
        };
        var source = new FakeAssetSource(new MuxDaemonAsset(Binary, "ab12", "x"));

        RemoteMuxInstallResult result = await Installer(host, source).InstallAsync(Ct);

        Assert.Equal(new RemoteMuxInstallResult(false, "Running a command on nova@fake-host failed: ssh was not found", null), result);
        Assert.Empty(source.RequestedRids);
    }

    [Fact]
    public async Task A_source_s_own_cancellation_is_a_failure_when_the_caller_did_not_cancel()
    {
        RecordingExecTransport host = Host(new FakeExecReply(InstalledJson));
        var source = new FakeAssetSource(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing."));

        RemoteMuxInstallResult result = await Installer(host, source).InstallAsync(Ct);

        Assert.False(result.Success);
        Assert.Contains("HttpClient.Timeout", result.Message, StringComparison.Ordinal);
        Assert.False(result.ReleaseMissing); // only a missing release says so
    }

    /// <summary>Task 23: "Copy install command" probes alone, for the RID; nothing is fetched or uploaded.</summary>
    [Fact]
    public async Task The_probe_alone_runs_one_command_and_names_the_platform()
    {
        RecordingExecTransport host = Host(new FakeExecReply(InstalledJson));
        var source = new FakeAssetSource(new MuxDaemonAsset(Binary, "ab12", "x"));

        RemoteHostProbeOutcome outcome = await Installer(host, source).ProbeAsync(Ct);

        Assert.Equal(new RemoteHostFacts("linux-x64", "/home/nova"), outcome);
        Assert.Equal([RemoteHostProbe.Command], host.Commands);
        Assert.Empty(source.RequestedRids);
        Assert.Equal([RemoteMuxInstallStep.Probing], _steps.Select(s => s.Step).Distinct());
        Assert.Contains(_steps, s => s.Message == "nova@fake-host: linux-x64, HOME=/home/nova");
    }

    [Fact]
    public async Task A_probe_that_cannot_run_is_a_refusal_with_the_reason()
    {
        var host = new RecordingExecTransport((_, _) => new FakeExecReply(UbuntuProbe))
        {
            OnStart = _ => throw new InvalidOperationException("ssh was not found"),
        };

        RemoteHostProbeOutcome outcome = await Installer(host, new FakeAssetSource(new MuxDaemonAsset(Binary, "ab12", "x"))).ProbeAsync(Ct);

        Assert.Equal(new RemoteHostRefusal("Running a command on nova@fake-host failed: ssh was not found"), outcome);
    }

    /// <summary>
    /// <paramref name="inner"/> for every command but the commit, which lands <paramref name="cancel"/> at a set point:
    /// <list type="bullet">
    /// <item><paramref name="commitFinishes"/>: its output and exit status (0) are ready from the start, and the cancel lands
    /// as the exec collects its result (reading its stderr, the last thing it does) - the commit has finished;</item>
    /// <item>otherwise: the cancel lands at stdin's EOF while the commit still runs, and it ends only when its channel is
    /// disposed, with no exit status - its result is never known.</item>
    /// </list>
    /// Either way the race the real transports leave open is closed, so the outcome does not depend on scheduling.
    /// </summary>
    private sealed class CommitHost(RecordingExecTransport inner, CancellationTokenSource cancel, bool commitFinishes) : ISshExecTransport
    {
        private int _commitRan;

        public string DisplayName => inner.DisplayName;

        public bool CommitRan => Volatile.Read(ref _commitRan) == 1;

        public ISshExecChannel Start(string remoteCommand, CancellationToken ct)
        {
            if (remoteCommand != Commit) return inner.Start(remoteCommand, ct);
            ct.ThrowIfCancellationRequested();
            Volatile.Write(ref _commitRan, 1);
            return commitFinishes ? new FinishedChannel(cancel) : new RunningChannel(cancel);
        }

        private sealed class RunningChannel : ISshExecChannel
        {
            private readonly TaskCompletionSource<int?> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public RunningChannel(CancellationTokenSource cancel)
            {
                Stdout = new OutputAtExit(_exit.Task);
                Stdin = new CancelAtEof(cancel);
            }

            public Stream Stdout { get; }

            public Stream Stdin { get; }

            public string StderrTail => string.Empty;

            public Task<int?> Completion => _exit.Task;

            public void Dispose() => _exit.TrySetResult(null);

            public void Abort() => Dispose();
        }

        /// <summary>No output until the command ends.</summary>
        private sealed class OutputAtExit(Task exited) : MemoryStream
        {
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                await exited.WaitAsync(cancellationToken).ConfigureAwait(false);
                return 0;
            }
        }

        /// <summary>The exec closing stdin (its EOF) is when the caller cancels.</summary>
        private sealed class CancelAtEof(CancellationTokenSource cancel) : MemoryStream
        {
            protected override void Dispose(bool disposing)
            {
                if (disposing) cancel.Cancel();
                base.Dispose(disposing);
            }
        }

        private sealed class FinishedChannel(CancellationTokenSource cancel) : ISshExecChannel
        {
            public Stream Stdout { get; } = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(InstalledJson));

            public Stream Stdin { get; } = new MemoryStream();

            public string StderrTail
            {
                get
                {
                    cancel.Cancel();
                    return string.Empty;
                }
            }

            public Task<int?> Completion { get; } = Task.FromResult<int?>(0);

            public void Dispose()
            {
            }

            public void Abort()
            {
            }
        }
    }

    /// <summary>Hands out one asset, or throws one exception, and keeps the RIDs it was asked for.</summary>
    private sealed class FakeAssetSource : IMuxDaemonAssetSource
    {
        private readonly MuxDaemonAsset? _asset;
        private readonly Exception? _failure;

        public FakeAssetSource(MuxDaemonAsset asset) => _asset = asset;

        public FakeAssetSource(Exception failure) => _failure = failure;

        public List<string> RequestedRids { get; } = [];

        /// <summary>Runs inside <see cref="GetAsync"/>, before it answers.</summary>
        public Action? OnGet { get; init; }

        public Task<MuxDaemonAsset> GetAsync(string rid, IProgress<long>? progress, CancellationToken ct)
        {
            RequestedRids.Add(rid);
            OnGet?.Invoke();
            ct.ThrowIfCancellationRequested();
            if (_failure is not null) throw _failure;
            progress?.Report(_asset!.Bytes.Length);
            return Task.FromResult(_asset!);
        }
    }

    private sealed class SyncProgress(Action<RemoteMuxInstallProgress> report) : IProgress<RemoteMuxInstallProgress>
    {
        public void Report(RemoteMuxInstallProgress value) => report(value);
    }
}
