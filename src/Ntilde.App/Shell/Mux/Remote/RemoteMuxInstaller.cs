using System.Globalization;
using System.Text.Json;
using Ntilde.Mux.Cli;
using Ntilde.Mux.Contracts;
using Ntilde.Platform.Ssh.Exec;
using Ntilde.Platform.Ssh.Models;
using Ntilde.VT;
using static Ntilde.Shell.Mux.Remote.RemoteOutputText;

namespace Ntilde.Shell.Mux.Remote;

/// <summary>Where <see cref="RemoteMuxInstaller.InstallAsync"/> is, for the dialog's step list.</summary>
internal enum RemoteMuxInstallStep
{
    Probing,
    Downloading,
    Uploading,
    Verifying,
    Done,
}

/// <summary>Bytes moved so far in <see cref="RemoteMuxInstallStep.Downloading"/> or <see cref="RemoteMuxInstallStep.Uploading"/>.</summary>
/// <param name="Total">The whole size; 0 while it is unknown (a download's).</param>
internal readonly record struct RemoteMuxInstallProgress(RemoteMuxInstallStep Step, long Done, long Total);

/// <summary>How an install ended.</summary>
/// <param name="Message">What the dialog shows: the installed version and path, or the reason it failed.</param>
/// <param name="Installed">What the installed binary reported; only on success.</param>
internal sealed record RemoteMuxInstallResult(bool Success, string Message, MuxVersionInfo? Installed);

/// <summary>
/// Installs <c>ntilde-mux</c> on a remote host (Phase 4 spec §9), every step over
/// <paramref name="transport"/> - the same exec transport, askpass and prompts as the host's persistent
/// tabs: probe the host (<see cref="RemoteHostProbe"/>), get the binary for its RID from
/// <paramref name="source"/>, upload it (<see cref="RemoteMuxInstallCommands.Upload"/>), and verify what the
/// installed binary reports. UI-free: <paramref name="report"/> gets each step with a line for the log.
/// </summary>
/// <remarks>
/// <para>
/// Every failure is a result with the reason, never an exception, except cancellation:
/// <see cref="InstallAsync"/> throws <see cref="OperationCanceledException"/> when its token is cancelled.
/// A cancelled upload never leaves a partial binary in place (see <see cref="RemoteMuxInstallCommands.Upload"/>).
/// </para>
/// <para>
/// <paramref name="report"/> and <see cref="Progress"/> are called synchronously on whatever thread the flow
/// is on - the caller's for the first report, usually the thread pool after that - so a UI marshals them.
/// </para>
/// <para>
/// The flow records nothing itself. On success the caller passes <see cref="RemoteMuxInstallResult.Installed"/>
/// to <see cref="Record"/> and saves the profile; <see cref="SshMuxOptions.PersistRemoteSessions"/> is the
/// user's to turn on.
/// </para>
/// </remarks>
internal sealed class RemoteMuxInstaller(ISshExecTransport transport, IMuxDaemonAssetSource source, Action<RemoteMuxInstallStep, string> report)
{
    /// <summary>The probe is three tiny commands; this bounds a stuck connect or prompt.</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);

    /// <summary>A few MB over a slow link, plus the trial run and the verification.</summary>
    public static readonly TimeSpan UploadTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Byte counts for the download and the upload, for a progress bar.</summary>
    public IProgress<RemoteMuxInstallProgress>? Progress { get; init; }

