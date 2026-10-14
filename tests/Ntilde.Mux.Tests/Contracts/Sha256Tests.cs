using System.Security.Cryptography;
using System.Text;
using Ntilde.Mux.Contracts;

namespace Ntilde.Mux.Tests.Contracts;

/// <summary>
/// The managed SHA-256 behind the endpoint hash (Phase 4 spec §2 decision 1: ntilde-mux needs only
/// libc, and the BCL's SHA256 is OpenSSL on Linux). The FIPS 180-4 vectors pin it to the standard;
/// the comparisons with <see cref="SHA256.HashData(byte[])"/> pin it to what the endpoint names were
/// computed with before - a GUI daemon and ntilde-mux on one host must name the same root alike.
/// </summary>
public sealed class Sha256Tests
{
    [Theory]
    [InlineData("", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    [InlineData("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq", "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1")]
    public void The_FIPS_180_4_vectors(string message, string expectedHex)
    {
        Assert.Equal(expectedHex, Convert.ToHexStringLower(Sha256.Hash(Encoding.ASCII.GetBytes(message))));
    }

    [Fact]
    public void The_FIPS_180_4_million_a_vector()
    {
        byte[] message = new byte[1_000_000];
        message.AsSpan().Fill((byte)'a');
        Assert.Equal("cdc76e5c9914fb9281a1c7e284d73e67f1809a48a497200e046d39ccc7112cd0", Convert.ToHexStringLower(Sha256.Hash(message)));
    }

    /// <summary>
    /// Every length from 0 to 200 - so every padding case, including the block-boundary lengths 55,
    /// 56, 63, 64, 119 and 120, where the length field does or does not still fit - and a few larger
    /// buffers that span many blocks and end mid-block.
    /// </summary>
    public static TheoryData<int> Lengths()
    {
        var data = new TheoryData<int>();
        for (int length = 0; length <= 200; length++) data.Add(length);
        foreach (int length in new[] { 255, 256, 1000, 4096, 65_535, 65_536, 65_537, 1_000_003 }) data.Add(length);
        return data;
    }

    [Theory]
    [MemberData(nameof(Lengths))]
    public void Matches_the_BCL_for_every_length(int length)
    {
        byte[] data = new byte[length];
        new Random(length).NextBytes(data);

        Assert.Equal(SHA256.HashData(data), Sha256.Hash(data));
    }

    // A home root, a short one, a trailing separator, one long enough that a Unix endpoint falls
    // back to a hashed directory name, and one with non-ASCII text (the hash is over UTF-8).
    public static TheoryData<string> Roots()
    {
        string longTail = new('d', 120);
        return OperatingSystem.IsWindows()
            ? new TheoryData<string>
            {
                @"C:\Users\someone\AppData\Local\ntilde",
                @"D:\x",
                @"D:\x\",
                @"C:\" + longTail,
                @"C:\Users\Jürgen\AppData\Local\ntilde",
            }
            : new TheoryData<string>
            {
                "/home/someone/.local/share/ntilde",
                "/tmp/x",
                "/tmp/x/",
                "/tmp/" + longTail,
                "/home/jürgen/.local/share/ntilde",
            };
    }

    /// <summary><c>MuxDiscovery.RootHash</c> as it was written before, over <see cref="SHA256.HashData(byte[])"/>.</summary>
    private static string RootHashTheOldWay(string root)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (OperatingSystem.IsWindows()) full = full.ToUpperInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(full));
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    [Theory]
    [MemberData(nameof(Roots))]
    public void The_root_hash_is_unchanged(string root)
    {
        Assert.Equal(RootHashTheOldWay(root), MuxDiscovery.RootHash(root));
    }

    /// <summary>
    /// The endpoint itself, wherever the hash appears in it: the pipe name on Windows; on Unix the
    /// directory a too-long root falls back to (a root that fits uses <c>&lt;root&gt;/mux/mux.sock</c>,
    /// with no hash in it at all).
    /// </summary>
    [Theory]
    [MemberData(nameof(Roots))]
    public void The_default_endpoint_is_unchanged(string root)
    {
        string name = "ntilde-mux-" + MuxDiscovery.SanitizedUser() + "-" + RootHashTheOldWay(root);
        string endpoint = MuxDiscovery.GetDefaultEndpoint(root);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(name, endpoint);
            return;
        }

        string preferred = Path.Combine(root, MuxDiscovery.DirectoryName, MuxDiscovery.SocketFileName);
        if (endpoint == preferred) return;

        Assert.Equal(MuxDiscovery.SocketFileName, Path.GetFileName(endpoint));
        Assert.Equal(name, Path.GetFileName(Path.GetDirectoryName(endpoint)));
    }

    [Fact]
    public void A_root_too_long_for_sun_path_takes_the_hashed_name_on_Unix()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows endpoints are pipe names, always hashed.");

        string root = "/tmp/" + new string('d', 120);
        string expected = "ntilde-mux-" + MuxDiscovery.SanitizedUser() + "-" + RootHashTheOldWay(root);
        Assert.Equal(expected, Path.GetFileName(Path.GetDirectoryName(MuxDiscovery.GetDefaultEndpoint(root))));
    }
}
