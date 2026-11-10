using Ntilde.Platform.Ssh.Models;

namespace Ntilde.Platform.Tests.Ssh;

public sealed class PortForwardDescribeTests
{
    [Fact]
    public void Local_with_no_bind_address_reads_localhost()
    {
        var forward = new PortForward { Kind = PortForwardKind.Local, SourcePort = 8080, DestinationHost = "db.internal", DestinationPort = 5432 };

        Assert.Equal("localhost:8080 -> db.internal:5432", forward.DescribeEndpoints());
        Assert.Equal("Local localhost:8080 -> db.internal:5432", forward.ToString());
    }

    [Fact]
    public void Remote_keeps_an_explicit_bind_address()
    {
        var forward = new PortForward { Kind = PortForwardKind.Remote, BindAddress = "0.0.0.0", SourcePort = 9000, DestinationHost = "localhost", DestinationPort = 3000 };

        Assert.Equal("Remote 0.0.0.0:9000 -> localhost:3000", forward.ToString());
    }

    [Fact]
    public void Dynamic_has_no_destination()
    {
        var forward = new PortForward { Kind = PortForwardKind.Dynamic, SourcePort = 1080, DestinationHost = "ignored", DestinationPort = 1 };

        Assert.Equal("localhost:1080 (SOCKS)", forward.DescribeEndpoints());
    }

    [Fact]
    public void Ipv6_literals_are_bracketed_so_the_port_is_unambiguous()
    {
        var forward = new PortForward { Kind = PortForwardKind.Local, BindAddress = "::1", SourcePort = 8080, DestinationHost = "fe80::1", DestinationPort = 22 };

        Assert.Equal("[::1]:8080 -> [fe80::1]:22", forward.DescribeEndpoints());
    }
}
