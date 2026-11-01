using System.Diagnostics;
using Ntilde.Shell;

namespace Ntilde.Tests.Input;

public sealed class ScpLocalPathTests
{
    [Theory]
    [InlineData(TransferDirection.Upload)]
    [InlineData(TransferDirection.Download)]
    public void Local_operand_uses_the_current_platform_path_rules(TransferDirection direction)
    {
        const string local = @"C:\local dir\file.txt";
        var profile = new TerminalProfile { SshHost = "target" };
        var job = new TransferJob { Direction = direction, LocalPath = local, RemotePath = "/remote/file" };
        string expected = SftpService.QuoteArg(OperatingSystem.IsWindows() ? "C:/local dir/file.txt" : local);
        string args = SftpService.BuildScpArguments(job, profile, null);
        Assert.EndsWith(direction == TransferDirection.Upload
            ? " " + expected + " target:/remote/file"
            : " target:/remote/file " + expected, args);
    }

    [Theory]
    [InlineData(TransferDirection.Upload, false)]
    [InlineData(TransferDirection.Download, false)]
    [InlineData(TransferDirection.Upload, true)]
    [InlineData(TransferDirection.Download, true)]
    public async Task Legacy_scp_uses_the_selected_unix_path_and_preserves_the_slash_named_decoy(TransferDirection direction, bool folder)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Literal backslash filenames require Unix.");
            return;
        }
        Assert.SkipUnless(File.Exists("/usr/bin/scp"), "Requires OpenSSH scp.");
        string root = Path.Combine(Path.GetTempPath(), "ntilde-scp-local-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string ssh = Path.Combine(root, "fake-ssh");
            await File.WriteAllTextAsync(ssh, "#!/bin/sh\nfor arg do command=\"$arg\"; done\nexec /bin/sh -c \"$command\"\n", TestContext.Current.CancellationToken);
            File.SetUnixFileMode(ssh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            string selected = Path.Combine(root, @"local\name with space");
            string decoy = Path.Combine(root, "local", "name with space");
            string remote = Path.Combine(root, "remote folder");
            string selectedFile = folder ? Path.Combine(selected, "child.txt") : selected;
            string decoyFile = folder ? Path.Combine(decoy, "child.txt") : decoy;
            string remoteFile = folder ? Path.Combine(remote, "child.txt") : remote;
            const string content = "selected content\n";
            const string untouched = "different slash-named path\n";
            Directory.CreateDirectory(Path.GetDirectoryName(decoyFile)!);
            await File.WriteAllTextAsync(decoyFile, untouched, TestContext.Current.CancellationToken);
            string source = direction == TransferDirection.Upload ? selectedFile : remoteFile;
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            await File.WriteAllTextAsync(source, content, TestContext.Current.CancellationToken);
            var profile = new TerminalProfile { SshHost = "fakehost" };
            var job = new TransferJob
            {
                Direction = direction,
                Kind = folder ? TransferKind.Folder : TransferKind.File,
                LocalPath = selected,
                RemotePath = remote
            };
            string args = "-S " + SftpService.QuoteArg(ssh) + SftpService.BuildScpArguments(job, profile, null);
            using var process = new Process { StartInfo = SftpService.CreateScpStartInfo("/usr/bin/scp", args, profile) };
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
            Assert.Equal(untouched, await File.ReadAllTextAsync(decoyFile, TestContext.Current.CancellationToken));
            string destination = direction == TransferDirection.Upload ? remoteFile : selectedFile;
            Assert.Equal(content, await File.ReadAllTextAsync(destination, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
