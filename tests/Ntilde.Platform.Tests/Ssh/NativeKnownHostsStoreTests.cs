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
            writer.TrustHost("seed", 22, "ssh-ed25519", "SHA256:seed");

            int failures = 0;
            int reads = 0;
            using var stop = new CancellationTokenSource();
            using var readerStarted = new ManualResetEventSlim();
            // Reads the file directly, bypassing the store's lock, like rusty_ssh does.
            Task readerTask = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    string json;
                    try
                    {
                        json = File.ReadAllText(path);
                    }
                    catch (IOException)
                    {
                        continue; // sharing violation during a rename; retry
                    }
                    catch (UnauthorizedAccessException)
                    {
                        continue;
                    }

                    Interlocked.Increment(ref reads);
                    readerStarted.Set();
                    try
                    {
                        var entries = System.Text.Json.JsonSerializer.Deserialize(
                            json, Ntilde.Platform.Ssh.Storage.SshJsonContext.Default.ListKnownHostEntry);
                        if (entries == null || !entries.Any(e => e.Host == "seed"))
                        {
                            Interlocked.Increment(ref failures);
                        }
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        Interlocked.Increment(ref failures);
                    }

                    // Yield between reads: a Windows rename can only retry so long against a reader
                    // that holds the file open back to back, and this is not a stress test of that.
                    Thread.Sleep(1);
                }
            }, TestContext.Current.CancellationToken);

            // The reader must be running before the first write, or a fast runner finishes
            // every write before it reads once and the test proves nothing.
            Assert.True(
                readerStarted.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken),
                "The unlocked reader never completed a read within 10 s, so it could not overlap the writes.");

            // Write at least 300 entries and keep going until the reader has read at least 20 times
            // during the writes, capped at 5000 writes or 5 s.
            int readsAtStart = Volatile.Read(ref reads);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int writes = 0;
            while (writes < 5000
                && clock.Elapsed < TimeSpan.FromSeconds(5)
                && (writes < 300 || Volatile.Read(ref reads) - readsAtStart < 20))
            {
                writer.TrustHost($"h{writes}", 22, "ssh-ed25519", $"SHA256:{writes}");
                writes++;
            }

            int readsDuringWrites = Volatile.Read(ref reads) - readsAtStart;

            stop.Cancel();
            readerTask.Wait(TestContext.Current.CancellationToken);

            Assert.True(readsDuringWrites >= 20, $"Only {readsDuringWrites} reads overlapped {writes} writes.");
            Assert.Equal(0, failures);
            Assert.Empty(Directory.GetFiles(tempRoot, "*.tmp"));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void A_literal_json_null_store_is_kept_aside_as_corrupt()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string path = Path.Combine(tempRoot, "native_known_hosts.json");
            File.WriteAllText(path, "null");
            var store = new NativeKnownHostsStore(path);

            store.TrustHost("h", 22, "ssh-ed25519", "SHA256:x");

            string[] aside = Directory.GetFiles(tempRoot, "native_known_hosts.json.corrupt-*");
            Assert.Single(aside);
            Assert.Equal("null", File.ReadAllText(aside[0]));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void An_unreadable_store_is_never_overwritten()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "FileShare.None read-blocking is Windows semantics.");
        string tempRoot = CreateTempDirectory();
        try
        {
            string path = Path.Combine(tempRoot, "native_known_hosts.json");
            new NativeKnownHostsStore(path).TrustHost("keep", 22, "ssh-ed25519", "SHA256:keep");
            byte[] original = File.ReadAllBytes(path);
            var store = new NativeKnownHostsStore(path);

            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.Throws<IOException>(() => store.TrustHost("h", 22, "ssh-ed25519", "SHA256:x"));
                Assert.Equal(NativeKnownHostMatch.Unknown, store.CheckHost("keep", 22, "ssh-ed25519", "SHA256:keep"));
            }

            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Equal(NativeKnownHostMatch.Trusted, store.CheckHost("keep", 22, "ssh-ed25519", "SHA256:keep"));
            Assert.Empty(Directory.GetFiles(tempRoot, "*.tmp"));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void A_corrupt_store_that_cannot_be_moved_aside_is_never_overwritten()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "A read-sharing handle blocking rename is Windows semantics.");
        string tempRoot = CreateTempDirectory();
        try
        {
            string path = Path.Combine(tempRoot, "native_known_hosts.json");
            File.WriteAllText(path, "{not json");
            var store = new NativeKnownHostsStore(path);

            // Readable, but a handle without FileShare.Delete blocks the rename.
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.Throws<IOException>(() => store.TrustHost("h", 22, "ssh-ed25519", "SHA256:x"));
            }

            Assert.Equal("{not json", File.ReadAllText(path));
            Assert.Empty(Directory.GetFiles(tempRoot, "*.corrupt-*"));
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
