using System.Text;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;

namespace Ntilde.Mux.Tests.Client;

public sealed class ByteInputTransportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Negotiated_input_preserves_every_octet_and_copies_caller_memory()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Assert.True(client.SupportsByteInput);
        Guid id = await MuxTestHost.SpawnAsync(client);
        host.Fake(id).SupportsByteInput = true;
        using MuxClientSession session = client.OpenSession(id);
        byte[] input = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
        byte[] expected = input.ToArray();
        Assert.True(session.TrySendInputBytes(input));
        Array.Fill(input, (byte)0);
        await TestWait.UntilAsync(() => host.Fake(id).SentInputBytes.Count == 1, "all binary octets arrive at the child transport");
        Assert.Equal(expected, Assert.Single(host.Fake(id).SentInputBytes));
        Assert.Empty(host.Fake(id).SentInput);
    }

    [Fact]
    public async Task Mixed_text_and_binary_frames_share_one_ordered_byte_budget_including_a_blocked_write()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        ScriptedTerminalSession child = host.Fake(id);
        child.SupportsByteInput = true;
        using var release = new ManualResetEventSlim();
        child.SendInputGate = release;
        using MuxClientSession session = client.OpenSession(id);
        byte[] mouse = [27, 91, 77, 32, 128, 255];
        byte[] first = Encoding.UTF8.GetBytes("prefix€");
        try
        {
            session.SendInput("prefix€");
            Assert.True(child.SendInputEntered.Wait(TimeSpan.FromSeconds(30), Ct));
            Assert.True(session.TrySendInputBytes(mouse));
            session.SendInput("tail");
            await client.PingAsync(Ct);
            Assert.Equal(first.Length + mouse.Length + 4, host.Mux(id).QueuedInputBytes);
            release.Set();
            await TestWait.UntilAsync(() => child.WireInput.Count == 3, "mixed input leaves the one writer in order");
            byte[][] actual = child.WireInput.ToArray();
            Assert.Equal(first, actual[0]); Assert.Equal(mouse, actual[1]); Assert.Equal(Encoding.UTF8.GetBytes("tail"), actual[2]);
            await TestWait.UntilAsync(() => host.Mux(id).QueuedInputBytes == 0, "byte charges are released after writes return");
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task Binary_admission_copies_before_return_and_keeps_the_inflight_write_charged()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        ScriptedTerminalSession child = host.Fake(id);
        child.SupportsByteInput = true;
        using var release = new ManualResetEventSlim();
        child.SendInputGate = release;
        HeadlessTerminalSession session = host.Mux(id);
        session.MaxQueuedInputBytesForTest = 12;
        byte[] input = [27, 91, 77, 32, 128, 255], expected = input.ToArray();
        try
        {
            Assert.True(session.TrySendInputBytes(input));
            Assert.True(child.SendInputEntered.Wait(TimeSpan.FromSeconds(30), Ct));
            Array.Fill(input, (byte)0);
            Assert.True(session.TrySendInputBytes(expected));
            Assert.Equal(12, session.QueuedInputBytes);
            Assert.False(session.TrySendInputBytes(new byte[] { 1 }));
            Assert.Equal(12, session.QueuedInputBytes);
            release.Set();
            await TestWait.UntilAsync(() => child.SentInputBytes.Count == 2, "only admitted owned copies arrive");
            Assert.All(child.SentInputBytes, actual => Assert.Equal(expected, actual));
            await TestWait.UntilAsync(() => session.QueuedInputBytes == 0, "both in-flight and queued charges are released");
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task A_text_only_child_accepts_valid_UTF8_but_cannot_replace_invalid_octets()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        HeadlessTerminalSession session = host.Mux(id);
        Assert.False(session.TrySendInputBytes(new byte[] { 128, 255 }));
        Assert.Equal(0, session.QueuedInputBytes);
        session.MaxQueuedInputBytesForTest = 2; // one UTF-16 character, preserving the existing text budget
        Assert.True(session.TrySendInputBytes(Encoding.UTF8.GetBytes("€")));
        await TestWait.UntilAsync(() => host.Fake(id).SentInput.Count == 1, "valid legacy text retains its original memory charge");
        Assert.Equal("€", Assert.Single(host.Fake(id).SentInput));
        Assert.Empty(host.Fake(id).SentInputBytes);
    }

    [Fact]
    public async Task Rejected_text_only_input_does_not_allocate_the_over_budget_decoded_string()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        HeadlessTerminalSession session = host.Mux(id);
        session.MaxQueuedInputBytesForTest = 2;
        byte[] input = new byte[1024 * 1024]; Array.Fill(input, (byte)'x');
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.False(session.TrySendInputBytes(input));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        // The old decode-first route allocates a 2 MiB string only to reject it.
        // Allow ample fixed overhead while forbidding that payload-sized allocation.
        Assert.True(allocated < 64 * 1024, $"Rejected input allocated {allocated} bytes.");
        Assert.Equal(0, session.QueuedInputBytes);
        Assert.Empty(host.Fake(id).SentInput);
    }

    [Fact]
    public async Task A_stalled_link_returns_false_for_the_binary_frame_that_exceeds_its_overflow_cap()
    {
        using var daemon = FakeMuxServerEnd.Create(FullSendQueue.PipeCapacityBytes);
        Task<MuxClient> connect = MuxClient.ConnectAsync(daemon.ClientEnd, new MuxClientOptions { MaxOverflowBytes = 64 * 1024 }, Ct);
        MuxRequest hello = await daemon.ReadRequestAsync();
        daemon.Reply(hello.Id, new WelcomeResult { Version = 1, SupportsByteInput = true }, MuxJsonContext.Default.WelcomeResult);
        using MuxClient client = await connect;
        using MuxClientSession session = client.OpenSession(Guid.NewGuid());
        await FullSendQueue.FillAsync(client);
        Assert.True(await FullSendQueue.ReturnsPromptlyAsync(() =>
        {
            Assert.True(session.TrySendInputBytes(new byte[32 * 1024]));
            return !session.TrySendInputBytes(new byte[64 * 1024]);
        }));
        Assert.False(client.IsConnected);
        Assert.Equal("send overflow", client.DisconnectReason);
    }

    [Fact]
    public async Task A_disconnected_client_cannot_claim_binary_transport_acceptance()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        using MuxClientSession session = client.OpenSession(id);
        Assert.True(session.SupportsByteInput);
        client.Dispose();
        Assert.False(session.TrySendInputBytes(new byte[] { 128, 255 }));
        Assert.Empty(host.Fake(id).WireInput);
    }

    [Fact]
    public async Task An_old_daemon_without_the_extension_receives_only_the_existing_text_frame()
    {
        using var fake = FakeMuxServerEnd.Create();
        Task<MuxClient> connect = MuxClient.ConnectAsync(fake.ClientEnd, new MuxClientOptions(), Ct);
        await fake.AcceptHelloAsync();
        using MuxClient client = await connect;
        Assert.False(client.SupportsByteInput);
        Guid id = Guid.NewGuid();
        using MuxClientSession session = client.OpenSession(id);
        Assert.False(session.TrySendInputBytes(new byte[] { 128, 255 }));
        session.SendInput("legacy €");
        using MuxInboundFrame frame = await fake.Raw.ReadAsync() ?? throw new EndOfStreamException();
        Assert.Equal(MuxFrameKind.Input, frame.Kind);
        Assert.True(MuxFrames.TryParseInput(frame.Payload, out Guid received, out ReadOnlySpan<byte> payload));
        Assert.Equal(id, received); Assert.Equal(Encoding.UTF8.GetBytes("legacy €"), payload.ToArray());
    }

    [Fact]
    public async Task An_old_client_keeps_its_original_text_contract_and_welcome_shape()
    {
        using var host = new MuxTestHost();
        MuxClient setup = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(setup);
        host.Fake(id).SupportsByteInput = true;
        using RawMuxConnection old = host.ConnectRaw();
        WelcomeResult welcome = await old.HelloAsync();
        Assert.False(welcome.SupportsByteInput);
        old.Send(MuxFrames.Input(id, Encoding.UTF8.GetBytes("legacy €")));
        await TestWait.UntilAsync(() => host.Fake(id).SentInput.Count == 1, "unnegotiated input stays on the text path");
        Assert.Equal("legacy €", Assert.Single(host.Fake(id).SentInput));
        Assert.Empty(host.Fake(id).SentInputBytes);
    }

    [Fact]
    public async Task A_read_only_attachment_cannot_use_binary_input_to_bypass_the_server_guard()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        host.Fake(id).SupportsByteInput = true;
        using MuxClientSession session = client.OpenSession(id);
        await session.AttachAsync(MuxAttachMode.ReadOnly, 100, MuxTestHost.DefaultPresentation, Ct);
        Assert.True(session.TrySendInputBytes(new byte[] { 128, 255 })); // transport acceptance is not child acknowledgement
        await client.PingAsync(Ct);
        Assert.Empty(host.Fake(id).SentInputBytes);
        Assert.Empty(host.Fake(id).WireInput);
    }
}
