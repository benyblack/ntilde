using Ntilde.Platform.Ssh.Models;
using Ntilde.Shell.Mux.Remote;

namespace Ntilde.Tests.Shell.Mux.Remote;

/// <summary>
/// The command that starts the remote proxy (Phase 4 spec §7.1, plan Review Focus 3). sshd hands it to
/// the user's login shell, which may be fish, tcsh or nushell, so it is either a bare absolute path made
/// of characters no shell treats specially, or <c>sh -c '&lt;script&gt;'</c> whose script holds no single
/// quote: every shell passes a single-quoted word to sh untouched.
/// </summary>
public sealed class RemoteMuxCommandTests
{
    private const string ProxyFallback =
        "sh -c 'case \"${XDG_DATA_HOME-}\" in /*) d=\"$XDG_DATA_HOME/ntilde/bin\";; *) d=\"$HOME/.local/share/ntilde/bin\";; esac; exec \"$d/ntilde-mux\" proxy --stdio'";

    [Theory]
    [InlineData("/home/nova/.local/share/ntilde/bin/ntilde-mux", "/home/nova/.local/share/ntilde/bin/ntilde-mux proxy --stdio")]
    [InlineData("/opt/ntilde-mux_1.2+b/bin/ntilde-mux", "/opt/ntilde-mux_1.2+b/bin/ntilde-mux proxy --stdio")]
    [InlineData("", ProxyFallback)]
    [InlineData("/home/a b/x", "sh -c 'exec \"/home/a b/x\" proxy --stdio'")]
    [InlineData("/home/José/.local/share/ntilde/bin/ntilde-mux", "sh -c 'exec \"/home/José/.local/share/ntilde/bin/ntilde-mux\" proxy --stdio'")]
    [InlineData("/home/a$b/x", "sh -c 'exec \"/home/a\\$b/x\" proxy --stdio'")]
    [InlineData("/home/a`b/x", "sh -c 'exec \"/home/a\\`b/x\" proxy --stdio'")]
    [InlineData("/home/a\"b/x", "sh -c 'exec \"/home/a\\\"b/x\" proxy --stdio'")]
    [InlineData("/home/$USER/x", "sh -c 'exec \"/home/\\$USER/x\" proxy --stdio'")]
    [InlineData("/x;rm -rf ~", "sh -c 'exec \"/x;rm -rf ~\" proxy --stdio'")]
    [InlineData("/home/a'b/x", ProxyFallback)]
    [InlineData("/home/a\\b/x", ProxyFallback)]
    [InlineData("/home/a!b/x", ProxyFallback)]
    [InlineData("/x/‮evil", ProxyFallback)]
    [InlineData("/x/a\u0001b", ProxyFallback)]
    [InlineData("relative/x", ProxyFallback)]
    [InlineData("/home/nova//x", ProxyFallback)]
    [InlineData("/home/nova/../root/x", ProxyFallback)]
    [InlineData("/home/it's/x", ProxyFallback)]
    [InlineData("/home/nova/x\n", ProxyFallback)]
    [InlineData("/", ProxyFallback)]
    public void Remote_command_is_shell_agnostic(string recordedPath, string expected)
    {
        string command = RemoteMuxCommand.Proxy(new SshMuxOptions { RemoteDaemonPath = recordedPath });

        Assert.Equal(expected, command);
        if (command.StartsWith("sh -c '", StringComparison.Ordinal))
        {
            // One single-quoted word: no quote inside it, so no shell can end it early.
            Assert.Equal(2, command.Count(c => c == '\''));
            Assert.EndsWith("'", command, StringComparison.Ordinal);
        }
        else
        {
            Assert.True(RemoteMuxCommand.IsQuotableAbsolutePath(recordedPath));
            Assert.DoesNotContain('\'', command);
            Assert.DoesNotContain('"', command);
        }
    }

    [Fact]
    public void VersionJson_follows_the_same_rule()
    {
        Assert.Equal(
            "/home/nova/.local/share/ntilde/bin/ntilde-mux --version --json",
            RemoteMuxCommand.VersionJson(new SshMuxOptions { RemoteDaemonPath = "/home/nova/.local/share/ntilde/bin/ntilde-mux" }));
        Assert.Equal(
            "sh -c 'exec \"/x y\" --version --json'",
            RemoteMuxCommand.VersionJson(new SshMuxOptions { RemoteDaemonPath = "/x y" }));
        Assert.Equal(
            ProxyFallback.Replace("proxy --stdio", "--version --json", StringComparison.Ordinal),
            RemoteMuxCommand.VersionJson(new SshMuxOptions { RemoteDaemonPath = "/x'y" }));
    }

