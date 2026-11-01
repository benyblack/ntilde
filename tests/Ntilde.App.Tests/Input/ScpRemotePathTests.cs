using System.Diagnostics;
using Ntilde.Platform;
using Ntilde.Shell;
using Xunit;

namespace Ntilde.Tests.Input;

public sealed class ScpRemotePathTests
{
    [Theory]
    [InlineData("/tmp/plain", "/tmp/plain")]
    [InlineData("~/my file", "~/my\\ file")]
    [InlineData("~", "~")]
    [InlineData("-option", "./-option")]
    [InlineData("/tmp/ñ😀", "/tmp/ñ😀")]
    [InlineData(@"/tmp/file\ name", @"/tmp/file\\\ name")]
    public void RemoteShellEscaping_PreservesLiteralPathsAndHomeShorthand(string path, string expected)
    {
        Assert.Equal(expected, SftpService.EscapeScpRemotePath(path));
    }

    [Theory]
    [InlineData("/tmp/line\nfeed")]
    [InlineData("/tmp/line\rreturn")]
    [InlineData("/tmp/null\0byte")]
    public void RemoteShellEscaping_RejectsUnsupportedProtocolCharacters(string path)
    {
        Assert.Throws<ArgumentException>(() => SftpService.EscapeScpRemotePath(path));
    }

    [Theory]
    [InlineData("file with spaces.txt", false)]
    [InlineData("quotes'\".txt", false)]
    [InlineData(@"back\slash.txt", false)]
    [InlineData("literal;$()&`echo injected`*?[]!#~{}|<>.txt", false)]
    [InlineData("ñ😀 file.txt", false)]
    [InlineData("~/home file.txt", false)]
    [InlineData("-option", false)]
    [InlineData("folder with spaces", true)]
    public async Task LegacyScp_UploadAndDownload_UseLiteralRemoteNames(string name, bool folder)
    {
        // Run the real legacy SCP protocol through a local fake SSH. This exercises
        // both .NET argv decoding and the remote shell, including strict filename checks.
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Requires Unix OpenSSH scp and /bin/sh.");
            return;
        }

        Assert.SkipUnless(File.Exists("/usr/bin/scp"), "Requires OpenSSH scp.");
        string root = Path.Combine(Path.GetTempPath(), "ntilde-scp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string ssh = Path.Combine(root, "fake-ssh");
            await File.WriteAllTextAsync(ssh, "#!/bin/sh\nfor arg do command=\"$arg\"; done\ncd \"$FAKE_SSH_ROOT\" || exit 1\nexec /bin/sh -c \"$command\"\n", TestContext.Current.CancellationToken);
            File.SetUnixFileMode(ssh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            string local = Path.Combine(root, "local file.txt");
            string remote = Path.Combine(root, name.StartsWith("~/", StringComparison.Ordinal) ? name[2..] : name);
            if (folder) Directory.CreateDirectory(local);
            string localFile = folder ? Path.Combine(local, "child.txt") : local;
            string remoteFile = folder ? Path.Combine(remote, "child.txt") : remote;
            const string content = "literal path transfer\n";
            await File.WriteAllTextAsync(localFile, content, TestContext.Current.CancellationToken);
            var profile = new TerminalProfile { SshHost = "fakehost" };
            foreach (TransferDirection direction in new[] { TransferDirection.Upload, TransferDirection.Download })
            {
                var job = new TransferJob { Direction = direction, Kind = folder ? TransferKind.Folder : TransferKind.File, LocalPath = local, RemotePath = name.StartsWith("~/", StringComparison.Ordinal) || name[0] == '-' ? name : remote };
                string args = "-S " + SftpService.QuoteArg(ssh) + SftpService.BuildScpArguments(job, profile, null);
                using var process = new Process { StartInfo = SftpService.CreateScpStartInfo("/usr/bin/scp", args, profile) };
                process.StartInfo.Environment["HOME"] = root;
                process.StartInfo.Environment["FAKE_SSH_ROOT"] = root;
                Assert.True(process.Start());
                Task<string> error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
                try
                {
                    await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                    Assert.True(process.ExitCode == 0, await error);
                }
                finally
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }

                Assert.Equal(content, await File.ReadAllTextAsync(direction == TransferDirection.Upload ? remoteFile : localFile, TestContext.Current.CancellationToken));
                if (direction == TransferDirection.Upload)
                {
                    if (folder) Directory.Delete(local, recursive: true);
                    else File.Delete(local);
                }
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
