using Ntilde.Shell;
using Ntilde.Shell.Secrets;

namespace Ntilde.Tests.Core;

public class VaultServiceInferenceKeyTests
{
    private sealed class UnavailableStore : ISecretStore
    {
        public bool IsAvailable => false;
        public string? Read(string key) => "must-not-be-read";
        public void Write(string key, string value) => throw new InvalidOperationException("must not write");
        public bool Delete(string key) => throw new InvalidOperationException("must not delete");
    }

    [Fact]
    public void Set_get_and_clear_round_trip_through_the_store()
    {
        var store = new InMemorySecretStore();
        var vault = new VaultService(store);

        Assert.False(vault.HasInferenceApiKey());
        Assert.Null(vault.GetInferenceApiKey());

        vault.SetInferenceApiKey("apikey_test_123");
        Assert.True(vault.HasInferenceApiKey());
        Assert.Equal("apikey_test_123", vault.GetInferenceApiKey());
        Assert.Equal("apikey_test_123", store.Read(VaultService.InferenceApiKeySecretKey));

        vault.SetInferenceApiKey(null);
        Assert.False(vault.HasInferenceApiKey());
        Assert.Null(store.Read(VaultService.InferenceApiKeySecretKey));
    }

    [Fact]
    public void Whitespace_is_trimmed_and_empty_clears()
    {
        var store = new InMemorySecretStore();
        var vault = new VaultService(store);

        vault.SetInferenceApiKey("  k  ");
        Assert.Equal("k", vault.GetInferenceApiKey());

        vault.SetInferenceApiKey("   ");
        Assert.Null(vault.GetInferenceApiKey());
    }

    [Fact]
    public void Unavailable_store_reads_null_and_writes_nothing()
    {
        var vault = new VaultService(new UnavailableStore());

        vault.SetInferenceApiKey("k");
        Assert.Null(vault.GetInferenceApiKey());
        Assert.False(vault.HasInferenceApiKey());
    }
}