    /// <summary>Runs the whole flow. Call it off the UI thread or await it; nothing in it blocks the caller's thread.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task<RemoteMuxInstallResult> InstallAsync(CancellationToken ct)
    {
        RemoteHostProbeOutcome outcome = await ProbeAsync(ct).ConfigureAwait(false);
        if (outcome is RemoteHostRefusal refusal)
        {
            return Failed(refusal.Reason);
        }

        var facts = (RemoteHostFacts)outcome;
        report(RemoteMuxInstallStep.Downloading, $"Getting ntilde-mux for {facts.Rid}\u2026");
        MuxDaemonAsset asset;
        try
        {
            asset = await source.GetAsync(facts.Rid, ProgressOf(RemoteMuxInstallStep.Downloading, total: 0), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsFailure(ex, ct))
        {
            // The source's message is the reason: no release for this version, a checksum mismatch, no such file.
            return Failed(ex.Message);
        }

        if (asset.Bytes.Length == 0)
        {
            return Failed($"The ntilde-mux binary from {asset.Origin} is empty");
        }

        report(RemoteMuxInstallStep.Downloading, string.Create(CultureInfo.InvariantCulture,
            $"{asset.Origin}: {asset.Bytes.Length} bytes, SHA-256 {asset.Sha256Hex}"));

        report(RemoteMuxInstallStep.Uploading, $"Uploading to {transport.DisplayName}\u2026");
        SshExecResult upload;
        try
        {
            upload = await SshExec.RunAsync(
                transport,
                RemoteMuxInstallCommands.Upload(asset.Bytes.Length),
                asset.Bytes,
                ProgressOf(RemoteMuxInstallStep.Uploading, asset.Bytes.Length),
                UploadTimeout,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsFailure(ex, ct))
        {
            return ExecFailed(ex);
        }

        if (upload.ExitCode != 0)
        {
            string exit = upload.ExitCode is { } code ? $"exit {code.ToString(CultureInfo.InvariantCulture)}" : "no exit status";
            string tail = LastLines(upload.Stderr);
            return Failed(tail.Length == 0
                ? $"The upload to {transport.DisplayName} failed ({exit})"
                : $"The upload to {transport.DisplayName} failed ({exit}): {tail}");
        }

        report(RemoteMuxInstallStep.Verifying, "Verifying the installed ntilde-mux\u2026");
        if (ParseVersion(upload.Stdout) is not { } installed)
        {
            string printed = LastLines(upload.Stdout);
            return Failed(printed.Length == 0
                ? "ntilde-mux did not report its version"
                : $"ntilde-mux did not report its version: {printed}");
        }

        if (installed.ProtocolMin > MuxProtocol.MaxSupportedVersion || installed.ProtocolMax < MuxProtocol.MinSupportedVersion)
        {
            return Failed(string.Create(CultureInfo.InvariantCulture,
                $"ntilde-mux {Quote(installed.Version)} speaks protocol {installed.ProtocolMin}-{installed.ProtocolMax}; this app speaks {MuxProtocol.MinSupportedVersion}-{MuxProtocol.MaxSupportedVersion}"));
        }

        string done = $"ntilde-mux {Quote(installed.Version)} installed at {Quote(installed.Path)}";
        if (!string.Equals(installed.Rid, facts.Rid, StringComparison.Ordinal))
        {
            // It ran, twice, so it stays: a binary run under emulation, say. But it is not this host's build,
            // and the dialog says so.
            string note = $"it reports {Quote(installed.Rid)}, but the host is {facts.Rid}";
            TerminalLogger.Log($"[RemoteMuxInstaller] {transport.DisplayName}: {note}");
            report(RemoteMuxInstallStep.Verifying, $"Warning: {note}");
            done = $"{done} (warning: {note})";
        }

        report(RemoteMuxInstallStep.Done, done);
        return new RemoteMuxInstallResult(true, done, installed);
    }

    /// <summary>
    /// Step 1 alone (spec §9): what the host is, or why it cannot have ntilde-mux - a refusal, or a probe
    /// that never produced a result (its reason is the refusal's). <see cref="InstallAsync"/> starts with it;
    /// the dialog's "Copy install command" runs it for the RID when the profile has none recorded.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task<RemoteHostProbeOutcome> ProbeAsync(CancellationToken ct)
    {
        report(RemoteMuxInstallStep.Probing, $"Probing {transport.DisplayName}\u2026");
        SshExecResult probe;
        try
        {
            probe = await SshExec.RunAsync(transport, RemoteHostProbe.Command, ReadOnlyMemory<byte>.Empty, null, ProbeTimeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsFailure(ex, ct))
        {
            return new RemoteHostRefusal(ExecFailureReason(ex));
        }

        RemoteHostProbeOutcome outcome = RemoteHostProbe.Parse(probe);
        if (outcome is RemoteHostFacts facts)
        {
            report(RemoteMuxInstallStep.Probing, $"{transport.DisplayName}: {facts.Rid}, HOME={Quote(facts.Home)}");
        }

        return outcome;
    }

    /// <summary>Records what the install flow installed in the profile's options (spec §9 step 4): its path, version and RID.</summary>
    public static void Record(SshMuxOptions options, MuxVersionInfo installed)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(installed);
        options.RemoteDaemonPath = installed.Path;
        options.RemoteDaemonVersion = installed.Version;
        options.RemoteDaemonRid = installed.Rid;
    }

    /// <summary>
    /// The <c>--version --json</c> line: the last line of stdout that is a JSON object, since a login
    /// shell's rc files may print first. Null when there is none, or it lacks a version or a sane range.
    /// </summary>
    private static MuxVersionInfo? ParseVersion(string stdout)
    {
        string? line = stdout.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('{'));
        if (line is null) return null;

        MuxVersionInfo? info;
        try
        {
            info = JsonSerializer.Deserialize(line, MuxCliJsonContext.Default.MuxVersionInfo);
        }
        catch (JsonException)
        {
            return null;
        }

        return info is { Version.Length: > 0, Rid: not null, Path: not null } && info.ProtocolMin >= 1 && info.ProtocolMax >= info.ProtocolMin
            ? info
            : null;
    }

    private StepProgress? ProgressOf(RemoteMuxInstallStep step, long total) =>
        Progress is { } progress ? new StepProgress(progress, step, total) : null;

    private static RemoteMuxInstallResult Failed(string reason) => new(false, reason, null);

    /// <summary>Anything but the caller's own cancellation: a timeout, a transport that could not start, a source's error.</summary>
    private static bool IsFailure(Exception ex, CancellationToken ct) => ex is not OperationCanceledException || !ct.IsCancellationRequested;

    /// <summary>A command that never produced a result: it timed out (the message says so), or the transport could not run it.</summary>
    private RemoteMuxInstallResult ExecFailed(Exception ex) => Failed(ExecFailureReason(ex));

    private string ExecFailureReason(Exception ex) =>
        ex is TimeoutException ? ex.Message : $"Running a command on {transport.DisplayName} failed: {ex.Message}";

    /// <summary>Forwards a byte count, synchronously, as this step's progress.</summary>
    private sealed class StepProgress(IProgress<RemoteMuxInstallProgress> progress, RemoteMuxInstallStep step, long total) : IProgress<long>
    {
        public void Report(long value) => progress.Report(new RemoteMuxInstallProgress(step, value, total));
    }
}
