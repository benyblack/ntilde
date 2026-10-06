using System.Globalization;

namespace Ntilde.Shell.Mux.Remote;

/// <summary>
/// The install flow's command lines (Phase 4 spec §9 steps 2(c) and 3, §2 decision 4). Each step runs
/// through the user's login shell, which may be bash, fish, tcsh or nushell, so it is one single-quoted
/// <c>sh -c</c> script with no single quote inside, as <see cref="RemoteMuxCommand"/>'s fallback is.
/// </summary>
/// <remarks>
/// The install is two execs (spec §9 step 3), so that nothing replaces a working <c>ntilde-mux</c> before the
/// app has read the new binary's <c>--version --json</c>: <see cref="UploadForTrial"/> uploads and trial-runs
/// it under a temp name, and then either <see cref="CommitUpload"/> moves it over the installed binary or
/// <see cref="DiscardUpload"/> drops it. The three name the temp file by the same app-generated token, a
/// <see cref="Guid"/> in its <c>"N"</c> form: 32 hex digits, so it is safe inside the single-quoted script.
/// </remarks>
internal static class RemoteMuxInstallCommands
{
    /// <summary>
    /// The upload's temp file is this, then the token, beside the installed binary (in
    /// <c>$HOME/.local/share/ntilde/bin</c>, <see cref="RemoteMuxCommand.DefaultRelativePath"/>'s directory).
    /// </summary>
    public const string UploadTempPrefix = ".ntilde-mux.upload-";

