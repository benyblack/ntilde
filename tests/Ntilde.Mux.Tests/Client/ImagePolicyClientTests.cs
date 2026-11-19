using System.Text.Json;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.VT;

namespace Ntilde.Mux.Tests.Client;

/// <summary>#838: the client side of the image policy: what it offers, what it accepts, and what it refuses.</summary>
public sealed class ImagePolicyClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(-1)]
    [InlineData(PersistentImagePolicy.EnabledVersion + 1)]
    public async Task An_image_ceiling_this_build_lacks_is_refused_before_the_hello(int policy)
    {
        var stream = new MemoryStream();
        await Assert.ThrowsAsync<MuxProtocolException>(() => MuxClient.ConnectAsync(stream, new MuxClientOptions { MaxImagePolicy = policy }, Ct));
        Assert.Empty(stream.ToArray());
    }

    [Fact]
    public async Task A_welcome_cannot_raise_the_image_policy_this_client_offered()
    {
        using var fake = FakeMuxServerEnd.Create();
        Task<MuxClient> connect = MuxClient.ConnectAsync(fake.ClientEnd, cancellationToken: Ct);
        MuxRequest hello = await fake.ReadRequestAsync();
        fake.Reply(hello.Id, new WelcomeResult { Version = 2, MaxImagePolicy = 1 }, MuxJsonContext.Default.WelcomeResult);
        Assert.Equal(MuxErrorCodes.VersionMismatch, (await Assert.ThrowsAsync<MuxProtocolException>(() => connect)).Code);
    }

    [Fact]
    public async Task An_older_daemon_leaves_every_session_SIXEL_only()
    {
        using var fake = FakeMuxServerEnd.Create();
        Task<MuxClient> connect = MuxClient.ConnectAsync(fake.ClientEnd, new MuxClientOptions { MaxImagePolicy = PersistentImagePolicy.EnabledVersion }, Ct);
        MuxRequest hello = await fake.ReadRequestAsync();
        Assert.Contains($"\"maxImagePolicy\":{PersistentImagePolicy.EnabledVersion}", hello.Params!.Value.GetRawText(), StringComparison.Ordinal);
        fake.Reply(hello.Id, new WelcomeResult { Version = 2 }, MuxJsonContext.Default.WelcomeResult);
        using var client = await connect;

        Assert.Equal(0, client.MaxImagePolicy);
        await Assert.ThrowsAsync<MuxProtocolException>(() => client.SpawnAsync(new SpawnParams { Command = "scripted", RequiredImagePolicy = 1 }, Ct));
        Assert.Throws<MuxProtocolException>(() => client.OpenSession(Guid.NewGuid(), requiredImagePolicy: 1));
        Assert.Equal(0, client.PendingRequestCount);
    }

    [Fact]
    public async Task A_daemon_from_before_the_multipart_form_gets_policy_1_sessions()
    {
        // Such a daemon offers policy 1. Sessions spawned against it name policy 1, under which this client's
        // parsers ignore iTerm2's multipart form as the daemon does, and a policy-2 attach is refused there.
        using var fake = FakeMuxServerEnd.Create();
        Task<MuxClient> connect = MuxClient.ConnectAsync(fake.ClientEnd, new MuxClientOptions { MaxImagePolicy = PersistentImagePolicy.EnabledVersion }, Ct);
        MuxRequest hello = await fake.ReadRequestAsync();
        fake.Reply(hello.Id, new WelcomeResult { Version = 2, MaxImagePolicy = PersistentImagePolicy.RetainedImagesVersion }, MuxJsonContext.Default.WelcomeResult);
        using var client = await connect;

        Assert.Equal(PersistentImagePolicy.RetainedImagesVersion, client.MaxImagePolicy);
        Assert.False(PersistentImagePolicy.AllowsITerm2Multipart(client.MaxImagePolicy));
        Assert.Throws<MuxProtocolException>(() => client.OpenSession(Guid.NewGuid(), requiredImagePolicy: PersistentImagePolicy.MultipartImagesVersion));
    }

    [Fact]
    public async Task A_snapshot_with_images_is_refused_for_a_SIXEL_only_session()
    {
        using var fake = FakeMuxServerEnd.Create();
        Task<MuxClient> connect = MuxClient.ConnectAsync(fake.ClientEnd, cancellationToken: Ct);
        await fake.AcceptHelloAsync(2);
        using var client = await connect;
        Guid id = Guid.NewGuid();
        using var session = client.OpenSession(id);
        int delivered = 0;
        session.SnapshotReceived += _ => delivered++;
        Task attach = session.AttachAsync(0, MuxTestHost.DefaultPresentation, Ct);
        MuxRequest request = await fake.ReadRequestAsync();
        TerminalStateSnapshot state = TerminalStateSerializer.FromBytes(FakeMuxServerEnd.SnapshotJson());
        state.ITermImages = [new() { Data = Ntilde.VT.Tests.TestImages.PngHeader(4, 4), PixelWidth = 4, PixelHeight = 4, Width = 1, Height = 1 }];
        fake.Raw.Send(MuxFrames.Snapshot(request.Id, id, 0, TerminalStateSerializer.ToBytes(state)));

        Assert.Equal(MuxErrorCodes.VersionMismatch, (await Assert.ThrowsAsync<MuxProtocolException>(() => attach)).Code);
        Assert.Equal(0, delivered);
    }

    [Fact]
    public async Task A_snapshot_with_animation_is_refused_for_a_session_that_predates_it_and_accepted_under_policy_three()
    {
        foreach ((int policy, bool accepted) in new[] { (PersistentImagePolicy.MultipartImagesVersion, false), (PersistentImagePolicy.AnimationImagesVersion, true), (PersistentImagePolicy.CompositionImagesVersion, true), (PersistentImagePolicy.PlaceholderImagesVersion, true) })
        {
            using var fake = FakeMuxServerEnd.Create();
            Task<MuxClient> connect = MuxClient.ConnectAsync(fake.ClientEnd, new MuxClientOptions { MaxImagePolicy = PersistentImagePolicy.EnabledVersion }, Ct);
            MuxRequest hello = await fake.ReadRequestAsync();
            fake.Reply(hello.Id, new WelcomeResult { Version = 2, MaxImagePolicy = PersistentImagePolicy.EnabledVersion }, MuxJsonContext.Default.WelcomeResult);
            using var client = await connect;
            Guid id = Guid.NewGuid();
            using var session = client.OpenSession(id, requiredImagePolicy: policy);
            int delivered = 0;
            session.SnapshotReceived += _ => delivered++;
            Task attach = session.AttachAsync(0, MuxTestHost.DefaultPresentation, Ct);
            MuxRequest request = await fake.ReadRequestAsync();
            TerminalStateSnapshot state = TerminalStateSerializer.FromBytes(FakeMuxServerEnd.SnapshotJson());
            state.KittyImages = [new() { Id = 1, Format = 32, RawWidth = 1, RawHeight = 1, Width = 1, Height = 1, Data = [1, 2, 3, 255],
                Animation = new() { State = 1, Frames = [new() { Gap = 100 }, new() { Gap = 100, Data = KittyFrameCodecProbe.OnePixel }] } }];
            fake.Raw.Send(MuxFrames.Snapshot(request.Id, id, 0, TerminalStateSerializer.ToBytes(state)));

            if (accepted)
            {
                await attach;
                Assert.Equal(1, delivered);
            }
            else
            {
                Assert.Equal(MuxErrorCodes.VersionMismatch, (await Assert.ThrowsAsync<MuxProtocolException>(() => attach)).Code);
                Assert.Equal(0, delivered);
            }
        }
    }

    [Fact]
    public async Task A_snapshot_with_virtual_placements_is_refused_for_a_session_that_predates_them_and_accepted_under_policy_five()
    {
        foreach ((int policy, bool accepted) in new[] { (PersistentImagePolicy.AnimationImagesVersion, false), (PersistentImagePolicy.CompositionImagesVersion, false), (PersistentImagePolicy.PlaceholderImagesVersion, true) })
        {
            using var fake = FakeMuxServerEnd.Create();
            Task<MuxClient> connect = MuxClient.ConnectAsync(fake.ClientEnd, new MuxClientOptions { MaxImagePolicy = PersistentImagePolicy.EnabledVersion }, Ct);
            MuxRequest hello = await fake.ReadRequestAsync();
            fake.Reply(hello.Id, new WelcomeResult { Version = 2, MaxImagePolicy = PersistentImagePolicy.EnabledVersion }, MuxJsonContext.Default.WelcomeResult);
            using var client = await connect;
            Guid id = Guid.NewGuid();
            using var session = client.OpenSession(id, requiredImagePolicy: policy);
            int delivered = 0;
            session.SnapshotReceived += _ => delivered++;
            Task attach = session.AttachAsync(0, MuxTestHost.DefaultPresentation, Ct);
            MuxRequest request = await fake.ReadRequestAsync();
            TerminalStateSnapshot state = TerminalStateSerializer.FromBytes(FakeMuxServerEnd.SnapshotJson());
            state.KittyImages = [new() { Id = 1, Format = 32, RawWidth = 1, RawHeight = 1, Width = 1, Height = 1, Data = [1, 2, 3, 255],
                VirtualPlacements = [new() { Placement = 7, Columns = 2, Rows = 2 }] }];
            fake.Raw.Send(MuxFrames.Snapshot(request.Id, id, 0, TerminalStateSerializer.ToBytes(state)));

            if (accepted)
            {
                await attach;
                Assert.Equal(1, delivered);
            }
            else
            {
                Assert.Equal(MuxErrorCodes.VersionMismatch, (await Assert.ThrowsAsync<MuxProtocolException>(() => attach)).Code);
                Assert.Equal(0, delivered);
            }
        }
    }

    [Fact]
    public async Task A_file_the_daemon_read_answers_only_the_command_in_the_output_frame_that_follows_it()
    {
        // #887: a client never opens a file. The daemon's FileTransfer frame precedes the output frame that names the file; while the client parses that output, the parser's reader
        // takes the first unclaimed entry for the path it was asked about, and an entry nobody claimed (or one for another offset) never reaches a later chunk.
        using var fake = FakeMuxServerEnd.Create();
        Task<MuxClient> connect = MuxClient.ConnectAsync(fake.ClientEnd, new MuxClientOptions { MaxImagePolicy = PersistentImagePolicy.EnabledVersion }, Ct);
        MuxRequest hello = await fake.ReadRequestAsync();
        fake.Reply(hello.Id, new WelcomeResult { Version = 2, MaxImagePolicy = PersistentImagePolicy.EnabledVersion }, MuxJsonContext.Default.WelcomeResult);
        using var client = await connect;
        Guid id = Guid.NewGuid();
        using var session = client.OpenSession(id, requiredImagePolicy: PersistentImagePolicy.FileTransportImagesVersion);
        var reads = new Dictionary<string, string>();
        session.OnOutputReceived += text =>
        {
            string Read(string path) => session.ReadTransportFile(path) is { } bytes ? Convert.ToHexString(bytes) : "none";
            reads[text] = text switch
            {
                "one" => string.Join(",", Read("/tmp/b"), Read("/tmp/a"), Read("/tmp/a"), Read("/tmp/c")),   // an unavailable read, a file, the same file again (consumed), a path nobody sent
                "two" => "no read",                                                                           // its entry is dropped unclaimed
                "thr" => Read("/tmp/a"),                                                                      // so the next chunk finds nothing
                "fou" => Read("/tmp/a"),                                                                      // an entry for another offset is stale
                _ => string.Join(",", Read("/tmp/a"), Read("/tmp/a")),                                        // two entries for one path are taken in order
            };
        };
        Task attach = session.AttachAsync(0, MuxTestHost.DefaultPresentation, Ct);
        MuxRequest request = await fake.ReadRequestAsync();
        fake.Raw.Send(MuxFrames.Snapshot(request.Id, id, 0, FakeMuxServerEnd.SnapshotJson()));
        await attach;

        void Send(long seq, string output, params (string Path, byte[]? Data)[] files)
        {
            foreach ((string path, byte[]? data) in files) fake.Raw.Send(MuxFrames.FileTransfer(id, seq, path, data ?? [], data is not null));
            fake.Raw.Send(MuxFrames.Output(id, seq, System.Text.Encoding.UTF8.GetBytes(output)));
        }

        Send(0, "one", ("/tmp/b", null), ("/tmp/a", [1, 2]));
        Send(3, "two", ("/tmp/a", [3]));
        Send(6, "thr");
        fake.Raw.Send(MuxFrames.FileTransfer(id, 100, "/tmp/a", [5], hasData: true));
        Send(9, "fou");
        Send(12, "fiv", ("/tmp/a", [7]), ("/tmp/a", [8]));
        await TestWait.UntilAsync(() => reads.Count == 5, "every output frame was parsed");

        Assert.Equal("none,0102,none,none", reads["one"]);
        Assert.Equal("no read", reads["two"]);
        Assert.Equal("none", reads["thr"]);
        Assert.Equal("none", reads["fou"]);
        Assert.Equal("07,08", reads["fiv"]);

        // A daemon forwards at most MaxFileTransfersPerChunk entries (and MaxFileTransferBytes) for one chunk: a client that is sent more is not following a daemon it understands.
        for (int i = 0; i < MuxFrames.MaxFileTransfersPerChunk; i++) session.DeliverFile(15, "/tmp/many", [1]);
        Assert.Throws<MuxProtocolException>(() => session.DeliverFile(15, "/tmp/many", [1]));
        session.DeliverFile(16, "/tmp/other-chunk", [1]);                                   // another offset has its own budget
        Assert.Throws<MuxProtocolException>(() => session.DeliverFile(16, "/tmp/big", new byte[MuxFrames.MaxFileTransferBytes]));
    }

    /// <summary>The packed form of one opaque pixel, written out so the test needs no VT internals (a literal of one pixel).</summary>
    private static class KittyFrameCodecProbe
    {
        internal static readonly byte[] OnePixel = [0, 1, 2, 3, 255];
    }

    [Fact]
    public void The_image_policy_fields_are_omitted_at_zero_and_named_otherwise()
    {
        var id = Guid.Parse("00000000-0000-0000-0000-000000000001");
        Assert.Equal("{\"sessionId\":\"00000000-0000-0000-0000-000000000001\"}",
            JsonSerializer.Serialize(new SpawnResult { SessionId = id }, MuxJsonContext.Default.SpawnResult));
        Assert.Equal("{\"sessionId\":\"00000000-0000-0000-0000-000000000001\",\"requiredImagePolicy\":1}",
            JsonSerializer.Serialize(new SpawnResult { SessionId = id, RequiredImagePolicy = 1 }, MuxJsonContext.Default.SpawnResult));
        Assert.DoesNotContain("requiredImagePolicy", JsonSerializer.Serialize(new SessionSummary { SessionId = id }, MuxJsonContext.Default.SessionSummary), StringComparison.Ordinal);
        Assert.Contains("\"requiredImagePolicy\":1", JsonSerializer.Serialize(
            new AttachParams { SessionId = id, Presentation = MuxTestHost.DefaultPresentation, RequiredImagePolicy = 1 }, MuxJsonContext.Default.AttachParams), StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"version\":2,\"maxImagePolicy\":1,\"maxImagePolicy\":0}", MuxJsonContext.Default.WelcomeResult));
    }
}
