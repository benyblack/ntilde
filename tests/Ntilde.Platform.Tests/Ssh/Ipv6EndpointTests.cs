using Ntilde.Platform.Ssh.Launch;
using Ntilde.Platform.Ssh.Models;
using Ntilde.Platform.Ssh.OpenSsh;

namespace Ntilde.Platform.Tests.Ssh;

public sealed class Ipv6EndpointTests
{
    [Theory]
    [InlineData("bastion.example:2200")]
    [InlineData("ops@bastion.example:2200")]
    [InlineData("ops@[2001:db8::1]:2200")]
    [InlineData("[2001:db8::1]:2200")]
    public void Jump_host_preserves_existing_endpoint_spellings(string host)
    {
        var profile = new SshProfile
        {
            Host = "target.example",
            JumpHops = { new SshJumpHop { Host = host } }
        };
        Assert.Contains(host, SshArgBuilder.BuildArguments(profile));
        Assert.Contains("ProxyJump " + host, new OpenSshConfigCompiler().BuildHostOptions(profile));
    }

    [Theory]
    [InlineData("2001:db8::1", "[2001:db8::1]")]
    [InlineData(" [2001:db8::1] ", "[2001:db8::1]")]
    [InlineData("fe80::1%eth0", "[fe80::1%eth0]")]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("host.example", "host.example")]
    public void Jump_and_forward_endpoints_are_unambiguous(string host, string endpoint)
    {
        var profile = new SshProfile
        {
            Host = "2001:db8::2",
            JumpHops =
            {
                new SshJumpHop { Host = host },
                new SshJumpHop { Host = host, User = "ops", Port = 2200 }
            },
            Forwards =
            {
                new PortForward { Kind = PortForwardKind.Local, BindAddress = host, SourcePort = 8080, DestinationHost = host, DestinationPort = 80 },
                new PortForward { Kind = PortForwardKind.Remote, BindAddress = host, SourcePort = 8081, DestinationHost = host, DestinationPort = 81 },
                new PortForward { Kind = PortForwardKind.Dynamic, BindAddress = host, SourcePort = 1080 }
            }
        };
        string jump = $"{endpoint},ops@{endpoint}:2200";
        IReadOnlyList<string> args = SshArgBuilder.BuildArguments(profile);
        Assert.Contains(jump, args);
        Assert.Contains($"{endpoint}:8080:{endpoint}:80", args);
        Assert.Contains($"{endpoint}:8081:{endpoint}:81", args);
        Assert.Contains($"{endpoint}:1080", args);
        Assert.Equal("2001:db8::2", args[^1]);

        IReadOnlyList<string> options = new OpenSshConfigCompiler().BuildHostOptions(profile);
        Assert.Contains("HostName 2001:db8::2", options);
        Assert.Contains("ProxyJump " + jump, options);
        Assert.Contains($"LocalForward {endpoint}:8080 {endpoint}:80", options);
        Assert.Contains($"RemoteForward {endpoint}:8081 {endpoint}:81", options);
        Assert.Contains($"DynamicForward {endpoint}:1080", options);
    }
}
