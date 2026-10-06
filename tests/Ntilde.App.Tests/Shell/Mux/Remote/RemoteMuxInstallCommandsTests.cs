using Ntilde.Shell.Mux.Remote;

namespace Ntilde.Tests.Shell.Mux.Remote;

/// <summary>
/// The install flow's command lines (Phase 4 spec §9, §2 decision 4; plan Review Focus 3): the upload
/// that replaces the binary atomically and verifies it, and the offline one-liner for the clipboard.
/// </summary>
public sealed class RemoteMuxInstallCommandsTests
{
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
    public void Upload_streams_into_a_temp_file_checks_its_size_trial_runs_it_then_moves_it_over_and_verifies()
    {
        Assert.Equal(
            "sh -c 'set -e; d=\"$HOME/.local/share/ntilde/bin\"; mkdir -p \"$d\"; t=\"$d/.ntilde-mux.$$\"; "
            + "trap \"rm -f \\\"$t\\\"\" EXIT; cat > \"$t\"; "
            + "n=$(wc -c < \"$t\"); [ $n -eq 1834567 ] || { echo \"ntilde-mux upload incomplete: expected 1834567 bytes\" >&2; exit 1; }; "
            + "chmod 755 \"$t\"; \"$t\" --version --json > /dev/null; mv -f \"$t\" \"$d/ntilde-mux\"; trap - EXIT; "
            + "exec \"$d/ntilde-mux\" --version --json'",
            RemoteMuxInstallCommands.Upload(1834567));
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(64L * 1024 * 1024)]
    public void Upload_is_one_single_quoted_sh_script(long size)
    {
        AssertOneSingleQuotedShScript(RemoteMuxInstallCommands.Upload(size));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void Upload_needs_a_binary(long size)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RemoteMuxInstallCommands.Upload(size));
    }

    [Fact]
    public void Upload_installs_at_the_path_the_remote_command_runs()
    {
        Assert.Contains("\"$HOME/.local/share/ntilde/bin\"", RemoteMuxInstallCommands.Upload(10), StringComparison.Ordinal);
        Assert.Equal(".local/share/ntilde/bin/ntilde-mux", RemoteMuxCommand.DefaultRelativePath);
    }

    [Fact]
    public void OfflineOneLiner_for_linux_uses_sha256sum()
    {
        Assert.Equal(
            "mkdir -p ~/.local/share/ntilde/bin && cd ~/.local/share/ntilde/bin && "
            + "curl -fsSLo ntilde-mux.new https://github.com/benyblack/ntilde/releases/download/v0.11.0/ntilde-mux-linux-x64 && "
            + "curl -fsSL https://github.com/benyblack/ntilde/releases/download/v0.11.0/ntilde-mux-linux-x64.sha256 "
            + "| sed 's/ .*/  ntilde-mux.new/' | sha256sum -c - && chmod 755 ntilde-mux.new && mv -f ntilde-mux.new ntilde-mux",
            RemoteMuxInstallCommands.OfflineOneLiner("0.11.0", "linux-x64"));
    }

    [Fact]
    public void OfflineOneLiner_for_macos_uses_shasum()
    {
        Assert.Equal(
            "mkdir -p ~/.local/share/ntilde/bin && cd ~/.local/share/ntilde/bin && "
            + "curl -fsSLo ntilde-mux.new https://github.com/benyblack/ntilde/releases/download/v0.11.0/ntilde-mux-osx-arm64 && "
            + "curl -fsSL https://github.com/benyblack/ntilde/releases/download/v0.11.0/ntilde-mux-osx-arm64.sha256 "
            + "| sed 's/ .*/  ntilde-mux.new/' | shasum -a 256 -c - && chmod 755 ntilde-mux.new && mv -f ntilde-mux.new ntilde-mux",
            RemoteMuxInstallCommands.OfflineOneLiner("0.11.0", "osx-arm64"));
    }

    [Fact]
    public void OfflineOneLiner_for_linux_arm64_names_its_asset()
    {
        string line = RemoteMuxInstallCommands.OfflineOneLiner("0.12.1", "linux-arm64");

        Assert.Contains(GitHubReleaseMuxAssetSource.AssetUrl("0.12.1", "linux-arm64") + " ", line, StringComparison.Ordinal);
        Assert.Contains(GitHubReleaseMuxAssetSource.AssetUrl("0.12.1", "linux-arm64") + ".sha256 ", line, StringComparison.Ordinal);
        Assert.Contains("| sha256sum -c - ", line, StringComparison.Ordinal);
    }
}