    /// <summary>
    /// Step 1, the upload of a binary of <paramref name="byteCount"/> bytes, streamed to its stdin and then EOF.
    /// It commits nothing. In <c>$HOME/.local/share/ntilde/bin</c>, it:
    /// <list type="number">
    /// <item>removes upload temp files more than an hour old, which interrupted installs left (best effort);</item>
    /// <item><c>cat</c>s stdin into the token's temp file, which a <c>trap</c> removes on any failure;</item>
    /// <item>checks the temp file has exactly <paramref name="byteCount"/> bytes;</item>
    /// <item><c>chmod 755</c>s it and runs it once, <c>--version --json</c>: that JSON is the script's stdout,
    /// for the app to validate;</item>
    /// <item>disarms the trap, so the temp file is kept for <see cref="CommitUpload"/> or <see cref="DiscardUpload"/>.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// <para>
    /// A cancelled or timed-out upload ends with stdin's EOF (the channel's dispose sends it before stopping
    /// the command), and <c>cat</c> takes a short read for the whole file: the size check stops that. The trial
    /// run stops a file the host cannot execute. <c>wc -c</c> pads its count on macOS; the unquoted <c>$n</c>
    /// drops the padding.
    /// </para>
    /// <para>
    /// The cleanup trap is written <c>trap "rm -f \"\$t\"" EXIT</c>: <c>$t</c> is expanded when the trap
    /// fires, quoted, so a <c>$HOME</c> holding a quote or a <c>$(…)</c> is neither a syntax error nor run.
    /// SIGPIPE is ignored, since a dropped connection makes the shell's last message to the gone stderr
    /// raise it and dash runs no EXIT trap on a signal; a failed write then ends the script through
    /// <c>set -e</c> instead, and the trap runs. SIGHUP and SIGTERM exit through the trap too.
    /// </para>
    /// <para>
    /// The sweep (<c>find -maxdepth 1 -name … -mmin +60 -exec rm -f {} +</c>, which GNU, BSD/macOS and busybox
    /// <c>find</c> all take) spares a live upload: one lives minutes, so another window's may be between its
    /// steps. It is what removes a temp file the trap could not: a script killed outright, or one whose trial
    /// succeeded after the app gave up on it (a cancel, a timeout or a lost link after the last byte). A
    /// <c>find</c> that cannot do it fails nothing (<c>|| :</c> under <c>set -e</c>).
    /// </para>
    /// <para>
    /// The script reads no output until its stdin ends and then prints one line, so streaming megabytes
    /// into it cannot deadlock on a full stdout pipe.
    /// </para>
    /// </remarks>
    public static string UploadForTrial(long byteCount, Guid token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteCount);
        string size = byteCount.ToString(CultureInfo.InvariantCulture);
        return "sh -c 'set -e; trap \"\" PIPE; trap \"exit 1\" HUP TERM; " + DirectoryVariable + "mkdir -p \"$d\"; "
            + $"find \"$d\" -maxdepth 1 -name \"{UploadTempPrefix}*\" -mmin +60 -exec rm -f {{}} + 2>/dev/null || :; "
            + TempFileVariable(token) + "trap \"rm -f \\\"\\$t\\\"\" EXIT; cat > \"$t\"; "
            + $"n=$(wc -c < \"$t\"); [ $n -eq {size} ] || {{ echo \"ntilde-mux upload incomplete: expected {size} bytes\" >&2; exit 1; }}; "
            + "chmod 755 \"$t\"; \"$t\" --version --json; trap - EXIT'";
    }

    /// <summary>
    /// Step 2a, once the app accepted the trial's version: <c>mv -f</c>s the token's temp file over
    /// <c>ntilde-mux</c> - a rename, so it is atomic and a daemon running the old binary keeps its inode, where
    /// writing in place would fail with <c>ETXTBSY</c> or corrupt it - then execs the installed binary's
    /// <c>--version --json</c>: its stdout is the verification (spec §9 step 4). A temp file that is gone fails
    /// the <c>mv</c>, and nothing is installed.
    /// </summary>
    public static string CommitUpload(Guid token) =>
        "sh -c 'set -e; " + DirectoryVariable + TempFileVariable(token) + "mv -f \"$t\" \"$d/ntilde-mux\"; exec \"$d/ntilde-mux\" --version --json'";

    /// <summary>Step 2b, when the app turned the trial down or the user cancelled after it: removes the token's temp file.</summary>
    public static string DiscardUpload(Guid token) =>
        "sh -c '" + DirectoryVariable + TempFileVariable(token) + "rm -f \"$t\"'";

    /// <summary>
    /// The offline install command for the clipboard (spec §9 step 2(c)): it downloads the release asset on
    /// the host itself, checks it against its <c>.sha256</c> (<c>shasum -a 256</c> on macOS, which has no
    /// <c>sha256sum</c>), then <c>chmod</c>s it and moves it over the installed name. The user pastes it into
    /// their own shell, so it is not wrapped in <c>sh -c</c>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="version"/> is not a release version, or <paramref name="rid"/> is not a published RID.</exception>
    public static string OfflineOneLiner(string version, string rid)
    {
        // Both land unquoted in a command line the user runs.
        if (!MuxDaemonAsset.IsPlainName(version)) throw new ArgumentException($"Not a release version: \"{version}\"", nameof(version));
        if (!MuxDaemonRid.IsKnown(rid)) throw new ArgumentException($"Not a published runtime identifier: \"{rid}\"", nameof(rid));

        string url = GitHubReleaseMuxAssetSource.AssetUrl(version, rid);
        string check = rid == MuxDaemonRid.OsxArm64 ? "shasum -a 256 -c -" : "sha256sum -c -";
        return "mkdir -p ~/.local/share/ntilde/bin && cd ~/.local/share/ntilde/bin && "
            + $"curl -fsSLo ntilde-mux.new {url} && "
            + $"curl -fsSL {url}.sha256 | sed 's/ .*/  ntilde-mux.new/' | {check} && "
            + "chmod 755 ntilde-mux.new && mv -f ntilde-mux.new ntilde-mux";
    }

    /// <summary>Sets <c>$d</c>, the directory <see cref="RemoteMuxCommand.DefaultRelativePath"/> names.</summary>
    private const string DirectoryVariable = "d=\"$HOME/.local/share/ntilde/bin\"; ";

    /// <summary>Sets <c>$t</c>, the token's temp file in <c>$d</c>.</summary>
    private static string TempFileVariable(Guid token) =>
        $"t=\"$d/{UploadTempPrefix}{token.ToString("N", CultureInfo.InvariantCulture)}\"; ";
}
