using System.Net;
using System.Net.Sockets;

namespace Ntilde.Platform.Ssh.OpenSsh;

/// <summary>Host syntax for OpenSSH operands that use colons as separators.</summary>
public static class OpenSshEndpoint
{
    /// <summary>
    /// Brackets IPv6 literals in jump hosts, forwarding endpoints and SCP operands.
    /// Direct ssh destinations and HostName options do not need this syntax.
    /// </summary>
    public static string FormatHost(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        string value = host.Trim();
        // A jump Host can also contain an existing user@host:port operand.
        // Only an IPv6 literal needs brackets; preserve those legacy spellings.
        return IPAddress.TryParse(value, out IPAddress? address) &&
            address.AddressFamily == AddressFamily.InterNetworkV6 &&
            !value.StartsWith('[')
            ? $"[{value}]"
            : value;
    }
}
