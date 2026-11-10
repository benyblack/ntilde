using System.Globalization;
using Ntilde.Platform.Ssh.OpenSsh;

namespace Ntilde.Platform.Ssh.Models;

public enum PortForwardKind
{
    Local = 0,
    Remote = 1,
    Dynamic = 2
}

public sealed class PortForward
{
    public PortForwardKind Kind { get; set; } = PortForwardKind.Local;
    public string BindAddress { get; set; } = string.Empty;
    public int SourcePort { get; set; }
    public string DestinationHost { get; set; } = string.Empty;
    public int DestinationPort { get; set; }

    /// <summary>
    /// The forward's endpoints for display: "bind:port -> host:port", or "bind:port (SOCKS)" for a
    /// Dynamic forward. An empty bind address reads "localhost" because that is what both backends
    /// bind (OpenSSH's default; <c>NativePortForwardSession</c> resolves empty to loopback), and IPv6
    /// literals are bracketed so the port stays unambiguous. A method rather than a property so
    /// profile serialization never picks it up.
    /// </summary>
    public string DescribeEndpoints()
    {
        string bind = string.IsNullOrWhiteSpace(BindAddress) ? "localhost" : BindAddress;
        string listen = string.Create(CultureInfo.InvariantCulture, $"{OpenSshEndpoint.FormatHost(bind)}:{SourcePort}");
        return Kind == PortForwardKind.Dynamic
            ? $"{listen} (SOCKS)"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{listen} -> {OpenSshEndpoint.FormatHost(DestinationHost ?? string.Empty)}:{DestinationPort}");
    }

    /// <summary>The kind followed by <see cref="DescribeEndpoints"/>, e.g. "Local localhost:8080 -> db:5432".</summary>
    public override string ToString() => $"{Kind} {DescribeEndpoints()}";
}
