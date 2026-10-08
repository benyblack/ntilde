using System.Net;
using System.Security.Cryptography;
using System.Text;
using Ntilde.Shell.Mux.Remote;

namespace Ntilde.Tests.Shell.Mux.Remote;

/// <summary>
/// Where the install flow gets the binary (Phase 4 spec §9 step 2, §2 decision 2): the GitHub release
/// for the running app's version, verified against its <c>.sha256</c> and cached, or a file the user
/// picks. HTTP is a fake handler, so nothing leaves the machine.
/// </summary>
public sealed class MuxAssetSourceTests : IDisposable
{
    private const string Version = "0.11.0";
    private const string Rid = "linux-x64";
    private const string AssetUrl = "https://github.com/benyblack/ntilde/releases/download/v0.11.0/ntilde-mux-linux-x64";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ntilde-mux-asset-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeHttpHandler _http = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string CacheDirectory => Path.Combine(_root, "cache", "ntilde-mux");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { /* nothing was written */ }
    }

    private static byte[] Binary(int size = 200_000)
    {
        byte[] bytes = new byte[size];
        new Random(4242).NextBytes(bytes);
        return bytes;
    }

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private GitHubReleaseMuxAssetSource Source(string version = Version) => new(new HttpClient(_http), version, CacheDirectory);

    private GitHubReleaseMuxAssetSource PinnedSource(string pinHex) =>
        new(new HttpClient(_http), Version, CacheDirectory, new Dictionary<string, string> { [Rid] = pinHex });

    private const string PinMismatchMessage = "The release's checksum does not match the one built into this app.";

    private void ServeRelease(byte[] binary, string? checksumLine = null)
    {
        _http.Serve(AssetUrl, binary);
        _http.Serve(AssetUrl + ".sha256", Encoding.ASCII.GetBytes(checksumLine ?? $"{Hex(binary)}  ntilde-mux-{Rid}\n"));
    }

    [Fact]
    public void AssetUrl_is_the_release_download_url()
    {
        Assert.Equal(AssetUrl, GitHubReleaseMuxAssetSource.AssetUrl(Version, Rid));
        Assert.Equal(
            "https://github.com/benyblack/ntilde/releases/download/v0.12.3/ntilde-mux-osx-arm64",
            GitHubReleaseMuxAssetSource.AssetUrl("0.12.3", "osx-arm64"));
    }

    [Fact]
    public async Task A_matching_checksum_gives_the_asset_and_caches_it()
    {
        byte[] binary = Binary();
        ServeRelease(binary);
        var progress = new RecordingProgress();

        MuxDaemonAsset asset = await Source().GetAsync(Rid, progress, Ct);

        Assert.Equal(binary, asset.Bytes);
        Assert.Equal(Hex(binary), asset.Sha256Hex);
        Assert.Equal(AssetUrl, asset.Origin);
        Assert.Equal(binary.Length, progress.Reports[^1]);
        Assert.Equal([AssetUrl + ".sha256", AssetUrl], _http.Requests);

        string cached = Path.Combine(CacheDirectory, Version, Rid, "ntilde-mux");
        Assert.Equal(binary, File.ReadAllBytes(cached));
        Assert.Equal($"{Hex(binary)}  ntilde-mux-{Rid}\n", File.ReadAllText(cached + ".sha256"));
        Assert.Equal(["ntilde-mux", "ntilde-mux.sha256"], Directory.GetFiles(Path.GetDirectoryName(cached)!).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_checksum_compares_case_insensitively()
    {
        byte[] binary = Binary();
        ServeRelease(binary, $"{Hex(binary).ToUpperInvariant()} *ntilde-mux-{Rid}\r\n");

        MuxDaemonAsset asset = await Source().GetAsync(Rid, null, Ct);

        Assert.Equal(Hex(binary), asset.Sha256Hex);
    }

    [Fact]
    public async Task A_checksum_mismatch_throws_and_caches_nothing()
    {
        byte[] binary = Binary();
        ServeRelease(binary, $"{Hex(Binary(10))}  ntilde-mux-{Rid}\n");

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Source().GetAsync(Rid, null, Ct));

        Assert.StartsWith("checksum mismatch", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(CacheDirectory, Version, Rid, "ntilde-mux")));
        Assert.False(File.Exists(Path.Combine(CacheDirectory, Version, Rid, "ntilde-mux.sha256")));
    }

    [Fact]
    public async Task A_malformed_checksum_file_throws()
    {
        ServeRelease(Binary(), "<html>Not a checksum</html>");

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Source().GetAsync(Rid, null, Ct));

        Assert.Contains(AssetUrl + ".sha256", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0.10.0")]
    [InlineData("0.12.0-dev")]
    public async Task A_version_with_no_release_says_so_on_404(string version)
    {
        // Nothing served: every URL is a 404, as for a dev build's version.
        var ex = await Assert.ThrowsAsync<MuxReleaseNotFoundException>(() => Source(version).GetAsync(Rid, null, Ct));

        Assert.Equal($"No ntilde-mux release for {version} \u2014 choose a file or copy the install command", ex.Message);
        Assert.Equal(version, ex.Version);
    }

    [Fact]
    public async Task A_missing_binary_is_a_404_too()
    {
        _http.Serve(AssetUrl + ".sha256", Encoding.ASCII.GetBytes($"{Hex(Binary())}  ntilde-mux-{Rid}\n"));

        await Assert.ThrowsAsync<MuxReleaseNotFoundException>(() => Source().GetAsync(Rid, null, Ct));
    }

    [Fact]
    public async Task Another_http_failure_names_the_status()
    {
        _http.Serve(AssetUrl + ".sha256", HttpStatusCode.ServiceUnavailable);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Source().GetAsync(Rid, null, Ct));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
        Assert.Contains("503", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cache_hit_makes_no_http_call()
    {
        byte[] binary = Binary();
        string dir = Path.Combine(CacheDirectory, Version, Rid);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "ntilde-mux"), binary);
        File.WriteAllText(Path.Combine(dir, "ntilde-mux.sha256"), $"{Hex(binary)}  ntilde-mux-{Rid}\n");

        MuxDaemonAsset asset = await Source().GetAsync(Rid, null, Ct);

        Assert.Equal(binary, asset.Bytes);
        Assert.Equal(Hex(binary), asset.Sha256Hex);
        Assert.Equal(Path.Combine(dir, "ntilde-mux"), asset.Origin);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task A_corrupt_cache_is_downloaded_again()
    {
        byte[] binary = Binary();
        string dir = Path.Combine(CacheDirectory, Version, Rid);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "ntilde-mux"), binary.AsSpan(0, 1000).ToArray());
        File.WriteAllText(Path.Combine(dir, "ntilde-mux.sha256"), $"{Hex(binary)}  ntilde-mux-{Rid}\n");
        ServeRelease(binary);

        MuxDaemonAsset asset = await Source().GetAsync(Rid, null, Ct);

        Assert.Equal(binary, asset.Bytes);
        Assert.Equal(AssetUrl, asset.Origin);
        Assert.Equal(binary, File.ReadAllBytes(Path.Combine(dir, "ntilde-mux")));
    }

    [Fact]
    public async Task A_served_pair_that_disagrees_with_the_pin_throws_and_caches_nothing()
    {
        // The binary and its .sha256 agree with each other, as a swapped release's would; the app's pin does not.
        byte[] binary = Binary();
        ServeRelease(binary);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => PinnedSource(Hex(Binary(10))).GetAsync(Rid, null, Ct));

        Assert.Equal(PinMismatchMessage, ex.Message);
        Assert.False(File.Exists(Path.Combine(CacheDirectory, Version, Rid, "ntilde-mux")));
    }

    [Fact]
    public async Task A_served_pair_that_matches_the_pin_is_accepted_and_cached()
    {
        byte[] binary = Binary();
        ServeRelease(binary);

        MuxDaemonAsset asset = await PinnedSource(Hex(binary).ToUpperInvariant()).GetAsync(Rid, null, Ct);

        Assert.Equal(binary, asset.Bytes);
        Assert.Equal(Hex(binary), asset.Sha256Hex);
        Assert.Equal(binary, File.ReadAllBytes(Path.Combine(CacheDirectory, Version, Rid, "ntilde-mux")));
    }

    [Fact]
    public async Task A_pinned_download_whose_binary_differs_from_both_checksums_still_throws()
    {
        // The .sha256 equals the pin, but the binary is not what it names.
        byte[] binary = Binary();
        ServeRelease(Binary(10), $"{Hex(binary)}  ntilde-mux-{Rid}\n");

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => PinnedSource(Hex(binary)).GetAsync(Rid, null, Ct));

        Assert.StartsWith("checksum mismatch", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cache_hit_that_agrees_with_the_pin_makes_no_http_call()
    {
        byte[] binary = Binary();
        string dir = Path.Combine(CacheDirectory, Version, Rid);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "ntilde-mux"), binary);
        File.WriteAllText(Path.Combine(dir, "ntilde-mux.sha256"), $"{Hex(binary)}  ntilde-mux-{Rid}\n");

        MuxDaemonAsset asset = await PinnedSource(Hex(binary)).GetAsync(Rid, null, Ct);

        Assert.Equal(binary, asset.Bytes);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task A_self_consistent_cache_with_another_hash_than_the_pin_is_downloaded_again()
    {
        // A cached pair that verifies against itself but not against the pin (a stale or planted copy) is a miss.
        byte[] stale = Binary(5000);
        byte[] binary = Binary();
        string dir = Path.Combine(CacheDirectory, Version, Rid);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "ntilde-mux"), stale);
        File.WriteAllText(Path.Combine(dir, "ntilde-mux.sha256"), $"{Hex(stale)}  ntilde-mux-{Rid}\n");
        ServeRelease(binary);

        MuxDaemonAsset asset = await PinnedSource(Hex(binary)).GetAsync(Rid, null, Ct);

        Assert.Equal(binary, asset.Bytes);
        Assert.Equal(AssetUrl, asset.Origin);
        Assert.Equal(binary, File.ReadAllBytes(Path.Combine(dir, "ntilde-mux")));
    }

    [Fact]
    public async Task A_pin_for_another_rid_changes_nothing()
    {
        byte[] binary = Binary();
        ServeRelease(binary);
        var source = new GitHubReleaseMuxAssetSource(
            new HttpClient(_http), Version, CacheDirectory, new Dictionary<string, string> { ["osx-arm64"] = Hex(Binary(10)) });

        MuxDaemonAsset asset = await source.GetAsync(Rid, null, Ct);

        Assert.Equal(binary, asset.Bytes);
    }

    [Fact]
    public async Task No_pins_keep_todays_behaviour()
    {
        byte[] binary = Binary();
        ServeRelease(binary);
        var source = new GitHubReleaseMuxAssetSource(new HttpClient(_http), Version, CacheDirectory, new Dictionary<string, string>());

        MuxDaemonAsset asset = await source.GetAsync(Rid, null, Ct);

        Assert.Equal(binary, asset.Bytes);
    }

    [Fact]
    public async Task The_cache_is_per_version_and_rid()
    {
        byte[] binary = Binary();
        ServeRelease(binary);
        await Source().GetAsync(Rid, null, Ct);
        _http.Requests.Clear();

        // Another version of the app has its own release: the 0.11.0 cache does not answer for it.
        await Assert.ThrowsAsync<MuxReleaseNotFoundException>(() => Source("0.11.1").GetAsync(Rid, null, Ct));
        Assert.NotEmpty(_http.Requests);
    }

    [Fact]
    public async Task A_declared_length_over_the_limit_is_refused_before_reading()
    {
        _http.Serve(AssetUrl + ".sha256", Encoding.ASCII.GetBytes($"{Hex(Binary())}  ntilde-mux-{Rid}\n"));
        _http.Serve(AssetUrl, () =>
        {
            var content = new ByteArrayContent(new byte[16]);
            content.Headers.ContentLength = MuxDaemonAsset.MaxBytes + 1;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Source().GetAsync(Rid, null, Ct));

        Assert.Contains("larger than", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_body_over_the_limit_is_refused_while_reading()
    {
        byte[] binary = Binary(5000);
        ServeRelease(binary);
        var source = new GitHubReleaseMuxAssetSource(new HttpClient(_http), Version, CacheDirectory) { MaxAssetBytes = 4096 };

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => source.GetAsync(Rid, null, Ct));

        Assert.Contains("larger than", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stalled_download_times_out()
    {
        _http.Stall = true;
        var source = new GitHubReleaseMuxAssetSource(new HttpClient(_http), Version, CacheDirectory) { DownloadTimeout = TimeSpan.FromMilliseconds(200) };

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => source.GetAsync(Rid, null, Ct));

        Assert.Contains(AssetUrl, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_is_not_a_timeout()
    {
        _http.Stall = true;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        Task<MuxDaemonAsset> get = Source().GetAsync(Rid, null, cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => get);
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("linux/x64")]
    [InlineData("")]
    public async Task A_rid_that_is_not_a_plain_name_is_rejected(string rid)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Source().GetAsync(rid, null, Ct));
        Assert.Empty(_http.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0.11.0 beta")]
    public async Task A_version_that_cannot_have_a_release_says_so_without_asking(string version)
    {
        var ex = await Assert.ThrowsAsync<MuxReleaseNotFoundException>(() => Source(version).GetAsync(Rid, null, Ct));

        Assert.Equal(
            $"No ntilde-mux release for {(version.Length == 0 ? "this build" : version)} \u2014 choose a file or copy the install command",
            ex.Message);
        Assert.Empty(_http.Requests);
    }

    [Theory]
    [InlineData("linux-x64")]
    [InlineData("linux-arm64")]
    [InlineData("osx-arm64")]
    public async Task A_local_file_for_the_host_s_platform_is_read_and_hashed(string rid)
    {
        byte[] binary = MuxDaemonRidTests.ExecutableFor(rid, 200_000);
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "picked-ntilde-mux");
        File.WriteAllBytes(path, binary);

        MuxDaemonAsset asset = await new LocalFileMuxAssetSource(path).GetAsync(rid, null, Ct);

        Assert.Equal(binary, asset.Bytes);
        Assert.Equal(Hex(binary), asset.Sha256Hex);
    }

    [Theory]
    [InlineData("linux-arm64", "linux-x64", "ntilde-mux-linux-arm64 is a linux-arm64 executable; the host needs linux-x64")]
    [InlineData("osx-arm64", "linux-arm64", "ntilde-mux-osx-arm64 is an osx-arm64 executable; the host needs linux-arm64")]
    [InlineData("linux-x64", "osx-arm64", "ntilde-mux-linux-x64 is a linux-x64 executable; the host needs osx-arm64")]
    public async Task A_local_file_for_another_platform_is_refused(string fileRid, string hostRid, string message)
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "ntilde-mux-" + fileRid);
        File.WriteAllBytes(path, MuxDaemonRidTests.ExecutableFor(fileRid));

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => new LocalFileMuxAssetSource(path).GetAsync(hostRid, null, Ct));

        Assert.Equal(message, ex.Message);
    }

    [Fact]
    public async Task A_local_file_that_is_no_executable_is_refused()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "notes.txt");
        File.WriteAllText(path, "#!/bin/sh\necho not ntilde-mux\n");

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => new LocalFileMuxAssetSource(path).GetAsync(Rid, null, Ct));

        Assert.Equal("notes.txt is not a Linux or macOS executable; the host needs linux-x64", ex.Message);
    }

    [Fact]
    public async Task A_local_file_is_read_and_hashed()
    {
        byte[] binary = MuxDaemonRidTests.ExecutableFor(Rid, 200_000);
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "picked-ntilde-mux");
        File.WriteAllBytes(path, binary);
        var progress = new RecordingProgress();

        MuxDaemonAsset asset = await new LocalFileMuxAssetSource(path).GetAsync(Rid, progress, Ct);

        Assert.Equal(binary, asset.Bytes);
        Assert.Equal(Hex(binary), asset.Sha256Hex);
        Assert.Equal(Path.GetFullPath(path), asset.Origin);
        Assert.Equal(binary.Length, progress.Reports[^1]);
    }

    [Fact]
    public async Task A_missing_local_file_says_which()
    {
        string path = Path.Combine(_root, "nope");

        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => new LocalFileMuxAssetSource(path).GetAsync(Rid, null, Ct));

        Assert.Contains(path, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_default_http_client_follows_redirects()
    {
        // GitHub answers a release download with a redirect to its CDN.
        using HttpClient client = GitHubReleaseMuxAssetSource.CreateHttpClient();

        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
        Assert.Contains(client.DefaultRequestHeaders.UserAgent, p => p.Product?.Name == "ntilde");
    }

    /// <summary>A synchronous <see cref="IProgress{T}"/>: <see cref="Progress{T}"/> posts, and would race the assertions.</summary>
    internal sealed class RecordingProgress : IProgress<long>
    {
        private readonly List<long> _reports = [];

        public IReadOnlyList<long> Reports
        {
            get { lock (_reports) return _reports.ToArray(); }
        }

        public void Report(long value)
        {
            lock (_reports) _reports.Add(value);
        }
    }

    /// <summary>Serves fixed URLs; any other URL is a 404, as GitHub answers for a release that does not exist.</summary>
    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

        public List<string> Requests { get; } = [];

        /// <summary>Every request waits until it is cancelled.</summary>
        public bool Stall { get; set; }

        public void Serve(string url, byte[] body) =>
            _routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

        public void Serve(string url, HttpStatusCode status) =>
            _routes[url] = () => new HttpResponseMessage(status) { Content = new ByteArrayContent([]) };

        public void Serve(string url, Func<HttpResponseMessage> response) => _routes[url] = response;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.AbsoluteUri;
            lock (Requests) Requests.Add(url);
            if (Stall)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return _routes.TryGetValue(url, out var response)
                ? response()
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("Not Found") };
        }
    }
}
