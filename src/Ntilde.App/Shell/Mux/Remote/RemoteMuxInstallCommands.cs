using System.Globalization;

namespace Ntilde.Shell.Mux.Remote;

/// <summary>
/// The install flow's command lines (Phase 4 spec §9 steps 2(c) and 3, §2 decision 4). The upload runs
/// through the user's login shell, which may be bash, fish, tcsh or nushell, so it is one single-quoted
/// <c>sh -c</c> script with no single quote inside, as <see cref="RemoteMuxCommand"/>'s fallback is.
/// </summary>
internal static class RemoteMuxInstallCommands
{
    /// <summary>
    /// The upload of a binary of <paramref name="byteCount"/> bytes, streamed to its stdin and then EOF.
    /// Into <c>$HOME/.local/share/ntilde/bin</c> (<see cref="RemoteMuxCommand.DefaultRelativePath"/>), it:
    /// <list type="number">
    /// <item><c>cat</c>s stdin into a temp file beside the target, which a <c>trap</c> removes on any failure;</item>
    /// <item>checks the temp file has exactly <paramref name="byteCount"/> bytes;</item>
    /// <item><c>chmod 755</c>s it and runs it once (<c>--version --json</c>, output dropped);</item>
    /// <item><c>mv -f</c>s it over <c>ntilde-mux</c> - a rename, so a daemon running the old binary keeps its
    /// inode, where writing in place would fail with <c>ETXTBSY</c> or corrupt it;</item>
    /// <item>execs the installed binary's <c>--version --json</c>: its stdout is the verification (step 4).</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Steps 2 and 3 go beyond the spec's first draft, so that nothing ever replaces a working binary with
    /// one that cannot run. A cancelled or timed-out upload ends with stdin's EOF (the channel's dispose
    /// sends it before stopping the command), and <c>cat</c> takes a short read for the whole file: the
    /// size check stops that. The trial run stops a file the host cannot execute. <c>wc -c</c> pads its
    /// count on macOS; the unquoted <c>$n</c> drops the padding.
    /// </para>
    /// <para>
    /// The cleanup trap is written <c>trap "rm -f \"\$t\"" EXIT</c>: <c>$t</c> is expanded when the trap
    /// fires, quoted, so a <c>$HOME</c> holding a quote or a <c>$(…)</c> is neither a syntax error nor run.
    /// SIGPIPE is ignored, since a dropped connection makes the shell's last message to the gone stderr
    /// raise it and dash runs no EXIT trap on a signal; a failed write then ends the script through
    /// <c>set -e</c> instead, and the trap runs. SIGHUP and SIGTERM exit through the trap too.
    /// </para>
    /// <para>
    /// The script reads no output until its stdin ends and then prints one line, so streaming megabytes
    /// into it cannot deadlock on a full stdout pipe.
    /// </para>
    /// </remarks>
    public static string Upload(long byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteCount);
        string size = byteCount.ToString(CultureInfo.InvariantCulture);
        return "sh -c 'set -e; trap \"\" PIPE; trap \"exit 1\" HUP TERM; "
            + "d=\"$HOME/.local/share/ntilde/bin\"; mkdir -p \"$d\"; t=\"$d/.ntilde-mux.$$\"; "
            + "trap \"rm -f \\\"\\$t\\\"\" EXIT; cat > \"$t\"; "
            + $"n=$(wc -c < \"$t\"); [ $n -eq {size} ] || {{ echo \"ntilde-mux upload incomplete: expected {size} bytes\" >&2; exit 1; }}; "
            + "chmod 755 \"$t\"; \"$t\" --version --json > /dev/null; mv -f \"$t\" \"$d/ntilde-mux\"; trap - EXIT; "
            + "exec \"$d/ntilde-mux\" --version --json'";
    }

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
}
