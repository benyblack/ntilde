using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.VT;
using Ntilde.VT.Tests.StateTransfer;

namespace Ntilde.Mux.Tests.Client;

/// <summary>Phase 5 Task 11: the public calls a caller that never attached uses (the agent host's windowless sessions).</summary>
public sealed class MuxClientReadScreenTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Lines(int count) =>
        string.Concat(Enumerable.Range(0, count).Select(i => $"\x1b[1;3{i % 8}mline\x1b[0m {i}\r\n"));

    private static (TerminalBuffer Buffer, AnsiParser Parser) Restore(TerminalStateSnapshot snapshot)
    {
        var buffer = new TerminalBuffer(snapshot.Cols, snapshot.Rows);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { ImageDecoder = null, AllowNativeKittyGraphics = false };
        TerminalStateTransfer.Restore(buffer, parser, snapshot);
        return (buffer, parser);
    }

    [Fact]
    public async Task ReadScreen_returns_the_state_an_attach_sees_at_the_same_point()
    {
        using var host = new MuxTestHost();
        MuxClient viewer = await host.ConnectClientAsync();
        MuxClient agent = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(viewer);
        host.Fake(id).Emit(Lines(40) + "prompt> ");
        host.Fake(id).Emit(new byte[] { 0xE2, 0x82 }); // half a euro sign: the decoder tail travels too
        await host.Mux(id).FlushAsync(); // an attach is a control item and would otherwise overtake the queued output
        ClientPaneModel pane = await MuxTestHost.AttachPaneAsync(viewer, id, maxScrollbackRows: 100);
        await host.SettleAsync(id, viewer);

        MuxScreenRead? read = await agent.ReadScreenAsync(id, 100, Ct);

        Assert.NotNull(read);
        TerminalStateSnapshot? attached = pane.LastSnapshot;
        Assert.NotNull(attached);
        Assert.True(read.Snapshot.ScrollbackRowCount > 0, "the read carries scrollback");
        Assert.Equal(new byte[] { 0xE2, 0x82 }, read.Snapshot.DecoderTail);
        Assert.Equal(attached.StreamSeq, read.Snapshot.StreamSeq);
        Assert.Equal(attached.DecoderTail, read.Snapshot.DecoderTail);
        (TerminalBuffer expectedBuffer, AnsiParser expectedParser) = Restore(attached);
        (TerminalBuffer actualBuffer, AnsiParser actualParser) = Restore(read.Snapshot);
        TerminalStateAssert.AssertEquivalent("readScreen vs attach", expectedBuffer, expectedParser, actualBuffer, actualParser);
        Assert.Equal((true, 1, (int?)1, "test"), (read.Status.Running, read.Status.AttachedClients, read.Status.InteractiveClients, read.Status.Title));
        Assert.NotNull(read.Status.LastOutputUnixMs);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task A_daemon_that_does_not_know_readScreen_reads_as_unsupported(int negotiated)
    {
        using var fake = FakeMuxServerEnd.Create();
        Task<MuxClient> connect = MuxClient.ConnectAsync(fake.ClientEnd, new MuxClientOptions(), Ct);
        await fake.AcceptHelloAsync(negotiated);
        using MuxClient client = await connect;
        Guid id = Guid.NewGuid();

        Task<MuxScreenRead?> read = client.ReadScreenAsync(id, 50, Ct);
        MuxRequest request = await fake.ReadRequestAsync();
        fake.Raw.Send(MuxFrames.Response(new MuxResponse
        {
            Id = request.Id,
            Error = new MuxError { Code = MuxErrorCodes.ProtocolError, Message = "Unknown method 'readScreen'." },
        }));

        Assert.Null(await read);
        Assert.Equal(MuxMethods.ReadScreen, request.Method);
        ReadScreenParams p = MuxFrames.ParseParams(request.Params, MuxJsonContext.Default.ReadScreenParams);
        Assert.Equal((id, 50), (p.SessionId, p.MaxScrollbackRows));
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task Any_other_refusal_is_an_error_not_unsupported()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();

        var ex = await Assert.ThrowsAsync<MuxProtocolException>(() => client.ReadScreenAsync(Guid.NewGuid(), 0, Ct));

        Assert.Equal(MuxErrorCodes.UnknownSession, ex.Code);
        await client.PingAsync(Ct);
    }

    [Fact]
    public async Task The_client_applies_its_attach_limits_to_a_read_and_keeps_the_connection()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync(new MuxClientOptions { AttachLimits = new MuxAttachLimits { MaxScrollbackRows = 10 } });
        Guid id = await MuxTestHost.SpawnAsync(client);
        host.Fake(id).Emit(Lines(60));
        await host.Mux(id).FlushAsync();

        var ex = await Assert.ThrowsAsync<MuxProtocolException>(() => client.ReadScreenAsync(id, 100, Ct));

        Assert.Equal(MuxErrorCodes.SnapshotTooLarge, ex.Code);
        Assert.Contains("scrollback", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(await client.ReadScreenAsync(id, 10, Ct)); // within the limit: read, on the same connection
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task SendInputTo_reaches_a_session_this_connection_never_attached()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);

        client.SendInputTo(id, "echo hi\r");

        await TestWait.UntilAsync(() => host.Fake(id).SentInput.Contains("echo hi\r"), "the input reached the child");
        Assert.Equal(new[] { "echo hi\r" }, host.Fake(id).SentInput.ToArray());
        Assert.Equal(0, host.Mux(id).AttachedClients);
    }

    [Fact]
    public async Task GetSessionInfo_needs_no_open_session()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        host.Fake(id).HasActiveChildProcesses = true;

        SessionInfoResult info = await client.GetSessionInfoAsync(id, Ct);

        Assert.Equal((true, (int?)null, true, "test", (int?)0), (info.Running, info.ExitCode, info.HasActiveChildProcesses, info.Title, info.AttachedClients));
        var ex = await Assert.ThrowsAsync<MuxProtocolException>(() => client.GetSessionInfoAsync(Guid.NewGuid(), Ct));
        Assert.Equal(MuxErrorCodes.UnknownSession, ex.Code);
    }
}
