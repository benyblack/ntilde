using Ntilde.Shell.Mux.Remote;

namespace Ntilde.Tests.Shell.Mux.Remote;

/// <summary>
/// The install flow's command lines (Phase 4 spec §9, §2 decision 4; plan Review Focus 3): the upload that
/// trial-runs the binary under a temp name, the commit that moves it over atomically and verifies it, the
/// discard that drops it, and the offline one-liner for the clipboard.
/// </summary>
public sealed class RemoteMuxInstallCommandsTests
{
    private static readonly Guid Token = new("5f2c1e0a9b8d4c7e8f6a3b2d1c0e9f8a");
    private const string TempName = ".ntilde-mux.upload-5f2c1e0a9b8d4c7e8f6a3b2d1c0e9f8a";

    /// <summary>
    /// sshd hands a command to the login shell, which may be fish, tcsh or nushell: <c>sh -c '…'</c> with no
    /// single quote inside is one word every shell passes to sh untouched.
    /// </summary>
    internal static void AssertOneSingleQuotedShScript(string command)
    {
        Assert.StartsWith("sh -c '", command, StringComparison.Ordinal);
        Assert.EndsWith("'", command, StringComparison.Ordinal);
        Assert.Equal(2, command.Count(c => c == '\''));
    }

    [Fact]
    public void UploadForTrial_sweeps_old_uploads_streams_into_the_token_s_temp_file_checks_its_size_and_trial_runs_it()
    {
        Assert.Equal(
            "sh -c 'set -e; trap \"\" PIPE; trap \"exit 1\" HUP TERM; "
            + RemoteInstallDir.Assign + "mkdir -p \"$d\"; "
            + "find \"$d\" -maxdepth 1 -name \".ntilde-mux.upload-*\" -mmin +60 -exec rm -f {} + 2>/dev/null || :; "
            + "t=\"$d/" + TempName + "\"; trap \"rm -f \\\"\\$t\\\"\" EXIT; cat > \"$t\"; "
            + "n=$(wc -c < \"$t\"); [ $n -eq 1834567 ] || { echo \"ntilde-mux upload incomplete: expected 1834567 bytes\" >&2; exit 1; }; "
            + "chmod 755 \"$t\"; \"$t\" --version --json; trap - EXIT'",
            RemoteMuxInstallCommands.UploadForTrial(1834567, Token));
    }

    [Fact]
    public void UploadForTrial_never_touches_the_installed_binary()
    {
        string upload = RemoteMuxInstallCommands.UploadForTrial(10, Token);

        // The greptile P1: a binary that runs but is not this app's must not have replaced a working one by the
        // time the app reads its version. Only CommitUpload moves anything over ntilde-mux.
        Assert.DoesNotContain("mv ", upload, StringComparison.Ordinal);
        Assert.DoesNotContain("\"$d/ntilde-mux\"", upload, StringComparison.Ordinal);
        Assert.DoesNotContain("; exec ", upload, StringComparison.Ordinal);
    }

    [Fact]
    public void UploadForTrial_keeps_the_temp_file_only_when_the_trial_run_succeeded()
    {
        string upload = RemoteMuxInstallCommands.UploadForTrial(10, Token);

        // The cleanup trap is armed before cat and disarmed only as the last step, after the trial run: any
        // failure before it (short read, chmod, a binary that cannot run) removes the temp file.
        int arm = upload.IndexOf("trap \"rm -f \\\"\\$t\\\"\" EXIT;", StringComparison.Ordinal);
        int cat = upload.IndexOf("cat > \"$t\";", StringComparison.Ordinal);
        int trial = upload.IndexOf("\"$t\" --version --json;", StringComparison.Ordinal);
        int disarm = upload.IndexOf("trap - EXIT", StringComparison.Ordinal);
        Assert.InRange(arm, 0, cat);
        Assert.InRange(cat, arm, trial);
        Assert.InRange(trial, cat, disarm);
        Assert.EndsWith("trap - EXIT'", upload, StringComparison.Ordinal);
    }

    [Fact]
    public void UploadForTrial_s_cleanup_trap_expands_the_temp_path_when_it_fires()
    {
        string upload = RemoteMuxInstallCommands.UploadForTrial(10, Token);

        // Deferred: the trap's text is rm -f "$t", so a HOME holding a quote or a $(...) is neither a
        // syntax error when the trap fires nor run. The eager form re-parses the expanded path.
        Assert.Contains("trap \"rm -f \\\"\\$t\\\"\" EXIT;", upload, StringComparison.Ordinal);
        Assert.DoesNotContain("trap \"rm -f \\\"$t\\\"\" EXIT;", upload, StringComparison.Ordinal);
    }

