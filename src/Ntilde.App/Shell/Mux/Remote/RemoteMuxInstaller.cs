using System.Diagnostics.CodeAnalysis;
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
internal sealed record RemoteMuxInstallResult(bool Success, string Message, MuxVersionInfo? Installed)
{
    /// <summary>
    /// It failed because the source has no release for this app version (<see cref="MuxReleaseNotFoundException"/>):
    /// the release download, and the install command that points at it, cannot work for this version.
    /// </summary>
    public bool ReleaseMissing { get; init; }
}

/// <summary>
/// Installs <c>ntilde-mux</c> on a remote host (Phase 4 spec §9), every step over
/// <paramref name="transport"/> - the same exec transport, askpass and prompts as the host's persistent
/// tabs: probe the host (<see cref="RemoteHostProbe"/>), get the binary for its RID from
/// <paramref name="source"/>, upload and trial-run it under a temp name
/// (<see cref="RemoteMuxInstallCommands.UploadForTrial"/>), check what it reports, then move it over the
/// installed binary (<see cref="RemoteMuxInstallCommands.CommitUpload"/>) and verify what that reports. UI-free:
/// <paramref name="report"/> gets each step with a line for the log.
/// </summary>
/// <remarks>
/// <para>
/// Every failure is a result with the reason, never an exception, except cancellation:
/// <see cref="InstallAsync"/> throws <see cref="OperationCanceledException"/> when its token is cancelled.
/// </para>
/// <para>
/// A failed install never replaces a working <c>ntilde-mux</c> (spec §9 step 3). A short or cancelled upload,
/// or one the host cannot run, is removed by the upload script's own trap. A binary that runs but cannot serve
/// this app - no version line, or a protocol range with nothing in common - is turned away before the commit,
/// and its upload discarded (<see cref="RemoteMuxInstallCommands.DiscardUpload"/>); so is one the user cancels
/// after the upload. A binary that reports another RID is committed, with a warning, as it ran.
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

    /// <summary>A few MB over a slow link, plus the trial run.</summary>
    public static readonly TimeSpan UploadTimeout = TimeSpan.FromMinutes(5);

    /// <summary>The commit is a rename and the verification; as the probe's, this bounds a stuck connect or prompt.</summary>
    public static readonly TimeSpan CommitTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The discard is one <c>rm -f</c>, best effort, and may run after the user cancelled: a short bound.</summary>
    public static readonly TimeSpan DiscardTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Byte counts for the download and the upload, for a progress bar.</summary>
    public IProgress<RemoteMuxInstallProgress>? Progress { get; init; }

    /// <summary>The token that names an install's upload on the host (<see cref="RemoteMuxInstallCommands"/>); tests fix it.</summary>
    internal Func<Guid> NewUploadToken { get; init; } = Guid.NewGuid;

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
            return Failed(ex.Message) with { ReleaseMissing = ex is MuxReleaseNotFoundException };
        }

        if (asset.Bytes.Length == 0)
        {
            return Failed($"The ntilde-mux binary from {asset.Origin} is empty");
        }

        report(RemoteMuxInstallStep.Downloading, string.Create(CultureInfo.InvariantCulture,
            $"{asset.Origin}: {asset.Bytes.Length} bytes, SHA-256 {asset.Sha256Hex}"));

        report(RemoteMuxInstallStep.Uploading, $"Uploading to {transport.DisplayName}\u2026");
        Guid token = NewUploadToken();
        SshExecResult upload;
        try
        {
            upload = await SshExec.RunAsync(
                transport,
                RemoteMuxInstallCommands.UploadForTrial(asset.Bytes.Length, token),
                asset.Bytes,
                ProgressOf(RemoteMuxInstallStep.Uploading, asset.Bytes.Length),
                UploadTimeout,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsFailure(ex, ct))
        {
            // No discard, here or on a failed exit: the upload script's trap removes its temp file. Only a
            // reply lost after a trial run that succeeded leaves one, for the next upload's sweep.
            return ExecFailed(ex);
        }

        if (upload.ExitCode != 0)
        {
            return Failed(ExitFailure($"The upload to {transport.DisplayName} failed", upload));
        }

        // The host now holds the upload under the token's name, and nothing else has changed. Every way out
        // of here but a commit that ran discards it, so a binary turned away never replaces a working one.
        bool committed = false;
        try
        {
            report(RemoteMuxInstallStep.Verifying, "Verifying the uploaded ntilde-mux\u2026");
            if (!TryAccept(upload.Stdout, out _, out string? rejected))
            {
                return Failed(rejected);
            }

            ct.ThrowIfCancellationRequested();
            report(RemoteMuxInstallStep.Verifying, "Installing it\u2026");
            SshExecResult commit;
            try
            {
                commit = await SshExec.RunAsync(
                    transport, RemoteMuxInstallCommands.CommitUpload(token), ReadOnlyMemory<byte>.Empty, null, CommitTimeout, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsFailure(ex, ct))
            {
                return ExecFailed(ex);
            }

            // The commit's result is in: a cancel that lands now changes nothing. The upload already replaced ntilde-mux,
            // so the install is verified and reported (and the caller records it); only a cancel the exec sees before the
            // result leaves the commit unknown, and discards (spec §9 step 3).
            if (commit.ExitCode != 0)
            {
                return Failed(ExitFailure($"Installing ntilde-mux on {transport.DisplayName} failed", commit));
            }

            committed = true;
            return Verified(commit.Stdout, facts);
        }
        finally
        {
            if (!committed)
            {
                await DiscardAsync(token).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Spec §9 step 4, on the committed binary's <c>--version --json</c>: the checks the trial passed, again (it
    /// is the same file, now at its installed path), and the RID it reports against the probe's.
    /// </summary>
    private RemoteMuxInstallResult Verified(string stdout, RemoteHostFacts facts)
    {
        if (!TryAccept(stdout, out MuxVersionInfo? installed, out string? rejected))
        {
            return Failed(rejected);
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
    /// Whether <paramref name="stdout"/> holds the <c>--version --json</c> of an ntilde-mux this app can use:
    /// a version line (<see cref="ParseVersion"/>) whose protocol range overlaps this app's. Otherwise
    /// <paramref name="rejection"/> is the reason the dialog shows.
    /// </summary>
    private static bool TryAccept(
        string stdout,
        [NotNullWhen(true)] out MuxVersionInfo? info,
        [NotNullWhen(false)] out string? rejection)
    {
        info = ParseVersion(stdout);
        if (info is null)
        {
            string printed = LastLines(stdout);
            rejection = printed.Length == 0
                ? "ntilde-mux did not report its version"
                : $"ntilde-mux did not report its version: {printed}";
            return false;
        }

        if (info.ProtocolMin > MuxProtocol.MaxSupportedVersion || info.ProtocolMax < MuxProtocol.MinSupportedVersion)
        {
            rejection = string.Create(CultureInfo.InvariantCulture,
                $"ntilde-mux {Quote(info.Version)} speaks protocol {info.ProtocolMin}-{info.ProtocolMax}; this app speaks {MuxProtocol.MinSupportedVersion}-{MuxProtocol.MaxSupportedVersion}");
            info = null;
            return false;
        }

        rejection = null;
        return true;
    }

    /// <summary>
    /// Removes an upload that was never committed (spec §9 step 3). Best effort: it runs even after the
    /// caller cancelled, under <see cref="DiscardTimeout"/>, and a failure is only logged, so the result stays
    /// the reason the install stopped. What it cannot remove, the next upload's sweep does.
    /// </summary>
    private async Task DiscardAsync(Guid token)
    {
        string failure;
        try
        {
            SshExecResult discard = await SshExec.RunAsync(
                transport, RemoteMuxInstallCommands.DiscardUpload(token), ReadOnlyMemory<byte>.Empty, null, DiscardTimeout, CancellationToken.None).ConfigureAwait(false);
            if (discard.ExitCode == 0)
            {
                return;
            }

            failure = ExitFailure("failed", discard);
        }
        catch (Exception ex)
        {
            failure = $"failed: {ex.Message}";
        }

        TerminalLogger.Log($"[RemoteMuxInstaller] {transport.DisplayName}: removing the uploaded ntilde-mux {failure}");
    }

    /// <summary><paramref name="what"/>, then the exit status and the stderr tail of a command that did not exit 0.</summary>
    private static string ExitFailure(string what, SshExecResult result)
    {
        string exit = result.ExitCode is { } code ? $"exit {code.ToString(CultureInfo.InvariantCulture)}" : "no exit status";
        string tail = LastLines(result.Stderr);
        return tail.Length == 0 ? $"{what} ({exit})" : $"{what} ({exit}): {tail}";
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
