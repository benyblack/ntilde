namespace Ntilde.Tests.Update;

/// <summary>
/// Final-fix item 2: Velopack's apply-on-startup must not bypass the in-app apply path, which asks
/// before closing multiplexed sessions and shuts the daemon down first.
/// </summary>
public sealed class StartupAutoApplyTests
{
    [Theory]
    [InlineData(new[] { "mux", "serve" }, false)]
    [InlineData(new[] { "mux", "serve" }, true)]
    [InlineData(new[] { "mux", "ls" }, false)]
    [InlineData(new[] { "MUX", "kill-server" }, true)]
    public void Mux_cli_modes_never_auto_apply(string[] args, bool daemonLive)
    {
        Assert.False(Program.ShouldAutoApplyUpdateOnStartup(args, () => daemonLive));
    }

    [Fact]
    public void Gui_start_with_a_live_daemon_does_not_auto_apply()
    {
        Assert.False(Program.ShouldAutoApplyUpdateOnStartup([], () => true));
    }

    [Fact]
    public void Gui_start_without_a_daemon_keeps_auto_apply()
    {
        Assert.True(Program.ShouldAutoApplyUpdateOnStartup([], () => false));
    }

    [Fact]
    public void A_mux_cli_mode_does_not_even_probe_for_a_daemon()
    {
        int probes = 0;
        Program.ShouldAutoApplyUpdateOnStartup(["mux", "serve"], () => { probes++; return false; });
        Assert.Equal(0, probes);
    }
}
