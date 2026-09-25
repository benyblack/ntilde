using Ntilde.Shell;
using Ntilde.CommandAssist.ShellIntegration.Contracts;
using Ntilde.CommandAssist.ShellIntegration.Zsh;
using Ntilde.Platform;
using Ntilde.VT;

namespace Ntilde.Tests.CommandAssist.ShellIntegration;

public sealed class ZshShellIntegrationProviderTests
{
    [Theory]
    [InlineData("zsh", "/bin/zsh")]
    [InlineData("zsh", "zsh")]
    [InlineData("ZSH", "/usr/local/bin/zsh")]
    public void CanIntegrate_ForZshShells_ReturnsTrue(string shellKind, string command)
    {
        var provider = new ZshShellIntegrationProvider(() => BootstrapTestDirectory.ForCaller());

        Assert.True(provider.CanIntegrate(shellKind, command));
    }

    [Theory]
    [InlineData("bash", "/bin/bash")]
    [InlineData("pwsh", "pwsh.exe")]
    [InlineData("fish", "/usr/local/bin/fish")]
    public void CanIntegrate_ForOtherShells_ReturnsFalse(string shellKind, string command)
    {
        var provider = new ZshShellIntegrationProvider(() => BootstrapTestDirectory.ForCaller());

        Assert.False(provider.CanIntegrate(shellKind, command));
    }

    [Fact]
    public void CreateLaunchPlan_ForVanillaZsh_InjectsBootstrapViaZdotdirEnvOverride()
    {
        var provider = new ZshShellIntegrationProvider(() => BootstrapTestDirectory.ForCaller());

        ShellIntegrationLaunchPlan plan = provider.CreateLaunchPlan("/bin/zsh", shellArguments: null, workingDirectory: null);

        Assert.True(plan.IsIntegrated);
        Assert.NotNull(plan.BootstrapScriptPath);
        Assert.NotNull(plan.EnvironmentOverrides);
        Assert.True(plan.EnvironmentOverrides!.ContainsKey("ZDOTDIR"));
        // ZDOTDIR points to the directory containing our .zshrc, which is
        // also where BootstrapScriptPath was written.
        Assert.Equal(
            Path.GetDirectoryName(plan.BootstrapScriptPath!),
            plan.EnvironmentOverrides["ZDOTDIR"]);
    }

    [Fact]
    public void CreateLaunchPlan_WhenTheUserHasTheirOwnZdotdir_PassesItToTheShims()
    {
        var provider = new ZshShellIntegrationProvider(
            () => BootstrapTestDirectory.ForCaller(),
            name => name == "ZDOTDIR" ? "/Users/me/.config/zsh" : null);

        ShellIntegrationLaunchPlan plan = provider.CreateLaunchPlan("/bin/zsh", shellArguments: null, workingDirectory: null);

        Assert.Equal("/Users/me/.config/zsh", plan.EnvironmentOverrides![ZshBootstrapBuilder.UserZdotdirVariable]);
    }

    [Fact]
    public void CreateLaunchPlan_WithoutAUserZdotdir_LeavesItUnsetForTheShims()
    {
        // Absent means "unset" to the shims, i.e. $HOME -- an empty value would not.
        var provider = new ZshShellIntegrationProvider(() => BootstrapTestDirectory.ForCaller(), _ => null);

        ShellIntegrationLaunchPlan plan = provider.CreateLaunchPlan("/bin/zsh", shellArguments: null, workingDirectory: null);

        Assert.False(plan.EnvironmentOverrides!.ContainsKey(ZshBootstrapBuilder.UserZdotdirVariable));
    }

    [Fact]
    public void CreateLaunchPlan_WhenInheritedZdotdirIsOurOwn_DoesNotPassItAsTheUsers()
    {
        // An Ntilde started from an Ntilde pane mid-startup inherits the shim directory; treating
        // it as the user's would make the shims source themselves.
        string root = BootstrapTestDirectory.ForCaller();
        string ours = Path.GetDirectoryName(ZshBootstrapBuilder.WriteScript(root))!;
        var provider = new ZshShellIntegrationProvider(() => root, name => name == "ZDOTDIR" ? ours + "/" : null);

        ShellIntegrationLaunchPlan plan = provider.CreateLaunchPlan("/bin/zsh", shellArguments: null, workingDirectory: null);

        Assert.False(plan.EnvironmentOverrides!.ContainsKey(ZshBootstrapBuilder.UserZdotdirVariable));
    }

    [Fact]
    public void CreateLaunchPlan_WithExistingUserArguments_PreservesThem()
    {
        var provider = new ZshShellIntegrationProvider(() => BootstrapTestDirectory.ForCaller());

        ShellIntegrationLaunchPlan plan = provider.CreateLaunchPlan("/bin/zsh", "--login", null);

        Assert.True(plan.IsIntegrated);
        Assert.Contains("--login", plan.ShellArguments!);
    }

    [Theory]
    [InlineData("-c \"echo hi\"")]
    [InlineData("--no-rcs")]
    public void CreateLaunchPlan_WhenUserForcesIncompatibleStartupMode_DisablesIntegration(string userArgs)
    {
        var provider = new ZshShellIntegrationProvider(() => BootstrapTestDirectory.ForCaller());

        ShellIntegrationLaunchPlan plan = provider.CreateLaunchPlan("/bin/zsh", userArgs, null);

        Assert.False(plan.IsIntegrated);
        Assert.Null(plan.BootstrapScriptPath);
        Assert.Equal(userArgs, plan.ShellArguments);
    }
}
