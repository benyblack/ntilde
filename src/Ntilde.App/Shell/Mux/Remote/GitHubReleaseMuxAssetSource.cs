using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Ntilde.VT;

namespace Ntilde.Shell.Mux.Remote;

/// <summary>
/// The binary from the GitHub release of the running app's version (Phase 4 spec §9 step 2(a), §2
/// decision 2): <c>ntilde-mux-&lt;rid&gt;</c>, verified against <c>ntilde-mux-&lt;rid&gt;.sha256</c>, both
/// published by <c>release.yml</c>'s <c>publish_mux_daemon</c> job. A verified download is cached under
/// <c>&lt;cacheDirectory&gt;/&lt;version&gt;/&lt;rid&gt;/</c>, and a cache hit is verified again before it is used.
/// A release build also carries the expected hashes (<see cref="MuxAssetPins"/>) and holds both the download
/// and the cache to them.
/// </summary>
/// <remarks>
/// <para>
/// Both asset names are a contract with release.yml: renaming either breaks every installed app's remote
/// installs. GitHub answers a release download with a redirect to its CDN, so the
/// <see cref="HttpClient"/> must follow redirects (<see cref="CreateHttpClient"/>'s does). A version that
/// has no release, such as a dev build's, is a 404, which throws <see cref="MuxReleaseNotFoundException"/>.
/// </para>
/// <para>
/// The download is bounded in size (<see cref="MuxDaemonAsset.MaxBytes"/>) and in time
/// (<see cref="DefaultDownloadTimeout"/>, which covers the body too, unlike <see cref="HttpClient.Timeout"/>
/// with <see cref="HttpCompletionOption.ResponseHeadersRead"/>).
/// </para>
/// </remarks>
/// <param name="http">Used as is; tests pass one over a fake handler.</param>
/// <param name="appVersion">The release version, without build metadata (<see cref="AppVersionInfo.Version"/>).</param>
/// <param name="cacheDirectory">Usually <see cref="DefaultCacheDirectory"/>.</param>
/// <param name="pins">
/// RID to the SHA-256 this app was released with (<see cref="MuxAssetPins.Load"/>; null or empty in a dev
/// build). With a pin for the RID the pin, not the release's own <c>.sha256</c>, decides what is accepted,
/// and a cached copy is checked against it too. With pins but none usable for the RID (a release build missing
/// one, or one <see cref="MuxAssetPins.Unusable"/>), the RID is refused: only a dev build, with no pins at
/// all, trusts the release's <c>.sha256</c> alone.
/// </param>
internal sealed class GitHubReleaseMuxAssetSource(
    HttpClient http,
    string appVersion,
    string cacheDirectory,
    IReadOnlyDictionary<string, string>? pins = null) : IMuxDaemonAssetSource
{
    /// <summary>Where release assets are downloaded from: <c>&lt;base&gt;v&lt;version&gt;/&lt;asset&gt;</c>.</summary>
    public const string ReleaseDownloadBase = "https://github.com/benyblack/ntilde/releases/download/";

    /// <summary>How long one <see cref="GetAsync"/> may take, both downloads together.</summary>
    public static readonly TimeSpan DefaultDownloadTimeout = TimeSpan.FromMinutes(5);

    /// <summary>The release's <c>.sha256</c> is not the one this app was released with.</summary>
    internal const string PinMismatchMessage = "The release's checksum does not match the one built into this app.";

    /// <summary>
    /// This app is a release build (it carries pins) but has no usable pin for <paramref name="rid"/>: the release's own
    /// <c>.sha256</c> is not trusted in its place.
    /// </summary>
    internal static string NoPinMessage(string rid) =>
        $"This build of ntilde carries no checksum for ntilde-mux-{rid}, so a download for that platform cannot be verified. Install ntilde-mux from a file instead.";

    private const string CachedBinaryName = "ntilde-mux";
    private const int MaxChecksumBytes = 4096;
    private const int ReadBufferBytes = 81920;

    /// <summary><c>&lt;app data&gt;/cache/ntilde-mux</c>: the cache this source keeps in the app.</summary>
    public static string DefaultCacheDirectory => Path.Combine(AppPaths.RootDirectory, "cache", "ntilde-mux");

    /// <summary>The largest binary accepted; settable for tests.</summary>
    internal long MaxAssetBytes { get; init; } = MuxDaemonAsset.MaxBytes;

    /// <summary>The bound on one <see cref="GetAsync"/>; settable for tests.</summary>
    internal TimeSpan DownloadTimeout { get; init; } = DefaultDownloadTimeout;

    /// <summary><c>https://github.com/benyblack/ntilde/releases/download/v{version}/ntilde-mux-{rid}</c>.</summary>
    public static string AssetUrl(string version, string rid) => $"{ReleaseDownloadBase}v{version}/ntilde-mux-{rid}";

    /// <summary>
    /// A client for this source: it follows redirects (GitHub's CDN), names the app in its User-Agent, and
    /// leaves the time bound to <see cref="GetAsync"/>.
    /// </summary>
    public static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            ConnectTimeout = TimeSpan.FromSeconds(30),
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        string version = AppVersionInfo.Version;
        client.DefaultRequestHeaders.UserAgent.Add(MuxDaemonAsset.IsPlainName(version)
            ? new ProductInfoHeaderValue("ntilde", version)
            : new ProductInfoHeaderValue(new ProductHeaderValue("ntilde")));
        return client;
    }

    /// <exception cref="MuxReleaseNotFoundException">This version has no release (or is unknown), or the release has no asset for <paramref name="rid"/>.</exception>
    /// <exception cref="InvalidDataException">The checksum does not match (<c>checksum mismatch …</c>), the checksum file is malformed, the asset is too large, or this release build has no usable pin for <paramref name="rid"/> (<see cref="NoPinMessage"/>).</exception>
    /// <exception cref="HttpRequestException">Another HTTP failure.</exception>
    /// <exception cref="TimeoutException">The downloads outlasted <see cref="DownloadTimeout"/>.</exception>
    public async Task<MuxDaemonAsset> GetAsync(string rid, IProgress<long>? progress, CancellationToken ct)
    {
        // Both go into a URL and a cache path. A version that is no plain name (empty: the build's is
        // unknown) can have no release, which the dialog explains as it does a 404.
        if (!MuxDaemonAsset.IsPlainName(rid)) throw new ArgumentException($"Not a runtime identifier: \"{rid}\"", nameof(rid));
        if (!MuxDaemonAsset.IsPlainName(appVersion)) throw new MuxReleaseNotFoundException(appVersion);
        ct.ThrowIfCancellationRequested();

        string directory = Path.Combine(cacheDirectory, appVersion, rid);
        string binaryPath = Path.Combine(directory, CachedBinaryName);
        string checksumPath = binaryPath + ".sha256";
        string? pin = pins is not null && pins.TryGetValue(rid, out string? pinned) && pinned.Length > 0 ? pinned.ToLowerInvariant() : null;
        if (pin is null && pins is { Count: > 0 })
        {
            // A release build: the release's own .sha256 sits beside the binary it would vouch for, so it is no check.
            throw new InvalidDataException(NoPinMessage(rid));
        }

        if (await TryReadCacheAsync(binaryPath, checksumPath, pin, ct).ConfigureAwait(false) is { } cached)
        {
            progress?.Report(cached.Bytes.Length);
            return cached;
        }

        string url = AssetUrl(appVersion, rid);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(DownloadTimeout);
        byte[] bytes;
        string expected;
        try
        {
            // The small checksum first: a release without this asset fails before megabytes move.
            byte[] checksumFile = await DownloadAsync(url + ".sha256", MaxChecksumBytes, null, deadline.Token).ConfigureAwait(false);
            if (!MuxDaemonAsset.TryParseChecksum(Encoding.ASCII.GetString(checksumFile), out expected))
            {
                throw new InvalidDataException($"{url}.sha256 is not a SHA-256 checksum file.");
            }

            if (pin is not null && !string.Equals(expected, pin, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(PinMismatchMessage);
            }

            bytes = await DownloadAsync(url, MaxAssetBytes, progress, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our deadline, or the HttpClient's own timeout: either way, not the caller's cancel.
            throw new TimeoutException(string.Create(CultureInfo.InvariantCulture,
                $"Downloading {url} did not finish within {DownloadTimeout.TotalSeconds:0.###} s."));
        }

        string actual = MuxDaemonAsset.Sha256Of(bytes);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"checksum mismatch: {url} has SHA-256 {actual}, but its .sha256 says {expected}");
        }

        TryWriteCache(directory, binaryPath, checksumPath, bytes, actual, rid);
        return new MuxDaemonAsset(bytes, actual, url);
    }

    private async Task<byte[]> DownloadAsync(string url, long maxBytes, IProgress<long>? progress, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new MuxReleaseNotFoundException(appVersion);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                string.Create(CultureInfo.InvariantCulture, $"Downloading {url} failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}"),
                inner: null,
                response.StatusCode);
        }

        if (response.Content.Headers.ContentLength is long declared && declared > maxBytes)
        {
            throw TooLarge(url, maxBytes);
        }

        Stream body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (body.ConfigureAwait(false))
        {
            using var kept = new MemoryStream();
            byte[] buffer = new byte[ReadBufferBytes];
            long total = 0;
            while (true)
            {
                int read = await body.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0)
                {
                    return kept.ToArray();
                }

                total += read;
                if (total > maxBytes)
                {
                    throw TooLarge(url, maxBytes);
                }

                kept.Write(buffer, 0, read);
                progress?.Report(total);
            }
        }
    }

    private static InvalidDataException TooLarge(string url, long maxBytes) =>
        new(maxBytes % (1024 * 1024) == 0
            ? $"{url} is larger than {(maxBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture)} MiB."
            : $"{url} is larger than {maxBytes.ToString(CultureInfo.InvariantCulture)} bytes.");

    /// <summary>The cached binary, when it and its <c>.sha256</c> are there and agree; otherwise null.</summary>
    private async Task<MuxDaemonAsset?> TryReadCacheAsync(string binaryPath, string checksumPath, string? pin, CancellationToken ct)
    {
        try
        {
            var binary = new FileInfo(binaryPath);
            var checksum = new FileInfo(checksumPath);
            if (!binary.Exists || !checksum.Exists || binary.Length == 0 || binary.Length > MaxAssetBytes || checksum.Length > MaxChecksumBytes)
            {
                return null;
            }

            string checksumText = await File.ReadAllTextAsync(checksumPath, Encoding.ASCII, ct).ConfigureAwait(false);
            if (!MuxDaemonAsset.TryParseChecksum(checksumText, out string expected))
            {
                return null;
            }

            // A copy that agrees with its own .sha256 but not with the app's pin is a miss: it is replaced
            // by a download, which the pin then verifies.
            if (pin is not null && !string.Equals(expected, pin, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            byte[] bytes = await File.ReadAllBytesAsync(binaryPath, ct).ConfigureAwait(false);
            string actual = MuxDaemonAsset.Sha256Of(bytes);
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase) ? new MuxDaemonAsset(bytes, actual, binaryPath) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable cache is a miss: the download replaces it.
            return null;
        }
    }

    /// <summary>
    /// Caches a verified download: each file written beside its final name, then moved over it, so a
    /// reader never sees half a file. The binary goes first; a cache hit re-verifies the pair anyway.
    /// </summary>
    private static void TryWriteCache(string directory, string binaryPath, string checksumPath, byte[] bytes, string sha256Hex, string rid)
    {
        try
        {
            Directory.CreateDirectory(directory);
            WriteAtomically(binaryPath, bytes);
            WriteAtomically(checksumPath, Encoding.ASCII.GetBytes($"{sha256Hex}  ntilde-mux-{rid}\n"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The cache only saves the next download; this one is verified and used regardless.
            TerminalLogger.Log($"[RemoteMuxInstaller] could not cache {binaryPath}: {ex.Message}");
        }
    }

    private static void WriteAtomically(string path, byte[] bytes)
    {
        string temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
