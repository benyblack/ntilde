using System.Reflection;
using System.Text;

namespace Ntilde.Shell.Mux.Remote;

/// <summary>
/// The SHA-256 of each released <c>ntilde-mux-&lt;rid&gt;</c>, built into this app by <c>release.yml</c>
/// (Phase 5 Task 10): <see cref="GitHubReleaseMuxAssetSource"/> accepts a download only when it hashes to
/// the pin, so a swapped release asset with a matching swapped <c>.sha256</c> is refused. A build made
/// without <c>NtildeMuxSha256Dir</c> (a dev or CI build) embeds nothing, and <see cref="Load"/> is empty.
/// </summary>
internal static class MuxAssetPins
{
    /// <summary>The resource name prefix <c>Ntilde.App.csproj</c> gives each embedded checksum file.</summary>
    internal const string ResourcePrefix = "Ntilde.Resources.mux-sha256.ntilde-mux-";

    private const string ResourceSuffix = ".sha256";

    /// <summary>
    /// The value <see cref="Parse"/> keeps for a RID whose checksum resource is there but holds no SHA-256: never a
    /// hash, so nothing matches it, and <see cref="GitHubReleaseMuxAssetSource"/> refuses that RID.
    /// </summary>
    internal const string Unusable = "";

    /// <summary>RID to lowercase hex, from the checksum files embedded in this assembly; empty when none are.</summary>
    internal static IReadOnlyDictionary<string, string> Load()
    {
        Assembly assembly = typeof(MuxAssetPins).Assembly;
        var resources = new List<KeyValuePair<string, string>>();
        foreach (string name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            using Stream? stream = assembly.GetManifestResourceStream(name);
            if (stream is null || stream.Length > 4096)
            {
                // There but unreadable: Parse keeps it as Unusable, so the RID is refused rather than unpinned.
                resources.Add(new(name, string.Empty));
                continue;
            }

            using var reader = new StreamReader(stream, Encoding.ASCII);
            resources.Add(new(name, reader.ReadToEnd()));
        }

        return Parse(resources);
    }

    /// <summary>
    /// Pins from (resource name, file text) pairs. A resource that is not
    /// <c>Ntilde.Resources.mux-sha256.ntilde-mux-&lt;rid&gt;.sha256</c>, or whose RID is no plain name, is ignored.
    /// One whose text holds no SHA-256 is kept as <see cref="Unusable"/>, never dropped: dropping it would leave a
    /// release build unpinned for that RID, and so trusting the release's own <c>.sha256</c> (release hardening item 4).
    /// Neither ever fails the app; only remote installs for that RID are refused.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> Parse(IEnumerable<KeyValuePair<string, string>> resources)
    {
        var pins = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string name, string text) in resources)
        {
            if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal) || !name.EndsWith(ResourceSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            string rid = name[ResourcePrefix.Length..^ResourceSuffix.Length];
            if (!MuxDaemonAsset.IsPlainName(rid))
            {
                continue;
            }

            pins[rid] = MuxDaemonAsset.TryParseChecksum(text, out string hex) ? hex : Unusable;
        }

        return pins;
    }
}
