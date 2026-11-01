using Ntilde.Platform.Ssh.Models;
using Ntilde.Platform.Ssh.Native;

namespace Ntilde.Platform.Tests.Ssh;

public sealed class NativeSharingKeyTests
{
    [Fact]
    public void Valid_ipv6_jump_addresses_and_ports_have_distinct_keys()
    {
        var profile = Profile();
        profile.JumpHops.Add(new() { Host = "2001:db8::1", User = "jump", Port = 2222 });
        string first = Key(profile);
        profile.JumpHops[0].Host = "2001:db8::1:2222";
        profile.JumpHops[0].Port = 22;
        Assert.NotEqual(first, Key(profile));
    }

    [Fact]
    public void Field_boundaries_cannot_be_shifted_with_newlines()
    {
        var profile = Profile();
        var first = new NativeSshConnectionOptions { Host = "first\nuser", User = "target" };
        var second = new NativeSshConnectionOptions { Host = "first", User = "user\ntarget" };
        Assert.NotEqual(NativeSshConnectionPool.KeyFor(profile, first), NativeSshConnectionPool.KeyFor(profile, second));
    }

    [Fact]
    public void A_comma_in_one_jump_cannot_impersonate_two_jumps()
    {
        var profile = Profile();
        profile.JumpHops.Add(new() { Host = "one,two" });
        string first = Key(profile);
        profile.JumpHops.Clear();
        profile.JumpHops.Add(new() { Host = "one" });
        profile.JumpHops.Add(new() { Host = "two" });
        Assert.NotEqual(first, Key(profile));
    }

    [Fact]
    public void A_forward_display_string_cannot_impersonate_two_forwards()
    {
        var profile = Profile();
        profile.Forwards.Add(new() { SourcePort = 8080, DestinationHost = "one:80,Local 8081 -> two", DestinationPort = 81 });
        string first = Key(profile);
        profile.Forwards.Clear();
        profile.Forwards.Add(new() { SourcePort = 8080, DestinationHost = "one", DestinationPort = 80 });
        profile.Forwards.Add(new() { SourcePort = 8081, DestinationHost = "two", DestinationPort = 81 });
        Assert.NotEqual(first, Key(profile));
    }

    [Fact]
    public void Equivalent_cloned_routes_share_but_reordered_hops_do_not()
    {
        var profile = Profile();
        profile.JumpHops.Add(new() { Host = "2001:db8::1", User = "ops", Port = 2200 });
        profile.JumpHops.Add(new() { Host = "bastion", User = "ops" });
        profile.Forwards.Add(new() { SourcePort = 8080, DestinationHost = "internal", DestinationPort = 80 });
        string first = Key(profile);
        Assert.Equal(first, Key(profile)); // each factory call deep-copies the hop list
        profile.JumpHops.Reverse();
        Assert.NotEqual(first, Key(profile));
    }

    private static SshProfile Profile() => new() { Host = "target", User = "ops" };
    private static string Key(SshProfile profile) => NativeSshConnectionPool.KeyFor(profile, NativeSshConnectionOptionsFactory.Create(profile));
}
