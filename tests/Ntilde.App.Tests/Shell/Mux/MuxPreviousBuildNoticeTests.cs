using Ntilde.Shell.Mux;

namespace Ntilde.Tests.Shell.Mux;

/// <summary>
/// Phase 5 Task 23: what decides that a daemon is another build's, and the words the notice, its action and its question
/// use. The window's side is <c>MainWindowMuxUpdateTests</c> (local) and <c>MainWindowMuxRemoteTests</c> (remote).
/// </summary>
public sealed class MuxPreviousBuildNoticeTests
{
    /// <summary>
    /// Both versions known and different, build metadata aside. Unknown on either side - none, empty, or 0.0.0, which
    /// ntilde-mux reports when it cannot tell (the Task 20 ruling) - is never "another build": a dev build or a daemon
    /// that reports nothing is not told it is out of date.
    /// </summary>
    [Theory]
    [InlineData("0.0.1", "0.12.0", true)]
    [InlineData("0.13.0", "0.12.0", true)]
    [InlineData("0.12.0", "0.12.0", false)]
    [InlineData("0.12.0+3f2c1ab", "0.12.0", false)]
    [InlineData(" 0.12.0 ", "0.12.0+3f2c1ab", false)]
    [InlineData(null, "0.12.0", false)]
    [InlineData("", "0.12.0", false)]
    [InlineData("0.0.0", "0.12.0", false)]
    [InlineData("0.0.0+abc", "0.12.0", false)]
    [InlineData("0.11.0", null, false)]
    [InlineData("0.11.0", "", false)]
    [InlineData("0.11.0", "0.0.0", false)]
    public void A_daemon_is_another_builds_only_when_both_versions_are_known_and_differ(string? daemon, string? thisBuild, bool expected) =>
        Assert.Equal(expected, MuxPreviousBuildNotice.IsFromAnotherBuild(daemon, thisBuild));

    [Fact]
    public void The_local_notice_names_the_old_version_and_the_daemons_shells()
    {
        Assert.Equal(
            "The multiplexer is from the previous build (0.0.1); restart it when convenient — this closes its 3 shells.",
            MuxPreviousBuildNotice.LocalMessage("0.0.1", 3));
        Assert.Equal(
            "The multiplexer is from the previous build (0.11.0); restart it when convenient — this closes its 1 shell.",
            MuxPreviousBuildNotice.LocalMessage("0.11.0+3f2c1ab", 1));
        Assert.Equal("Multiplexer", MuxPreviousBuildNotice.Title);
        Assert.Equal("Restart multiplexer now", MuxPreviousBuildNotice.LocalActionLabel);
    }

    [Fact]
    public void The_remote_notice_and_its_action_name_the_host()
    {
        Assert.Equal(
            "ntilde-mux on nova@box is from a previous version (0.0.1); restart it when convenient — this closes its 2 shells.",
            MuxPreviousBuildNotice.RemoteMessage("nova@box", "0.0.1", 2));
        Assert.Equal("Restart ntilde-mux on nova@box", MuxPreviousBuildNotice.RemoteActionLabel("nova@box"));
    }

    /// <summary>
    /// Ruling P2: the version came over a socket (from a remote host, or a local daemon that any process of the user can
    /// start): control and format characters never reach the toast, and the length is capped.
    /// </summary>
    [Fact]
    public void A_reported_version_is_quoted_before_it_reaches_the_toast()
    {
        string hostile = "0.0.1\u001b[2J‮\r\nfake line" + new string('9', 500);

        foreach (string message in new[] { MuxPreviousBuildNotice.LocalMessage(hostile, 1), MuxPreviousBuildNotice.RemoteMessage("nova@‮box", hostile, 1) })
        {
            Assert.DoesNotContain(message, c => char.IsControl(c) || c == '‮');
            Assert.Contains("0.0.1[2J", message, StringComparison.Ordinal);
            Assert.True(message.Length < 400, $"not capped: {message.Length} characters");
        }
    }

    [Theory]
    [InlineData(null, 3, "Restart Multiplexer", "Restart the multiplexer?", "3 shells running in the multiplexer will be closed.")]
    [InlineData(null, 1, "Restart Multiplexer", "Restart the multiplexer?", "1 shell running in the multiplexer will be closed.")]
    [InlineData(null, -1, "Restart Multiplexer", "Restart the multiplexer?", "All shells running in the multiplexer will be closed.")]
    [InlineData("nova@box", 2, "Restart ntilde-mux", "Restart ntilde-mux on nova@box?", "2 shells running in ntilde-mux on nova@box will be closed.")]
    public void The_question_names_what_closes(string? host, int shells, string title, string heading, string message)
    {
        Assert.Equal(title, MuxPreviousBuildNotice.ConfirmTitle(host));
        Assert.Equal(heading, MuxPreviousBuildNotice.ConfirmHeading(host));
        Assert.Equal(message, MuxPreviousBuildNotice.ConfirmMessage(host, shells));
        Assert.Equal("Restart", MuxPreviousBuildNotice.ConfirmButton);
    }

    /// <summary>
    /// Once per launch for each endpoint: the first claim wins, later ones (a reconnect, another window) do not; a release
    /// - after a restart, or a count that could not be had - lets the next connection offer it again.
    /// </summary>
    [Fact]
    public void An_endpoint_is_offered_once_until_released()
    {
        var offered = new MuxPreviousBuildNotice.Offered();
        MuxEndpointId remote = MuxEndpointId.ForSsh(Guid.NewGuid());

        Assert.True(offered.TryClaim(MuxEndpointId.Local));
        Assert.False(offered.TryClaim(MuxEndpointId.Local));
        Assert.True(offered.TryClaim(remote)); // per endpoint
        Assert.False(offered.TryClaim(MuxEndpointId.ForSsh(remote.SshProfileId!.Value)));

        offered.Release(MuxEndpointId.Local);

        Assert.True(offered.TryClaim(MuxEndpointId.Local));
        Assert.False(offered.TryClaim(remote));
    }
}