    [Fact]
    public void UploadForTrial_ignores_SIGPIPE_and_exits_through_the_trap_on_HUP_and_TERM_before_anything_is_written()
    {
        string upload = RemoteMuxInstallCommands.UploadForTrial(10, Token);

        // dash runs no EXIT trap on a signal: a dropped connection's SIGPIPE would leave the temp file.
        int pipe = upload.IndexOf("trap \"\" PIPE;", StringComparison.Ordinal);
        int hupTerm = upload.IndexOf("trap \"exit 1\" HUP TERM;", StringComparison.Ordinal);
        int sweep = upload.IndexOf("find ", StringComparison.Ordinal);
        int cat = upload.IndexOf("cat > ", StringComparison.Ordinal);
        Assert.InRange(pipe, 0, sweep);
        Assert.InRange(hupTerm, 0, sweep);
        Assert.InRange(sweep, 0, cat);
    }

    [Fact]
    public void UploadForTrial_s_sweep_is_best_effort_and_spares_a_live_upload()
    {
        string upload = RemoteMuxInstallCommands.UploadForTrial(10, Token);

        // Only this directory's upload temps, only old ones (an upload lives minutes; another window's may be
        // between its steps), and a find that cannot do it fails nothing under set -e.
        Assert.Contains(
            "find \"$d\" -maxdepth 1 -name \".ntilde-mux.upload-*\" -mmin +60 -exec rm -f {} + 2>/dev/null || :;",
            upload,
            StringComparison.Ordinal);
        Assert.StartsWith(RemoteMuxInstallCommands.UploadTempPrefix, TempName, StringComparison.Ordinal);
    }

    [Fact]
    public void CommitUpload_moves_the_token_s_temp_file_over_the_installed_binary_and_reports_its_version()
    {
        Assert.Equal(
            "sh -c 'set -e; " + RemoteInstallDir.Assign + "t=\"$d/" + TempName + "\"; "
            + "mv -f \"$t\" \"$d/ntilde-mux\"; exec \"$d/ntilde-mux\" --version --json'",
            RemoteMuxInstallCommands.CommitUpload(Token));
    }

    [Fact]
    public void DiscardUpload_removes_the_token_s_temp_file_and_nothing_else()
    {
        Assert.Equal(
            "sh -c '" + RemoteInstallDir.Assign + "t=\"$d/" + TempName + "\"; rm -f \"$t\"'",
            RemoteMuxInstallCommands.DiscardUpload(Token));
    }

    [Fact]
    public void The_three_steps_name_the_same_temp_file()
    {
        string temp = $"t=\"$d/{TempName}\";";

        Assert.Contains(temp, RemoteMuxInstallCommands.UploadForTrial(10, Token), StringComparison.Ordinal);
        Assert.Contains(temp, RemoteMuxInstallCommands.CommitUpload(Token), StringComparison.Ordinal);
        Assert.Contains(temp, RemoteMuxInstallCommands.DiscardUpload(Token), StringComparison.Ordinal);
        Assert.DoesNotContain(TempName, RemoteMuxInstallCommands.CommitUpload(Guid.NewGuid()), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(64L * 1024 * 1024)]
    public void Each_step_is_one_single_quoted_sh_script(long size)
    {
        // A Guid's "N" form is 32 hex digits: nothing in the token can end the single-quoted word.
        foreach (Guid token in new[] { Token, Guid.NewGuid(), Guid.Empty })
        {
            AssertOneSingleQuotedShScript(RemoteMuxInstallCommands.UploadForTrial(size, token));
            AssertOneSingleQuotedShScript(RemoteMuxInstallCommands.CommitUpload(token));
            AssertOneSingleQuotedShScript(RemoteMuxInstallCommands.DiscardUpload(token));
        }
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void UploadForTrial_needs_a_binary(long size)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RemoteMuxInstallCommands.UploadForTrial(size, Token));
    }

    [Fact]
    public void Each_step_works_in_the_directory_the_remote_command_runs_from()
    {
        const string dir = RemoteInstallDir.Assign;
        Assert.Contains(dir, RemoteMuxInstallCommands.UploadForTrial(10, Token), StringComparison.Ordinal);
        Assert.Contains(dir, RemoteMuxInstallCommands.CommitUpload(Token), StringComparison.Ordinal);
        Assert.Contains(dir, RemoteMuxInstallCommands.DiscardUpload(Token), StringComparison.Ordinal);
        Assert.Equal(
            "case \"${XDG_DATA_HOME-}\" in /*) d=\"$XDG_DATA_HOME/ntilde/bin\";; *) d=\"$HOME/.local/share/ntilde/bin\";; esac; ",
            dir);
    }

    [Theory]
    [InlineData("/tmp/x", "/tmp/x/ntilde/bin")]
    [InlineData("rel", "/home/nova/.local/share/ntilde/bin")]
    [InlineData("", "/home/nova/.local/share/ntilde/bin")]
    [InlineData(null, "/home/nova/.local/share/ntilde/bin")]
    public void The_install_dir_follows_an_absolute_XDG_DATA_HOME_else_HOME(string? xdg, string expected)
    {
        var env = new Dictionary<string, string?> { ["XDG_DATA_HOME"] = xdg, ["HOME"] = "/home/nova" };

        (int exit, string stdout, string stderr) = PosixShell.Run("sh -c '" + RemoteInstallDir.Assign + "echo \"$d\"'", env);

        Assert.True(exit == 0, stderr);
        Assert.Equal(expected, stdout.Trim());
    }