    [Fact]
    public void The_fallback_runs_the_default_install_dir()
    {
        Assert.Contains(RemoteInstallDir.Assign + "exec \"$d/ntilde-mux\" ", RemoteMuxCommand.Proxy(new SshMuxOptions()), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/a", true)]
    [InlineData("/usr/local/bin/ntilde-mux", true)]
    [InlineData("/home/n.o-v_a/+x", true)]
    [InlineData("/a b", true)]
    [InlineData("/a*", true)]
    [InlineData("/a$b", true)]
    [InlineData("/hé", true)]
    [InlineData("", false)]
    [InlineData("/", false)]
    [InlineData("a/b", false)]
    [InlineData("~/x", false)]
    [InlineData("/a//b", false)]
    [InlineData("/a/../b", false)]
    [InlineData("/a\tb", false)]
    [InlineData("/a\\b", false)]
    [InlineData("/a'b", false)]
    [InlineData("/a!b", false)]
    [InlineData("/a​b", false)]
    [InlineData("/a‮b", false)]
    public void IsQuotableAbsolutePath_allows_any_absolute_path_without_the_refused_characters(string path, bool quotable)
    {
        Assert.Equal(quotable, RemoteMuxCommand.IsQuotableAbsolutePath(path));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("/home/nova/x", false)]
    [InlineData("/home/a b/x", false)]
    [InlineData("/home/a'b/x", true)]
    [InlineData("relative/x", true)]
    [InlineData("/x/‮evil", true)]
    public void RefusedRecordedPath_is_a_recorded_path_that_is_replaced(string recorded, bool refused)
    {
        Assert.Equal(refused, RemoteMuxCommand.RefusedRecordedPath(recorded));
    }

    [Theory]
    [InlineData("a b")]
    [InlineData("a$b c")]
    [InlineData("a`b`")]
    [InlineData("a\"b")]
    [InlineData("Jósé $HOME `id`")]
    public void A_quoted_recorded_path_runs_that_exact_file_under_sh(string dirName)
    {
        PosixShell.Require();
        string root = Path.Combine(Path.GetTempPath(), "ntilde-quote-" + Guid.NewGuid().ToString("N"));
        try
        {
            string script = Path.Combine(root, dirName, "ntilde-mux");
            if (OperatingSystem.IsWindows() && dirName.Contains('"'))
            {
                Assert.Skip("Windows file names cannot hold a double quote.");
            }

            PosixShell.WriteEchoScript(script);
            string recorded = PosixShell.ToShellPath(script);
            string command = RemoteMuxCommand.Proxy(new SshMuxOptions { RemoteDaemonPath = recorded });

            (int exit, string stdout, string stderr) = PosixShell.Run(command, new Dictionary<string, string?> { ["HOME"] = "/nonexistent" });

            Assert.True(exit == 0, $"{command}: {stderr}");
            Assert.Equal($"[{recorded}] proxy --stdio", stdout.Trim());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_fallback_runs_the_install_under_XDG_DATA_HOME_or_HOME(bool xdgSet)
    {
        PosixShell.Require();
        string root = Path.Combine(Path.GetTempPath(), "ntilde-fallback-" + Guid.NewGuid().ToString("N"));
        try
        {
            string xdg = Path.Combine(root, "xdg");
            string home = Path.Combine(root, "home");
            PosixShell.WriteEchoScript(Path.Combine(xdgSet ? xdg : home + "/.local/share", "ntilde", "bin", "ntilde-mux"));
            var env = new Dictionary<string, string?>
            {
                ["HOME"] = PosixShell.ToShellPath(home),
                ["XDG_DATA_HOME"] = xdgSet ? PosixShell.ToShellPath(xdg) : null,
            };

            (int exit, string stdout, string stderr) = PosixShell.Run(RemoteMuxCommand.Proxy(new SshMuxOptions()), env);

            Assert.True(exit == 0, stderr);
            string installed = PosixShell.ToShellPath(xdgSet ? xdg : home + "/.local/share") + "/ntilde/bin/ntilde-mux";
            Assert.Equal($"[{installed}] proxy --stdio", stdout.Trim());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
