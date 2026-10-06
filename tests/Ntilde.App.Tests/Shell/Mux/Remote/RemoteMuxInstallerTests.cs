using Ntilde.Mux.Cli;
using Ntilde.Platform.Ssh.Models;
using Ntilde.Shell.Mux.Remote;

namespace Ntilde.Tests.Shell.Mux.Remote;

/// <summary>
/// <see cref="RemoteMuxInstaller"/> (Phase 4 spec §9): probe, asset, upload over the exec channel,
/// verify, record. The host is a <see cref="RecordingExecTransport"/> that keeps every command and the
/// bytes its stdin got; the asset is a fake.
/// </summary>
public sealed class RemoteMuxInstallerTests
{
    private const string UbuntuProbe = "Linux x86_64\nldd (Ubuntu GLIBC 2.35-0ubuntu3.8) 2.35\nHOME=/home/nova\n";
    private const string InstalledPath = "/home/nova/.local/share/ntilde/bin/ntilde-mux";
    private const string InstalledJson =
        "{\"version\":\"0.11.0\",\"protocolMin\":1,\"protocolMax\":2,\"rid\":\"linux-x64\",\"path\":\"" + InstalledPath + "\"}\n";

    private static readonly MuxVersionInfo Installed = new("0.11.0", 1, 2, "linux-x64", InstalledPath);
    private static readonly byte[] Binary = MakeBinary(150 * 1024 + 17);

    private readonly List<(RemoteMuxInstallStep Step, string Message)> _steps = [];
    private readonly List<RemoteMuxInstallProgress> _progress = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] MakeBinary(int size)
    {
        byte[] bytes = new byte[size];
        new Random(7).NextBytes(bytes);
        return bytes;
    }

    /// <summary>A host that answers the probe with <paramref name="probe"/> and the upload with <paramref name="upload"/>.</summary>
    private static RecordingExecTransport Host(FakeExecReply upload, string probe = UbuntuProbe) =>
        new((command, _) => command == RemoteHostProbe.Command ? new FakeExecReply(probe) : upload);

    private RemoteMuxInstaller Installer(RecordingExecTransport host, IMuxDaemonAssetSource source) =>
        new(host, source, (step, message) => { lock (_steps) _steps.Add((step, message)); })
        {
            Progress = new SyncProgress(p => { lock (_progress) _progress.Add(p); }),
        };

    [Fact]
    public async Task Installs_through_the_exec_channel_and_verifies()
    {
        RecordingExecTransport host = Host(new FakeExecReply(InstalledJson));
        var source = new FakeAssetSource(new MuxDaemonAsset(Binary, "ab12", "https://example.test/ntilde-mux-linux-x64"));

        RemoteMuxInstallResult result = await Installer(host, source).InstallAsync(Ct);

        Assert.True(result.Success, result.Message);
        Assert.Equal(Installed, result.Installed);
        Assert.Equal($"ntilde-mux 0.11.0 installed at {InstalledPath}", result.Message);
        Assert.Equal([RemoteHostProbe.Command, RemoteMuxInstallCommands.Upload(Binary.Length)], host.Commands);
        Assert.Empty(host.Runs[0].Stdin);
        Assert.Equal(Binary, host.Runs[1].Stdin);
        Assert.Equal(["linux-x64"], source.RequestedRids);
        Assert.Equal(
            [RemoteMuxInstallStep.Probing, RemoteMuxInstallStep.Downloading, RemoteMuxInstallStep.Uploading, RemoteMuxInstallStep.Verifying, RemoteMuxInstallStep.Done],
            _steps.Select(s => s.Step).Distinct());
        Assert.Contains(_steps, s => s.Step == RemoteMuxInstallStep.Downloading && s.Message.Contains("ab12", StringComparison.Ordinal));
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
    }

    [Fact]
    public async Task A_lost_link_during_the_upload_is_a_failure()
    {
        RecordingExecTransport host = Host(new FakeExecReply(Stderr: "native ssh: connection lost\n", ExitCode: null));

        RemoteMuxInstallResult result = await Installer(host, new FakeAssetSource(new MuxDaemonAsset(Binary, "ab12", "x"))).InstallAsync(Ct);

        Assert.Equal(new RemoteMuxInstallResult(false, "The upload to nova@fake-host failed (no exit status): native ssh: connection lost", null), result);
    }

    [Fact]
    public async Task Protocol_disjoint_install_fails()
    {
        RecordingExecTransport host = Host(new FakeExecReply(
            "{\"version\":\"0.20.0\",\"protocolMin\":3,\"protocolMax\":4,\"rid\":\"linux-x64\",\"path\":\"" + InstalledPath + "\"}\n"));

        RemoteMuxInstallResult result = await Installer(host, new FakeAssetSource(new MuxDaemonAsset(Binary, "ab12", "x"))).InstallAsync(Ct);

        Assert.Equal(new RemoteMuxInstallResult(false, "ntilde-mux 0.20.0 speaks protocol 3-4; this app speaks 1-2", null), result);
        Assert.DoesNotContain(_steps, s => s.Step == RemoteMuxInstallStep.Done);
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

    [Theory]
    [InlineData("")]
    [InlineData("ntilde-mux 0.11.0 (protocol 1-2, linux-x64)\n")]
    [InlineData("{\"version\":\"0.11.0\",\"protocolMin\":1}\n")]
    [InlineData("{not json\n")]
    public async Task An_upload_that_does_not_report_a_version_fails(string stdout)
    {
        RecordingExecTransport host = Host(new FakeExecReply(stdout));

        RemoteMuxInstallResult result = await Installer(host, new FakeAssetSource(new MuxDaemonAsset(Binary, "ab12", "x"))).InstallAsync(Ct);

        Assert.False(result.Success);
        Assert.Null(result.Installed);
        Assert.StartsWith("ntilde-mux did not report its version", result.Message, StringComparison.Ordinal);
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
