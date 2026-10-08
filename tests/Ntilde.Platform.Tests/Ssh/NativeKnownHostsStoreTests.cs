using Ntilde.Platform.Ssh.Native;

namespace Ntilde.Platform.Tests.Ssh;

public sealed class NativeKnownHostsStoreTests
{
    [Fact]
    public void TrustHost_FirstTimePersistsEntryAndTrustedLookup()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string path = Path.Combine(tempRoot, "native_known_hosts.json");
            var store = new NativeKnownHostsStore(path);

            Assert.Equal(
                NativeKnownHostMatch.Unknown,
                store.CheckHost("example.internal", 22, "ssh-ed25519", " sha256:test-fingerprint "));

            store.TrustHost("example.internal", 22, "ssh-ed25519", " sha256:test-fingerprint ");

            Assert.Equal(
                NativeKnownHostMatch.Trusted,
                store.CheckHost("EXAMPLE.INTERNAL", 22, "ssh-ed25519", "SHA256:test-fingerprint"));

            string json = File.ReadAllText(path);
            Assert.Contains("example.internal", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("passphrase", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void CheckHost_WhenFingerprintChanges_ReturnsMismatch()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string path = Path.Combine(tempRoot, "native_known_hosts.json");
            var store = new NativeKnownHostsStore(path);
            store.TrustHost("example.internal", 22, "ssh-ed25519", "SHA256:trusted");

            Assert.Equal(
                NativeKnownHostMatch.Mismatch,
                store.CheckHost("example.internal", 22, "ssh-ed25519", "SHA256:changed"));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Formatter_NormalizesFingerprintDeterministically()
    {
        string normalized = HostKeyFingerprintFormatter.Normalize(" sha256:AbCdEf123= ");

        Assert.Equal("SHA256:AbCdEf123=", normalized);
    }

    [Fact]
    public void Concurrent_trusts_from_two_instances_keep_every_entry()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string path = Path.Combine(tempRoot, "native_known_hosts.json");
            var a = new NativeKnownHostsStore(path);
            var b = new NativeKnownHostsStore(path);

            Parallel.For(0, 64, i => (i % 2 == 0 ? a : b).TrustHost($"h{i}", 22, "ssh-ed25519", $"SHA256:{i}"));

            var fresh = new NativeKnownHostsStore(path);
            for (int i = 0; i < 64; i++)
            {
                Assert.Equal(NativeKnownHostMatch.Trusted, fresh.CheckHost($"h{i}", 22, "ssh-ed25519", $"SHA256:{i}"));
            }
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void A_corrupt_store_is_kept_aside_not_overwritten()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string path = Path.Combine(tempRoot, "native_known_hosts.json");
            File.WriteAllText(path, "{not json");
            var store = new NativeKnownHostsStore(path);

            store.TrustHost("h", 22, "ssh-ed25519", "SHA256:x");

            string[] aside = Directory.GetFiles(tempRoot, "native_known_hosts.json.corrupt-*");
            Assert.Single(aside);
            Assert.Equal("{not json", File.ReadAllText(aside[0]));
            Assert.Equal(NativeKnownHostMatch.Trusted, store.CheckHost("h", 22, "ssh-ed25519", "SHA256:x"));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Corrupt_files_are_capped_at_the_newest_five()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string path = Path.Combine(tempRoot, "native_known_hosts.json");
            for (int i = 0; i < 8; i++)
            {
                File.WriteAllText(path, "{not json");
                new NativeKnownHostsStore(path).CheckHost("h", 22, "ssh-ed25519", "SHA256:x");
                File.Delete(path);
            }
            File.WriteAllText(path, "{not json");
            new NativeKnownHostsStore(path).TrustHost("h", 22, "ssh-ed25519", "SHA256:x");

            Assert.Equal(5, Directory.GetFiles(tempRoot, "native_known_hosts.json.corrupt-*").Length);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void ForPath_returns_one_instance_per_normalised_path()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string path = Path.Combine(tempRoot, "native_known_hosts.json");
            string roundabout = Path.Combine(tempRoot, "sub", "..", "native_known_hosts.json");

            Assert.Same(NativeKnownHostsStore.ForPath(path), NativeKnownHostsStore.ForPath(roundabout));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void TrustHost_never_leaves_a_partial_file()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string path = Path.Combine(tempRoot, "native_known_hosts.json");
            var writer = new NativeKnownHostsStore(path);
            var reader = new NativeKnownHostsStore(path);
            writer.TrustHost("seed", 22, "ssh-ed25519", "SHA256:seed");

            int failures = 0;
            using var stop = new CancellationTokenSource();
            Task readerTask = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    // The seed entry is always present, so any torn read shows up as Unknown.
                    if (reader.CheckHost("seed", 22, "ssh-ed25519", "SHA256:seed") != NativeKnownHostMatch.Trusted)
                    {
                        Interlocked.Increment(ref failures);
                    }
                }
            });

            for (int i = 0; i < 200; i++)
            {
                writer.TrustHost($"h{i}", 22, "ssh-ed25519", $"SHA256:{i}");
            }

            stop.Cancel();
            readerTask.Wait();

            Assert.Equal(0, failures);
            Assert.Empty(Directory.GetFiles(tempRoot, "*.corrupt-*"));
            Assert.Empty(Directory.GetFiles(tempRoot, "*.tmp"));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ntilde_known_hosts_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
