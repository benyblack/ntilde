using Ntilde.Shell;
using Xunit;

namespace Ntilde.Tests.Input;

public sealed class Ipv6ScpEndpointTests
{
    [Theory]
    [InlineData("2001:db8::1", "[2001:db8::1]")]
    [InlineData("[2001:db8::1]", "[2001:db8::1]")]
    [InlineData("bastion.example", "bastion.example")]
    public void Legacy_jump_chain_and_scp_operand_format_ipv6(string host, string endpoint)
    {
        var jump = new TerminalProfile { SshHost = host, SshUser = "ops", SshPort = 2200 };
        var target = new TerminalProfile { SshHost = host, SshUser = "dev", JumpHostProfileId = jump.Id };
        List<TerminalProfile> profiles = [jump, target];
        Assert.Contains($"-J ops@{endpoint}:2200", target.GenerateSshArguments(profiles));
        var job = new TransferJob { LocalPath = "/local/file", RemotePath = "/remote/file", Direction = TransferDirection.Download };
        string args = SftpService.BuildScpArguments(job, target, null, profiles);
        Assert.Contains($"-J ops@{endpoint}:2200", args);
        Assert.Contains(SftpService.QuoteArg($"dev@{endpoint}:/remote/file"), args);
    }
}
