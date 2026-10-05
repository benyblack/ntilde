using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using Ntilde.Mux.Contracts;

namespace Ntilde.Mux.Cli;

/// <summary>
/// What <c>ntilde-mux --version</c> reports (Phase 4 spec §8.1, §9): the build, the protocol range it
/// serves and the platform it was built for. Public: the App's installer reads the
/// <c>--version --json</c> form back to decide whether a remote host's binary can serve it.
/// </summary>
/// <param name="Version">The build's version (<see cref="MuxCliHost.Version"/>).</param>
/// <param name="ProtocolMin">The oldest mux protocol version it speaks.</param>
/// <param name="ProtocolMax">The newest mux protocol version it speaks.</param>
/// <param name="Rid">The runtime identifier it was built for, e.g. <c>linux-x64</c>.</param>
/// <param name="Path">Where the executable is; empty when the runtime cannot tell.</param>
public sealed record MuxVersionInfo(string Version, int ProtocolMin, int ProtocolMax, string Rid, string Path)
{
    /// <summary>This process, built as <paramref name="version"/>.</summary>
    internal static MuxVersionInfo Current(string version) => new(
        version, MuxProtocol.MinSupportedVersion, MuxProtocol.MaxSupportedVersion, RuntimeInformation.RuntimeIdentifier, Environment.ProcessPath ?? string.Empty);

    /// <summary>The plain <c>--version</c> line: <c>ntilde-mux &lt;v&gt; (protocol 1-2, &lt;rid&gt;)</c>.</summary>
    internal string ToDisplayString() =>
        string.Create(CultureInfo.InvariantCulture, $"ntilde-mux {Version} (protocol {ProtocolMin}-{ProtocolMax}, {Rid})");
}

/// <summary>
/// Source-generated JSON for the CLI's own output, reflection-free (Native AOT). camelCase, as
/// <see cref="MuxJsonContext"/>: <c>{"version","protocolMin","protocolMax","rid","path"}</c>.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
[JsonSerializable(typeof(MuxVersionInfo))]
public sealed partial class MuxCliJsonContext : JsonSerializerContext
{
}
