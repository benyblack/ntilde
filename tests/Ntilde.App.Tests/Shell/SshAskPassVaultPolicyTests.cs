using Ntilde.Platform.Ssh.Models;
using Ntilde.Shell;

namespace Ntilde.Tests.Shell;

/// <summary>
/// <see cref="SshAskPassVaultPolicy"/>: the askpass helper is never offered the vault password for a connection a jump
/// host could answer as the target.
/// </summary>
public sealed class SshAskPassVaultPolicyTests
{
    private static SshProfile Profile(string? extraArgs = null, params (string User, string Host)[] hops)
    {
        var profile = new SshProfile { User = "ops", Host = "prod.internal", ExtraSshArgs = extraArgs ?? string.Empty };
        foreach ((string user, string host) in hops) profile.JumpHops.Add(new SshJumpHop { User = user, Host = host, Port = 2222 });
        return profile;
    }

    [Theory]
    [InlineData(false, false, "", "", "", true)]   // no jump host, ssh 8.1
    [InlineData(true, true, "bob", "bastion", "", true)]   // jump host, 8.4+, distinct hop
    [InlineData(true, false, "bob", "bastion", "", false)]   // jump host, ssh 8.1 (an unknown version is passed as false too; the factory tests cover it)
    [InlineData(true, true, "OPS", "prod.internal", "", false)]   // hop equals the target, case-insensitive
    [InlineData(true, true, "", "PROD.internal", "", false)]   // empty hop user is the profile's user
    [InlineData(true, true, "other", "prod.internal", "", true)]   // same host, another user: prompts differ
    [InlineData(false, false, "", "", "-J bob@bastion", false)]   // proxy in the extra arguments, 8.1
    [InlineData(false, true, "", "", "-o ProxyJump=bob@bastion", true)]   // proxy in the extra arguments, 8.4+
    public void The_vault_is_offered_only_when_no_jump_host_could_ask_as_the_target(
        bool hasHop, bool prefixes, string hopUser, string hopHost, string extra, bool expected)
    {
        SshProfile profile = hasHop ? Profile(extra, (hopUser, hopHost)) : Profile(extra);
        Assert.Equal(expected, SshAskPassVaultPolicy.MayOfferVault(profile, prefixes));
    }
}
