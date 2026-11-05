using System.Text.Json;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.VT;

namespace Ntilde.Mux.Tests.Client;

public sealed class SixelPolicyClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task Unsupported_client_ceiling_is_rejected_before_hello(int policy)
    {
        using var fake = FakeMuxServerEnd.Create();
        await Assert.ThrowsAsync<MuxProtocolException>(() => MuxClient.ConnectAsync(fake.ClientEnd,
            new MuxClientOptions { MaxSixelRasterPolicy = policy }, Ct));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task Unsupported_welcome_cannot_upgrade_this_client(int policy)
    {
        using var fake = FakeMuxServerEnd.Create();
        Task<MuxClient> connect = MuxClient.ConnectAsync(fake.ClientEnd, cancellationToken: Ct);
        MuxRequest hello = await fake.ReadRequestAsync();
        fake.Reply(hello.Id, new WelcomeResult { Version = 2, MaxSixelRasterPolicy = policy }, MuxJsonContext.Default.WelcomeResult);
        var ex = await Assert.ThrowsAsync<MuxProtocolException>(() => connect);
        Assert.Equal(MuxErrorCodes.VersionMismatch, ex.Code);
    }

    [Fact]
    public async Task Old_daemon_welcome_selects_legacy_and_explicit_native_spawn_is_not_sent()
    {
        using var fake = FakeMuxServerEnd.Create();
        Task<MuxClient> connect = MuxClient.ConnectAsync(fake.ClientEnd, cancellationToken: Ct);
        await fake.AcceptHelloAsync(2);
        using var client = await connect;
        Assert.Equal(0, client.MaxSixelRasterPolicy);
        await Assert.ThrowsAsync<MuxProtocolException>(() => client.SpawnAsync(new SpawnParams { Command = "scripted", RequiredSixelRasterPolicy = 1 }, Ct));
        await Assert.ThrowsAsync<MuxProtocolException>(() => client.ReadScreenAsync(Guid.NewGuid(), 0, 1, Ct));
        Assert.Throws<MuxProtocolException>(() => client.OpenSession(Guid.NewGuid(), requiredSixelRasterPolicy: 1));
        using var session = client.OpenSession(Guid.NewGuid());
        Assert.Equal(0, session.RequiredSixelRasterPolicy);
        Assert.Equal(0, client.PendingRequestCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Snapshot_policy_is_checked_before_attach_or_readScreen_delivery(bool readScreen)
    {
        using var fake = FakeMuxServerEnd.Create();
        Task<MuxClient> connect = MuxClient.ConnectAsync(fake.ClientEnd, cancellationToken: Ct);
        await fake.AcceptHelloAsync(2);
        using var client = await connect;
        Guid id = Guid.NewGuid();
        using var session = client.OpenSession(id);
        int delivered = 0;
        session.SnapshotReceived += _ => delivered++;
        Task pending = readScreen ? client.ReadScreenAsync(id, 0, Ct) : session.AttachAsync(0, MuxTestHost.DefaultPresentation, Ct);
        MuxRequest request = await fake.ReadRequestAsync();
        TerminalStateSnapshot state = TerminalStateSerializer.FromBytes(FakeMuxServerEnd.SnapshotJson());
        state.RequiredSixelRasterPolicy = 1;
        byte[] bytes = TerminalStateSerializer.ToBytes(state);
        if (readScreen)
            fake.Reply(request.Id, new ReadScreenResult { Snapshot = bytes }, MuxJsonContext.Default.ReadScreenResult);
        else fake.Raw.Send(MuxFrames.Snapshot(request.Id, id, 0, bytes));
        await Assert.ThrowsAsync<MuxProtocolException>(() => pending);
        Assert.Equal(0, delivered);
    }

    [Fact]
    public void Absent_or_zero_fields_preserve_exact_legacy_handshake_and_result_bytes()
    {
        Assert.Equal("{\"minVersion\":1,\"maxVersion\":2,\"clientKind\":\"test\"}",
            JsonSerializer.Serialize(new HelloParams { MinVersion = 1, MaxVersion = 2, ClientKind = "test" }, MuxJsonContext.Default.HelloParams));
        Assert.Equal("{\"version\":2,\"forceConPtyFiltering\":false}",
            JsonSerializer.Serialize(new WelcomeResult { Version = 2 }, MuxJsonContext.Default.WelcomeResult));
        var id = Guid.Parse("00000000-0000-0000-0000-000000000001");
        Assert.Equal("{\"sessionId\":\"00000000-0000-0000-0000-000000000001\"}",
            JsonSerializer.Serialize(new SpawnResult { SessionId = id }, MuxJsonContext.Default.SpawnResult));
        var zero = JsonSerializer.Deserialize("{\"version\":2,\"forceConPtyFiltering\":false,\"maxSixelRasterPolicy\":0}", MuxJsonContext.Default.WelcomeResult)!;
        Assert.Equal("{\"version\":2,\"forceConPtyFiltering\":false}", JsonSerializer.Serialize(zero, MuxJsonContext.Default.WelcomeResult));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"version\":2,\"maxSixelRasterPolicy\":1,\"maxSixelRasterPolicy\":0}", MuxJsonContext.Default.WelcomeResult));
    }
}
