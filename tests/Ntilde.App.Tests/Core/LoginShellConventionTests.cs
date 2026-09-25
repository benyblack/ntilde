using Ntilde.Pty;
using Xunit;

namespace Ntilde.Tests.Core;

/// <summary>
/// Covers <see cref="ShellHelper.ApplyLoginShellConvention"/>. A macOS GUI app inherits launchd's
/// bare PATH, and only a login zsh reads /etc/zprofile and ~/.zprofile to fill it in - so a
/// non-login pane had no Homebrew on PATH and every brew/nvm line in ~/.zshrc failed.
/// </summary>
public sealed class LoginShellConventionTests
{
    [Theory]
    [InlineData("/bin/zsh")]
    [InlineData("/opt/homebrew/bin/zsh")]
    [InlineData("zsh")]
    [InlineData("\"/bin/zsh\"")]
    public void MacOSZshWithoutArguments_IsLaunchedAsALoginShell(string shell)
    {
        Assert.Equal("-l", ShellHelper.ApplyLoginShellConvention(shell, null, isMacOS: true));
        Assert.Equal("-l", ShellHelper.ApplyLoginShellConvention(shell, "  ", isMacOS: true));
    }

    [Fact]
    public void UserConfiguredArguments_AreLeftExactlyAsConfigured()
    {
        Assert.Equal("-f", ShellHelper.ApplyLoginShellConvention("/bin/zsh", "-f", isMacOS: true));
        Assert.Equal("-i", ShellHelper.ApplyLoginShellConvention("/bin/zsh", "-i", isMacOS: true));
    }

    /// <summary>
    /// A login bash ignores --rcfile, which is how bash command-assist integration is injected,
    /// so bash must not inherit the convention.
    /// </summary>
    [Theory]
    [InlineData("/bin/bash")]
    [InlineData("/opt/homebrew/bin/fish")]
    [InlineData("/bin/zsh-5.9")]
    public void MacOSOtherShells_GetNoLoginFlag(string shell)
    {
        Assert.Equal(string.Empty, ShellHelper.ApplyLoginShellConvention(shell, null, isMacOS: true));
    }

    [Fact]
    public void OffMacOS_ZshIsLeftNonLogin()
    {
        Assert.Equal(string.Empty, ShellHelper.ApplyLoginShellConvention("/usr/bin/zsh", null, isMacOS: false));
    }
}
