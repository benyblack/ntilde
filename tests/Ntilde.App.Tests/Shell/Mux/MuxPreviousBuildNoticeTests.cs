using Ntilde.Shell.Mux;

namespace Ntilde.Tests.Shell.Mux;

/// <summary>
/// Phase 5 Task 23: what decides that a daemon is another build's, and the words the notice, its action, its question and
/// its outcomes use. The window's side is <c>MainWindowMuxUpdateTests</c> (local) and <c>MainWindowMuxRemoteTests</c> (remote).
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

    /// <summary>
    /// Final review I1: a remote restart starts the binary installed on the host, which an app update never replaces. So it
    /// is offered only when the version recorded as installed is known and newer than the running one (the install flow
    /// replaced the binary under it), whatever the app's version. Residual N1, ruling (a): a running version newer than the
    /// record, or one that has no order against it, is a stale record (another profile, another computer, a hand install),
    /// never a restart. Otherwise a host whose ntilde-mux is older than the app - installed and running alike, running
    /// ahead of a stale record, or running with no installed version known - is offered the update; anything else, nothing.
    /// </summary>
    [Theory]
    [InlineData("0.12.0", "0.12.1", "0.12.1", "restart")]
    [InlineData("0.12.0", "0.12.1", "0.12.0", "restart")]
    [InlineData("0.12.0-rc.1", "0.12.0", "0.12.0", "restart")]
    [InlineData("0.12.1", "0.12.0", "0.12.1", "none")] // running ahead of the record: the record is stale
    [InlineData("0.12.1", "0.12.0", "0.12.2", "update")] // ...and older than the app
    [InlineData("custom", "0.12.1", "0.12.1", "none")] // no order against the record
    [InlineData("0.12.0", "0.12.0", "0.12.1", "update")]
    [InlineData("0.12.0+3f2c1ab", "0.12.0", "0.12.1", "update")]
    [InlineData("0.12.0", "0.12.0", "0.12.0", "none")]
    [InlineData("0.12.1", "0.12.1", "0.12.0", "none")] // newer than the app
    [InlineData("custom", "custom", "0.12.0", "none")] // no order
    [InlineData("0.12.0", "0.12.0", "0.0.0", "none")] // a dev build of the app
    [InlineData("0.12.0", "", "0.12.1", "update")]
    [InlineData("0.12.0", null, "0.12.1", "update")]
    [InlineData("0.12.0", "0.0.0", "0.12.1", "update")]
    [InlineData("0.12.1", "", "0.12.0", "none")]
    [InlineData("0.12.0", "", "0.12.0", "none")]
    [InlineData("custom", "", "0.12.0", "none")]
    [InlineData("0.12.0", "", null, "none")]
    [InlineData("0.0.0", "0.12.1", "0.12.1", "none")] // the daemon reports nothing
    [InlineData("", "0.12.1", "0.12.1", "none")]
    [InlineData(null, "", "0.12.1", "none")]
    public void A_remote_daemon_is_offered_a_restart_only_against_the_installed_version(string? running, string? installed, string? app, string expected)
    {
        MuxPreviousBuildNotice.NoticeOffer offer = MuxPreviousBuildNotice.DecideRemote(running, installed, app);
        Assert.Equal(expected, offer.ToString().ToLowerInvariant());
    }

    /// <summary>Final review I1: what the last look reports when a remote daemon does not differ from the installed version.</summary>
    [Fact]
    public void A_remote_daemon_not_to_restart_says_why()
    {
        Assert.Equal(MuxPreviousBuildNotice.RestartOutcome.AlreadyThisBuild, MuxPreviousBuildNotice.NothingToRestartRemote("0.0.1", "0.0.1"));
        Assert.Equal(MuxPreviousBuildNotice.RestartOutcome.AlreadyThisBuild, MuxPreviousBuildNotice.NothingToRestartRemote("0.12.1", "0.12.0")); // ahead of a stale record
        Assert.Equal(MuxPreviousBuildNotice.RestartOutcome.InstalledVersionUnknown, MuxPreviousBuildNotice.NothingToRestartRemote("0.0.1", ""));
        Assert.Equal(MuxPreviousBuildNotice.RestartOutcome.InstalledVersionUnknown, MuxPreviousBuildNotice.NothingToRestartRemote("0.0.1", "0.0.0"));
        Assert.Equal(MuxPreviousBuildNotice.RestartOutcome.NoVersion, MuxPreviousBuildNotice.NothingToRestartRemote(null, "0.0.1"));
    }

    /// <summary>Final review I1: the update notice names the host and quotes both versions, and says what an update does to the shells.</summary>
    [Fact]
    public void The_remote_update_notice_names_the_host_and_both_versions()
    {
        Assert.Equal(
            "ntilde-mux on nova@box is older (0.12.0) than this app (0.12.1); update it when convenient. Updating replaces the binary; your shells keep running until you restart it.",
            MuxPreviousBuildNotice.RemoteUpdateMessage("nova@box", "0.12.0+3f2c1ab", "0.12.1"));
    }

    /// <summary>
    /// Ruling R-a: one comparison, SemVer 2.0.0 precedence. The numeric release parts compare as numbers (1.10 is newer than
    /// 1.9), build metadata is ignored, a prerelease comes before its release, and prerelease identifiers compare as SemVer
    /// says (numbers numerically and below words, words in ASCII order, a longer set after its prefix). A version that does
    /// not parse has no order.
    /// </summary>
    [Theory]
    [InlineData("0.11.0", "0.12.0", -1)]
    [InlineData("0.13.0", "0.12.0", 1)]
    [InlineData("1.10.0", "1.9.0", 1)]
    [InlineData("0.12.0+3f2c1ab", "0.11.0", 1)]
    [InlineData("0.12.0", "0.12.0+3f2c1ab", 0)]
    [InlineData("0.12", "0.12.0", 0)]
    [InlineData("0.12.0-beta.1", "0.12.0", -1)]
    [InlineData("0.12.0", "0.12.0-rc.1", 1)]
    [InlineData("0.12.0-beta.2", "0.12.0-beta.10", -1)]
    [InlineData("0.12.0-alpha", "0.12.0-beta", -1)]
    [InlineData("0.12.0-1", "0.12.0-alpha", -1)]
    [InlineData("0.12.0-beta", "0.12.0-beta.1", -1)]
    [InlineData("dev-build", "0.12.0", null)]
    [InlineData("0.12.x", "0.12.0", null)]
    [InlineData("0.12.0", "", null)]
    [InlineData("0.12.0-", "0.12.0", null)]
    [InlineData("0.12.0-beta..1", "0.12.0", null)]
    [InlineData("99999999999.0.0", "0.12.0", null)]
    public void Versions_compare_by_semver_precedence(string? a, string? b, int? expected)
    {
        Assert.Equal(expected, MuxPreviousBuildNotice.CompareVersions(a, b));
        Assert.Equal(-expected, MuxPreviousBuildNotice.CompareVersions(b, a));
    }

    /// <summary>
    /// Ruling R-a: "previous" only for an older daemon; a newer one is called newer, and one with no order different. Local
    /// only: a remote restart is offered only for a daemon behind the installed version (residual N1), so the remote notice
    /// always says "a previous version".
    /// </summary>
    [Theory]
    [InlineData("0.11.0", "the previous build")]
    [InlineData("0.12.0-rc.1", "the previous build")]
    [InlineData("0.13.0", "a newer build")]
    [InlineData("custom", "a different build")]
    public void The_local_notice_says_how_the_daemons_version_relates_to_this_one(string daemon, string local) =>
        Assert.Equal(
            $"The multiplexer is from {local} ({daemon}); restart it when convenient \u2014 this closes its 3 shells.",
            MuxPreviousBuildNotice.LocalMessage(daemon, "0.12.0", 3));

    [Fact]
    public void The_local_notice_names_the_old_version_and_the_daemons_shells()
    {
        Assert.Equal(
            "The multiplexer is from the previous build (0.0.1); restart it when convenient \u2014 this closes its 3 shells.",
            MuxPreviousBuildNotice.LocalMessage("0.0.1", "0.12.0", 3));
        Assert.Equal(
            "The multiplexer is from the previous build (0.11.0); restart it when convenient \u2014 this closes its 1 shell.",
            MuxPreviousBuildNotice.LocalMessage("0.11.0+3f2c1ab", "0.12.0", 1));
        Assert.Equal("Multiplexer", MuxPreviousBuildNotice.Title);
        Assert.Equal("Restart multiplexer now", MuxPreviousBuildNotice.LocalActionLabel);
    }

    [Fact]
    public void The_remote_notice_and_its_action_name_the_host()
    {
        Assert.Equal(
            "ntilde-mux on nova@box is from a previous version (0.0.1); restart it when convenient \u2014 this closes its 2 shells.",
            MuxPreviousBuildNotice.RemoteMessage("nova@box", "0.0.1", 2));
        Assert.Equal(
            "ntilde-mux on nova@box is from a previous version (0.12.0-rc.1); restart it when convenient — this closes its 1 shell.",
            MuxPreviousBuildNotice.RemoteMessage("nova@box", "0.12.0-rc.1+3f2c1ab", 1));
        Assert.Equal("Restart ntilde-mux on nova@box", MuxPreviousBuildNotice.RemoteActionLabel("nova@box"));
    }

    /// <summary>Ruling R-b: a daemon that runs no shells closes none - the clause goes, from the notice and from the question.</summary>
    [Fact]
    public void A_daemon_with_no_shells_is_not_said_to_close_any()
    {
        Assert.Equal("The multiplexer is from the previous build (0.0.1); restart it when convenient.", MuxPreviousBuildNotice.LocalMessage("0.0.1", "0.12.0", 0));
        Assert.Equal("ntilde-mux on nova@box is from a previous version (0.0.1); restart it when convenient.", MuxPreviousBuildNotice.RemoteMessage("nova@box", "0.0.1", 0));
        Assert.DoesNotContain("0", MuxPreviousBuildNotice.ConfirmMessage(null, 0), StringComparison.Ordinal);
        Assert.DoesNotContain("0", MuxPreviousBuildNotice.ConfirmMessage("nova@box", 0), StringComparison.Ordinal);
        Assert.DoesNotContain("closed", MuxPreviousBuildNotice.ConfirmMessage(null, 0), StringComparison.Ordinal);
    }

    /// <summary>
    /// Ruling P2: the version came over a socket (from a remote host, or a local daemon that any process of the user can
    /// start): control and format characters never reach the toast, and the length is capped.
    /// </summary>
    [Fact]
    public void A_reported_version_is_quoted_before_it_reaches_the_toast()
    {
        string bidi = ((char)0x202E).ToString();
        string hostile = "0.0.1" + (char)0x1B + "[2J" + bidi + "\r\nfake line" + new string('9', 500);

        foreach (string message in new[]
        {
            MuxPreviousBuildNotice.LocalMessage(hostile, "0.12.0", 1),
            MuxPreviousBuildNotice.RemoteMessage("nova@" + bidi + "box", hostile, 1),
            MuxPreviousBuildNotice.RemoteUpdateMessage("nova@" + bidi + "box", hostile, "0.12.0"),
        })
        {
            Assert.DoesNotContain(message, c => char.IsControl(c) || c == (char)0x202E);
            Assert.Contains("0.0.1[2Jfake line", message, StringComparison.Ordinal); // what is left of it, in order
            Assert.True(message.Length < 400, $"not capped: {message.Length} characters");
        }

        // The outcomes quote the host the same way.
        foreach (MuxPreviousBuildNotice.RestartOutcome outcome in Enum.GetValues<MuxPreviousBuildNotice.RestartOutcome>())
        {
            string message = MuxPreviousBuildNotice.Outcome(outcome, "nova@" + bidi + "box" + new string('x', 500));
            Assert.DoesNotContain(message, c => char.IsControl(c) || c == (char)0x202E);
            Assert.True(message.Length < 400, $"not capped: {message.Length} characters");
        }
    }

    [Theory]
    [InlineData(null, 3, "Restart Multiplexer", "Restart the multiplexer?", "3 shells running in the multiplexer will be closed.")]
    [InlineData(null, 1, "Restart Multiplexer", "Restart the multiplexer?", "1 shell running in the multiplexer will be closed.")]
    [InlineData(null, -1, "Restart Multiplexer", "Restart the multiplexer?", "All shells running in the multiplexer will be closed.")]
    [InlineData(null, 0, "Restart Multiplexer", "Restart the multiplexer?", "The multiplexer restarts with this version.")]
    [InlineData("nova@box", 2, "Restart ntilde-mux", "Restart ntilde-mux on nova@box?", "2 shells running in ntilde-mux on nova@box will be closed.")]
    [InlineData("nova@box", 0, "Restart ntilde-mux", "Restart ntilde-mux on nova@box?", "ntilde-mux on nova@box is stopped; reconnecting starts the installed version.")]
    public void The_question_names_what_closes(string? host, int shells, string title, string heading, string message)
    {
        Assert.Equal(title, MuxPreviousBuildNotice.ConfirmTitle(host));
        Assert.Equal(heading, MuxPreviousBuildNotice.ConfirmHeading(host));
        Assert.Equal(message, MuxPreviousBuildNotice.ConfirmMessage(host, shells));
        Assert.Equal("Restart", MuxPreviousBuildNotice.ConfirmButton);
    }

    /// <summary>Review items 3, 4 and M3: every confirmed restart that does not happen says why, in words chosen by its cause.</summary>
    [Fact]
    public void Each_outcome_of_a_restart_that_did_not_happen_is_said_plainly()
    {
        (MuxPreviousBuildNotice.RestartOutcome Outcome, string Local, string Remote)[] said =
        [
            (MuxPreviousBuildNotice.RestartOutcome.AlreadyRestarting, "The multiplexer is already being restarted.", "ntilde-mux on nova@box is already being restarted."),
            (MuxPreviousBuildNotice.RestartOutcome.AlreadyThisBuild, "The multiplexer is already from this build; nothing to restart.", "ntilde-mux on nova@box is already the installed version or newer; nothing to restart."),
            (MuxPreviousBuildNotice.RestartOutcome.NoVersion, "The multiplexer does not report its build; it was not restarted.", "ntilde-mux on nova@box does not report its version; it was not restarted."),
            (MuxPreviousBuildNotice.RestartOutcome.InstalledVersionUnknown, "The installed multiplexer's version is not known; it was not restarted.", "The version of ntilde-mux installed on nova@box is not known; it was not restarted."),
            (MuxPreviousBuildNotice.RestartOutcome.NotRunning, "The multiplexer is not running; nothing to restart.", "ntilde-mux on nova@box is not running; nothing to restart."),
            (MuxPreviousBuildNotice.RestartOutcome.Unreachable, "The multiplexer could not be reached; nothing was restarted.", "ntilde-mux on nova@box could not be reached; nothing was restarted."),
            (MuxPreviousBuildNotice.RestartOutcome.NotConnected, "The multiplexer is not connected; it was not restarted.", "ntilde-mux on nova@box is not connected; it was not restarted."),
            (MuxPreviousBuildNotice.RestartOutcome.ShutdownFailed, "The multiplexer could not be told to restart; it keeps running.", "ntilde-mux on nova@box could not be told to restart; it keeps running."),
            (MuxPreviousBuildNotice.RestartOutcome.NotStopped, "The old multiplexer could not be stopped; its shells may still be running.", "The old ntilde-mux on nova@box could not be stopped; its shells may still be running."),
            (MuxPreviousBuildNotice.RestartOutcome.StopUnconfirmed, "The old multiplexer was told to stop, but whether it did could not be checked.", "The old ntilde-mux on nova@box was told to stop, but whether it did could not be checked."),
            (MuxPreviousBuildNotice.RestartOutcome.Failed, "The multiplexer restart failed; see the log.", "The restart of ntilde-mux on nova@box failed; see the log."),
        ];

        Assert.Equal(Enum.GetValues<MuxPreviousBuildNotice.RestartOutcome>().Length, said.Length); // every outcome is worded
        foreach ((MuxPreviousBuildNotice.RestartOutcome outcome, string local, string remote) in said)
        {
            Assert.Equal(local, MuxPreviousBuildNotice.Outcome(outcome, host: null));
            Assert.Equal(remote, MuxPreviousBuildNotice.Outcome(outcome, "nova@box"));
        }
    }

    /// <summary>
    /// Once per launch for each endpoint: the first offer wins, later ones (a reconnect, another window) do not; a release
    /// - after a restart, or any way one did not happen - lets the next connection offer it again. One restart at a time
    /// for each endpoint, process-wide; another endpoint's runs alongside it.
    /// </summary>
    [Fact]
    public void An_endpoint_is_offered_once_until_released_and_restarted_once_at_a_time()
    {
        var launch = new MuxPreviousBuildNotice.Launch();
        MuxEndpointId remote = MuxEndpointId.ForSsh(Guid.NewGuid());

        Assert.True(launch.TryOffer(MuxEndpointId.Local));
        Assert.False(launch.TryOffer(MuxEndpointId.Local));
        Assert.True(launch.TryOffer(remote)); // per endpoint
        Assert.False(launch.TryOffer(MuxEndpointId.ForSsh(remote.SshProfileId!.Value)));
        launch.ReleaseOffer(MuxEndpointId.Local);
        Assert.True(launch.TryOffer(MuxEndpointId.Local));
        Assert.False(launch.TryOffer(remote));

        Assert.True(launch.TryBeginRestart(MuxEndpointId.Local));
        Assert.False(launch.TryBeginRestart(MuxEndpointId.Local));
        Assert.True(launch.TryBeginRestart(remote));
        launch.EndRestart(MuxEndpointId.Local);
        Assert.True(launch.TryBeginRestart(MuxEndpointId.Local));
        Assert.False(launch.TryBeginRestart(remote));
    }
}
