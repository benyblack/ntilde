using System.Security.Cryptography;

namespace Ntilde.Shell.Mux.Remote;

/// <summary>
/// Where the install flow gets the <c>ntilde-mux</c> binary for a host (Phase 4 spec §9 step 2, §2
/// decision 2): the GitHub release for the running app's version (<see cref="GitHubReleaseMuxAssetSource"/>)
/// or a file the user picks (<see cref="LocalFileMuxAssetSource"/>).
/// </summary>
internal interface IMuxDaemonAssetSource
{
    /// <summary>The binary for <paramref name="rid"/> (<c>linux-x64</c>, <c>linux-arm64</c>, <c>osx-arm64</c>).</summary>
    /// <param name="progress">Receives the total bytes read so far.</param>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    /// <remarks>Any other exception's message is the reason the dialog shows.</remarks>
    Task<MuxDaemonAsset> GetAsync(string rid, IProgress<long>? progress, CancellationToken ct);
}

/// <summary>A binary ready to upload.</summary>
/// <param name="Bytes">The whole executable.</param>
/// <param name="Sha256Hex">Its SHA-256, lowercase hex: verified against the release's for a download, shown for a picked file.</param>
/// <param name="Origin">Where it came from, for the dialog's log: a URL or a local path.</param>
internal sealed record MuxDaemonAsset(byte[] Bytes, string Sha256Hex, string Origin)
{
    /// <summary>
    /// The largest binary either source accepts. A release binary is a few MB (spec §2 decision 1), and the
    /// upload holds it in memory, so this bounds what a wrong file or a hostile server can make it hold.
    /// </summary>
    public const long MaxBytes = 64L * 1024 * 1024;

    /// <summary>SHA-256 as lowercase hex.</summary>
    public static string Sha256Of(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>
    /// The hash in a <c>sha256sum</c> / <c>shasum -a 256</c> line (<c>&lt;hex&gt;  ntilde-mux-&lt;rid&gt;</c>): its
    /// first token, when that is 64 hex digits.
    /// </summary>
    public static bool TryParseChecksum(string text, out string sha256Hex)
    {
        sha256Hex = string.Empty;
        string[] tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0 || tokens[0].Length != 64 || !tokens[0].All(char.IsAsciiHexDigit))
        {
            return false;
        }

        sha256Hex = tokens[0].ToLowerInvariant();
        return true;
    }

    /// <summary>
    /// True for a RID or a version that is safe as a URL and path segment: ASCII letters, digits and
    /// <c>. _ + -</c>, not starting with a dot.
    /// </summary>
    internal static bool IsPlainName(string? value) =>
        !string.IsNullOrEmpty(value)
        && value[0] != '.'
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '+' or '-');
}

/// <summary>
/// The GitHub release for this version has no <c>ntilde-mux</c> asset (HTTP 404): a dev build, or a
/// version that was never released. The dialog then offers a local file or the offline one-liner.
/// </summary>
internal sealed class MuxReleaseNotFoundException(string version)
    : Exception($"No ntilde-mux release for {version} \u2014 choose a file or copy the install command")
{
    /// <summary>The app version that has no release.</summary>
    public string Version { get; } = version;
}