    [Fact]
    public void Commit_and_discard_use_the_XDG_install_dir()
    {
        string data = Path.Combine(Path.GetTempPath(), "ntilde-xdg-" + Guid.NewGuid().ToString("N"));
        try
        {
            PosixShell.Require();
            string bin = Path.Combine(data, "ntilde", "bin");
            PosixShell.WriteEchoScript(Path.Combine(bin, RemoteMuxInstallCommands.UploadTempPrefix + Token.ToString("N")));
            var env = new Dictionary<string, string?> { ["XDG_DATA_HOME"] = PosixShell.ToShellPath(data), ["HOME"] = "/nonexistent" };

            (int exit, string stdout, string stderr) = PosixShell.Run(RemoteMuxInstallCommands.CommitUpload(Token), env);

            Assert.True(exit == 0, stderr);
            Assert.Contains("--version --json", stdout, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(bin, "ntilde-mux")));
        }
        finally
        {
            if (Directory.Exists(data)) Directory.Delete(data, recursive: true);
        }
    }

    [Fact]
    public void OfflineOneLiner_for_linux_uses_sha256sum()
    {
        Assert.Equal(
            "sh -c '" + RemoteInstallDir.Assign + "mkdir -p \"$d\" && cd \"$d\" && "
            + "curl -fsSLo ntilde-mux.new https://github.com/benyblack/ntilde/releases/download/v0.11.0/ntilde-mux-linux-x64 && "
            + "curl -fsSL https://github.com/benyblack/ntilde/releases/download/v0.11.0/ntilde-mux-linux-x64.sha256 "
            + "| sed \"s/ .*/  ntilde-mux.new/\" | sha256sum -c - && chmod 755 ntilde-mux.new && mv -f ntilde-mux.new ntilde-mux && echo \"installed to $d/ntilde-mux\"'",
            RemoteMuxInstallCommands.OfflineOneLiner("0.11.0", "linux-x64"));
    }

    [Fact]
    public void OfflineOneLiner_for_macos_uses_shasum()
    {
        Assert.Equal(
            "sh -c '" + RemoteInstallDir.Assign + "mkdir -p \"$d\" && cd \"$d\" && "
            + "curl -fsSLo ntilde-mux.new https://github.com/benyblack/ntilde/releases/download/v0.11.0/ntilde-mux-osx-arm64 && "
            + "curl -fsSL https://github.com/benyblack/ntilde/releases/download/v0.11.0/ntilde-mux-osx-arm64.sha256 "
            + "| sed \"s/ .*/  ntilde-mux.new/\" | shasum -a 256 -c - && chmod 755 ntilde-mux.new && mv -f ntilde-mux.new ntilde-mux && echo \"installed to $d/ntilde-mux\"'",
            RemoteMuxInstallCommands.OfflineOneLiner("0.11.0", "osx-arm64"));
    }

    [Theory]
    [InlineData("linux-x64")]
    [InlineData("osx-arm64")]
    public void OfflineOneLiner_is_one_single_quoted_sh_script(string rid)
    {
        // Pasted into fish or zsh as well as bash: the sed program is double-quoted so none ends the script early.
        AssertOneSingleQuotedShScript(RemoteMuxInstallCommands.OfflineOneLiner("0.11.0", rid));
    }

    [Fact]
    public void OfflineOneLiner_for_linux_arm64_names_its_asset()
    {
        string line = RemoteMuxInstallCommands.OfflineOneLiner("0.12.1", "linux-arm64");

        Assert.Contains(GitHubReleaseMuxAssetSource.AssetUrl("0.12.1", "linux-arm64") + " ", line, StringComparison.Ordinal);
        Assert.Contains(GitHubReleaseMuxAssetSource.AssetUrl("0.12.1", "linux-arm64") + ".sha256 ", line, StringComparison.Ordinal);
        Assert.Contains("| sha256sum -c - ", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "linux-x64")]
    [InlineData("0.11.0; rm -rf ~", "linux-x64")]
    [InlineData("0.11.0 ", "linux-x64")]
    [InlineData("../0.11.0", "linux-x64")]
    [InlineData("0.11.0", "")]
    [InlineData("0.11.0", "freebsd-x64")]
    [InlineData("0.11.0", "osx-x64")]
    [InlineData("0.11.0", "linux-x64 && id")]
    public void OfflineOneLiner_takes_only_a_release_version_and_a_published_rid(string version, string rid)
    {
        Assert.Throws<ArgumentException>(() => RemoteMuxInstallCommands.OfflineOneLiner(version, rid));
    }
}
