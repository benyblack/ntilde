using Ntilde.Mux.Daemon;
using Ntilde.Pty;

namespace Ntilde.Mux.Tests.Daemon;

/// <summary>The remote daemon's shell factory (spec §6.3): login-shell defaults, ~ expansion, no SSH.</summary>
public sealed class LocalShellSessionFactoryTests
{
    [Theory]
    [InlineData("", "", "/bin/zsh", "/bin/zsh", "-l")]
    [InlineData("  ", "", "/usr/bin/bash", "/usr/bin/bash", "-l")]
    [InlineData("", "", "/usr/bin/fish", "/usr/bin/fish", "-l")]
    [InlineData("", "", "/bin/ksh", "/bin/ksh", "-l")]
    [InlineData("", "", "/bin/sh", "/bin/sh", "-l")]
    [InlineData("", "", "/bin/dash", "/bin/dash", "-l")]
    // A default shell that is not a known login shell gets no flag it may not understand.
    [InlineData("", "", "/usr/bin/nu", "/usr/bin/nu", "")]
    [InlineData("", "", "pwsh.exe", "pwsh.exe", "")]
    // Arguments the caller chose are kept, even for a login shell.
    [InlineData("", "-c true", "/bin/bash", "/bin/bash", "-c true")]
    // An explicit command passes through unchanged, arguments and all.
    [InlineData("/bin/bash", "", "/bin/zsh", "/bin/bash", "")]
    [InlineData("vim", "notes.txt", "/bin/zsh", "vim", "notes.txt")]
    public void ResolveShell_defaults_an_empty_command_to_the_login_shell(
        string command, string arguments, string defaultShell, string expectedCommand, string expectedArguments)
    {
        (string resolvedCommand, string resolvedArguments) = LocalShellSessionFactory.ResolveShell(command, arguments, () => defaultShell);

        Assert.Equal((expectedCommand, expectedArguments), (resolvedCommand, resolvedArguments));
    }

    [Fact]
    public void ResolveShell_asks_for_the_default_shell_only_when_the_command_is_empty()
    {
        int asked = 0;
        string Default() { asked++; return "/bin/zsh"; }

        LocalShellSessionFactory.ResolveShell("/bin/bash", "", Default);
        Assert.Equal(0, asked);
        LocalShellSessionFactory.ResolveShell("", "", Default);
        Assert.Equal(1, asked);
    }

    private static readonly string Home = Path.Combine(Path.GetTempPath(), "home-of-test");

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("~", "")]
    [InlineData("~/projects", "projects")]
    [InlineData("~/a/b", "a/b")]
    public void ResolveWorkingDirectory_expands_against_home(string? requested, string underHome)
    {
        string expected = underHome.Length == 0 ? Home : Path.Combine(Home, underHome);

        Assert.Equal(expected, LocalShellSessionFactory.ResolveWorkingDirectory(requested, Home, _ => true));
    }

    [Fact]
    public void ResolveWorkingDirectory_keeps_an_existing_absolute_directory()
    {
        string dir = Path.Combine(Path.GetTempPath(), "somewhere");

        Assert.Equal(dir, LocalShellSessionFactory.ResolveWorkingDirectory(dir, Home, d => d == dir));
    }

    [Theory]
    [InlineData("/no/such/dir")]
    [InlineData("~/gone")]
    public void ResolveWorkingDirectory_falls_back_to_home_when_the_directory_does_not_exist(string requested)
    {
        Assert.Equal(Home, LocalShellSessionFactory.ResolveWorkingDirectory(requested, Home, d => d == Home));
    }

    [Fact]
    public void Ssh_requests_are_refused()
    {
        var request = new TerminalSessionRequest("", "", "", 80, 24, null, false,
            new SshSessionDescriptor(Guid.NewGuid(), 0, null, NativeSshEnabled: false));

        Assert.Throws<NotSupportedException>(() => LocalShellSessionFactory.Instance.Create(request));
    }
}
