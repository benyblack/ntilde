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
        return value.Contains(':') && !(value.StartsWith('[') && value.EndsWith(']'))
            ? $"[{value}]"
            : value;
    }
}
