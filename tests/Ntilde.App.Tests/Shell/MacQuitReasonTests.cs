using Ntilde.Shell;

namespace Ntilde.Tests.Shell;

/// <summary>Release hardening item 2: a logout, restart or shutdown never waits on the first-close question; Cmd+Q does.</summary>
public sealed class MacQuitReasonTests
{
    [Theory]
    [InlineData(MacQuitReason.ShutDown, true)]
    [InlineData(MacQuitReason.Restart, true)]
    [InlineData(MacQuitReason.ReallyLogOut, true)]
    [InlineData(MacQuitReason.LogOut, true)]
    [InlineData(0u, false)]
    [InlineData(0x71756974u, false)] // 'quit'
    public void Only_the_session_ending_counts(uint reason, bool sessionEnding)
    {
        Assert.Equal(sessionEnding, MacQuitReason.IsSessionEndReason(reason));
    }

    [Fact]
    public void Off_macOS_nothing_is_read_and_the_answer_is_no()
    {
        Assert.SkipWhen(OperatingSystem.IsMacOS(), "On macOS this reads the live Apple event: there is none in a test run, so it answers no as well.");
        Assert.False(MacQuitReason.IsSessionEnding());
    }

    [Fact]
    public void On_macOS_a_test_run_has_no_quit_event_and_reading_it_does_not_crash()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS only: the Objective-C runtime.");
        Assert.False(MacQuitReason.IsSessionEnding());
    }
}
