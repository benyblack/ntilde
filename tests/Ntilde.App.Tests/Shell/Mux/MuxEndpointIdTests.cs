using Ntilde.Shell.Mux;

namespace Ntilde.Tests.Shell.Mux;

/// <summary>
/// Phase 4 spec §5: <c>PaneNode.MuxEndpoint</c> is <c>local</c> or <c>ssh:&lt;sshProfileId&gt;</c>. Files
/// written before Phase 4 hold the local pipe or socket name there, or nothing: both mean local.
/// </summary>
public sealed class MuxEndpointIdTests
{
    private const string ProfileN = "0f8fad5bd9cb469fa16570867728950e";
    private static readonly Guid Profile = Guid.ParseExact(ProfileN, "N");

    [Theory]
    [InlineData(null, "local")]
    [InlineData("", "local")]
    [InlineData("local", "local")]
    // Legacy values: Host.Endpoint, the local daemon's pipe (Windows) or socket path (Unix).
    [InlineData("ntilde-mux-behna-1a2b3c4d", "local")]
    [InlineData("/home/u/.local/share/ntilde/mux/mux.sock", "local")]
    [InlineData("/run/user/1000/ntilde-mux-u-1a2b/mux.sock", "local")]
    [InlineData("npipe://ntilde-mux/default", "local")]
    [InlineData("ssh:" + ProfileN, "ssh:" + ProfileN)]
    // Lenient on read, canonical (N format, lower case) on write.
    [InlineData("ssh:0F8FAD5B-D9CB-469F-A165-70867728950E", "ssh:" + ProfileN)]
    [InlineData("SSH:" + ProfileN, "ssh:" + ProfileN)]
    // Not an ssh endpoint anyone wrote: local, and the caller logs it.
    [InlineData("ssh:not-a-guid", "local")]
    [InlineData("ssh:", "local")]
    public void Parse_then_ToString_is_canonical(string? persisted, string expected)
    {
        Assert.Equal(expected, MuxEndpointId.Parse(persisted).ToString());
        // A canonical value round-trips unchanged.
        Assert.Equal(expected, MuxEndpointId.Parse(expected).ToString());
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("local", true)]
    [InlineData("ntilde-mux-behna-1a2b3c4d", true)]
    [InlineData("ssh:" + ProfileN, true)]
    [InlineData("ssh:not-a-guid", false)]
    [InlineData("ssh:", false)]
    public void TryParse_flags_only_a_malformed_ssh_endpoint(string? persisted, bool recognized)
    {
        Assert.Equal(recognized, MuxEndpointId.TryParse(persisted, out MuxEndpointId id));
        Assert.Equal(MuxEndpointId.Parse(persisted), id);
    }

    [Fact]
    public void An_ssh_endpoint_names_its_profile()
    {
        MuxEndpointId ssh = MuxEndpointId.ForSsh(Profile);

        Assert.False(ssh.IsLocal);
        Assert.Equal(Profile, ssh.SshProfileId);
        Assert.Equal("ssh:" + ProfileN, ssh.ToString());
        Assert.Equal(ssh, MuxEndpointId.Parse(ssh.ToString()));
        Assert.NotEqual(MuxEndpointId.Local, ssh);
        Assert.NotEqual(MuxEndpointId.ForSsh(Guid.NewGuid()), ssh);
    }

    [Fact]
    public void Local_is_the_default_value()
    {
        Assert.True(MuxEndpointId.Local.IsLocal);
        Assert.Null(MuxEndpointId.Local.SshProfileId);
        Assert.Equal("local", MuxEndpointId.Local.ToString());
        Assert.Equal(MuxEndpointId.Local, default(MuxEndpointId));
        Assert.Equal(MuxEndpointId.Local, MuxEndpointId.Parse("ntilde-mux-legacy"));
    }

    [Fact]
    public void The_local_policy_keeps_todays_numbers_and_the_remote_one_differs()
    {
        // Spec §5's table: local is unchanged; a remote host waits for an askpass prompt and never
        // swallows a user's retry.
        Assert.Equal(TimeSpan.FromSeconds(5), MuxHostPolicy.Local.ConnectTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), MuxHostPolicy.Local.FailureCooldown);
        Assert.Equal(TimeSpan.FromSeconds(3), MuxHostPolicy.Local.RpcTimeout);
        Assert.False(MuxHostPolicy.Local.IsRemote);

        MuxHostPolicy remote = MuxHostPolicy.Remote("build box");
        Assert.Equal(TimeSpan.FromSeconds(120), remote.ConnectTimeout);
        Assert.Equal(TimeSpan.Zero, remote.FailureCooldown);
        Assert.Equal(TimeSpan.FromSeconds(10), remote.RpcTimeout);
        Assert.True(remote.IsRemote);
        Assert.Equal("build box", remote.DisplayName);
    }
}
