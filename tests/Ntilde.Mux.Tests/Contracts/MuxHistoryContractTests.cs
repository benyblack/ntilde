using System.Text.Json;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;

namespace Ntilde.Mux.Tests.Contracts;

public sealed class MuxHistoryContractTests
{
    [Fact]
    public void AbsentPolicyKeepsLegacyWireShapesAndNullMeaning()
    {
        string spawn = JsonSerializer.Serialize(new SpawnParams { Command = "scripted" }, MuxJsonContext.Default.SpawnParams);
        string presentation = JsonSerializer.Serialize(MuxTestHost.DefaultPresentation, MuxJsonContext.Default.MuxPresentation);
        string welcome = JsonSerializer.Serialize(new WelcomeResult { Version = 1 }, MuxJsonContext.Default.WelcomeResult);
        Assert.DoesNotContain("maxHistory", spawn, StringComparison.Ordinal);
        Assert.DoesNotContain("maxHistory", presentation, StringComparison.Ordinal);
        Assert.DoesNotContain("historyLimits", welcome, StringComparison.Ordinal);
        Assert.Null(JsonSerializer.Deserialize(spawn, MuxJsonContext.Default.SpawnParams)!.MaxHistory);
        Assert.Null(JsonSerializer.Deserialize(presentation, MuxJsonContext.Default.MuxPresentation)!.MaxHistory);
        Assert.Null(JsonSerializer.Deserialize(welcome, MuxJsonContext.Default.WelcomeResult)!.HistoryLimits);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100_000)]
    public void ExplicitPolicyAndIndependentLimitsRoundTrip(int rows)
    {
        var spawn = new SpawnParams { Command = "scripted", MaxHistory = rows };
        var presentation = MuxTestHost.DefaultPresentation with { MaxHistory = rows };
        var limits = new MuxHistoryLimits { MaxRows = 100_000, MaxBytes = 128L * 1024 * 1024, MaxAttachRows = 20_000 };
        string spawnJson = JsonSerializer.Serialize(spawn, MuxJsonContext.Default.SpawnParams);
        Assert.Contains($"\"maxHistory\":{rows}", spawnJson, StringComparison.Ordinal);
        Assert.Equal(rows, JsonSerializer.Deserialize(spawnJson, MuxJsonContext.Default.SpawnParams)!.MaxHistory);
        Assert.Equal(presentation, JsonSerializer.Deserialize(JsonSerializer.Serialize(presentation, MuxJsonContext.Default.MuxPresentation), MuxJsonContext.Default.MuxPresentation));
        Assert.Equal(limits, JsonSerializer.Deserialize(JsonSerializer.Serialize(limits, MuxJsonContext.Default.MuxHistoryLimits), MuxJsonContext.Default.MuxHistoryLimits));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task OlderWelcomesStillConnectWithoutRetentionSupport(int version)
    {
        using var peer = FakeMuxServerEnd.Create();
        var connecting = MuxClient.ConnectAsync(peer.ClientEnd, cancellationToken: TestContext.Current.CancellationToken);
        await peer.AcceptHelloAsync(version);
        using var client = await connecting;
        Assert.Null(client.HistoryLimits);
    }

    [Theory]
    [InlineData("rows")]
    [InlineData("bytes")]
    [InlineData("attach")]
    public async Task MalformedAdvertisedLimitsAreRejected(string field)
    {
        using var peer = FakeMuxServerEnd.Create();
        var connecting = MuxClient.ConnectAsync(peer.ClientEnd, cancellationToken: TestContext.Current.CancellationToken);
        var hello = await peer.ReadRequestAsync();
        peer.Reply(hello.Id, new WelcomeResult
        {
            Version = 1,
            HistoryLimits = new MuxHistoryLimits { MaxRows = field == "rows" ? -1 : 100_000, MaxBytes = field == "bytes" ? 0 : 128L * 1024 * 1024, MaxAttachRows = field == "attach" ? -1 : 20_000 }
        }, MuxJsonContext.Default.WelcomeResult);
        var error = await Assert.ThrowsAsync<MuxProtocolException>(() => connecting);
        Assert.Equal(MuxErrorCodes.ProtocolError, error.Code);
    }
}
