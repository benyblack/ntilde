namespace Ntilde.Tests.Shell;

/// <summary>
/// Which executable an OpenSSH exec channel names as its askpass helper (Phase 4 spec §8.2): the ntilde
/// app itself, which answers askpass when <c>NTILDE_SSH_ASKPASS=1</c>. When the process is something else
/// (a dev build under the test host, say), <c>Ntilde.Cli</c> next to it answers the same way; with
/// neither there is no helper, and ssh cannot prompt.
/// </summary>
public sealed class SshAskPassHelperLocationTests
{
    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "ntilde-askpass-test");

    [Theory]
    [InlineData("Ntilde.exe")]
    [InlineData("Ntilde")]
    [InlineData("ntilde")]
    public void The_app_itself_is_the_helper(string fileName)
    {
        string app = Path.Combine(Dir, fileName);

        Assert.Equal(app, SshAskPassCommand.LocateHelper(app, Dir, _ => false));
    }

    [Fact]
    public void Another_process_uses_Ntilde_Cli_next_to_it()
    {
        string cli = Path.Combine(Dir, OperatingSystem.IsWindows() ? "Ntilde.Cli.exe" : "Ntilde.Cli");

        Assert.Equal(cli, SshAskPassCommand.LocateHelper(Path.Combine(Dir, "testhost.exe"), Dir, path => path == cli));
        Assert.Equal(cli, SshAskPassCommand.LocateHelper(processPath: null, Dir, path => path == cli));
    }

    [Fact]
    public void Without_either_there_is_no_helper()
    {
        Assert.Null(SshAskPassCommand.LocateHelper(Path.Combine(Dir, "dotnet"), Dir, _ => false));
        Assert.Null(SshAskPassCommand.LocateHelper(processPath: null, Dir, _ => false));
    }
}
