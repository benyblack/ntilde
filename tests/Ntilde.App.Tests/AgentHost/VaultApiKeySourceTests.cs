using System;
using Ntilde.AgentHost;
using Ntilde.Shell;
using Ntilde.Shell.Secrets;

namespace Ntilde.AppTests.AgentHost;

/// <summary>
/// F3: <see cref="VaultApiKeySource"/> re-reads the vault at most every 30 s, and immediately
/// after <see cref="VaultApiKeySource.Invalidate"/> (called right after a key is saved).
/// </summary>
public class VaultApiKeySourceTests
{
    private sealed class FakeClock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan by) => Now += by;
        public Func<DateTimeOffset> Provider => () => Now;
    }

    [Fact]
    public void First_call_reads_the_vault()
    {
        var store = new InMemorySecretStore();
        var vault = new VaultService(store);
        vault.SetInferenceApiKey("key-1");
        var clock = new FakeClock();
        var source = new VaultApiKeySource(vault, clock.Provider);

        Assert.Equal("key-1", source.TryGetKey());
    }

    [Fact]
    public void A_store_change_within_the_ttl_is_not_seen()
    {
        var store = new InMemorySecretStore();
        var vault = new VaultService(store);
        vault.SetInferenceApiKey("key-1");
        var clock = new FakeClock();
        var source = new VaultApiKeySource(vault, clock.Provider);
        Assert.Equal("key-1", source.TryGetKey());

        vault.SetInferenceApiKey("key-2");
        clock.Advance(TimeSpan.FromSeconds(29));

        Assert.Equal("key-1", source.TryGetKey());
    }

    [Fact]
    public void The_cache_expires_after_the_ttl()
    {
        var store = new InMemorySecretStore();
        var vault = new VaultService(store);
        vault.SetInferenceApiKey("key-1");
        var clock = new FakeClock();
        var source = new VaultApiKeySource(vault, clock.Provider);
        Assert.Equal("key-1", source.TryGetKey());

        vault.SetInferenceApiKey("key-2");
        clock.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal("key-2", source.TryGetKey());
    }

    [Fact]
    public void Invalidate_makes_a_store_change_visible_immediately()
    {
        var store = new InMemorySecretStore();
        var vault = new VaultService(store);
        vault.SetInferenceApiKey("key-1");
        var clock = new FakeClock();
        var source = new VaultApiKeySource(vault, clock.Provider);
        Assert.Equal("key-1", source.TryGetKey());

        vault.SetInferenceApiKey("key-2");
        clock.Advance(TimeSpan.FromSeconds(1)); // well within the TTL
        source.Invalidate();

        Assert.Equal("key-2", source.TryGetKey());
    }
}
