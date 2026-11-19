using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Mux.TextClient;
using Ntilde.Pty;
using Ntilde.VT;
using Ntilde.VT.Tests;
using Ntilde.VT.Tests.StateTransfer;

namespace Ntilde.Mux.Tests.Headless;

/// <summary>
/// #838: under the image policy the daemon's parser answers Kitty commands itself, with no decoder, as a direct pane
/// answers them, and every client of the session places the same images in the same cells.
/// </summary>
public sealed class HeadlessImagePolicyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Kitty(string controls, string payload = "") => $"\x1b_G{controls};{payload}\x1b\\";
    private static string ITerm(string arguments, string payload) => $"\x1b]1337;File=inline=1{arguments}:{payload}\x07";

    /// <summary>#838's probe, a transmit, a placement, a hard delete and a placement of what it deleted.</summary>
    private static readonly string KittyCommands =
        Kitty("i=31,s=1,v=1,a=q,t=d,f=24", "AAAA")
        + Kitty("a=t,i=2,f=100", TestImages.PngBase64(8, 8))
        + Kitty("a=p,i=2,C=1")
        + Kitty("a=t,i=3,f=24,s=2,v=1", "AAAAAAAA")
        + Kitty("a=d,d=I,i=2")
        + Kitty("a=p,i=2");

    [Theory]
    [InlineData(false)]
    [InlineData(true)] // ConPTY on Windows: native APC is answered too, so a persistent tab answers #838's probe with OK
    public async Task The_daemon_answers_Kitty_commands_as_a_direct_pane_does(bool conPtyFiltering)
    {
        (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession(PersistentImagePolicy.RetainedImagesVersion, conPtyFiltering);
        using (mux)
        {
            fake.Emit(KittyCommands);
            await mux.FlushAsync();
            List<string> expected = DirectPaneReplies(KittyCommands);
            await TestWait.UntilAsync(() => fake.SentInput.Count == expected.Count, "the Kitty replies reached the child writer");

            Assert.Equal(expected, fake.SentInput.ToArray());
            Assert.Equal("\x1b_Gi=31;OK\x1b\\", expected[0]);
            Assert.Contains(expected, reply => reply.Contains("ENOENT", StringComparison.Ordinal)); // the deleted image is gone
        }
    }

    [Fact]
    public async Task A_SIXEL_only_session_still_refuses_Kitty_graphics()
    {
        (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession(PersistentImagePolicy.Legacy, conPtyFiltering: false);
        using (mux)
        {
            fake.Emit(Kitty("i=31,s=1,v=1,a=q,t=d,f=24", "AAAA"));
            await mux.FlushAsync();
            await TestWait.UntilAsync(() => fake.SentInput.Count == 1, "the probe reply reached the child writer");
            Assert.Equal("\x1b_Gi=31;ERR\x1b\\", fake.SentInput.Single());
        }
    }

    [Fact]
    public async Task Every_client_places_the_same_images_across_a_chunked_transfer_and_a_reattach()
    {
        using var host = new MuxTestHost();
        var options = new MuxClientOptions { MaxImagePolicy = PersistentImagePolicy.EnabledVersion };
        MuxClient first = await host.ConnectClientAsync(options);
        Guid id = await first.SpawnAsync(new SpawnParams { Command = "scripted", Cols = 80, Rows = 24, Title = "t", RequiredImagePolicy = 1 }, Ct);
        ClientPaneModel firstPane = await MuxTestHost.AttachPaneAsync(first, id, imagePolicy: 1);

        // The first chunk of a Kitty transfer arrives before the second pane attaches; the rest after it.
        string png = TestImages.PngBase64(30, 40);
        host.Fake(id).Emit("start " + Kitty("a=T,i=5,f=100,m=1", png[..20]));
        await host.SettleAsync(id, first);
        MuxClient second = await host.ConnectClientAsync(options);
        ClientPaneModel secondPane = await MuxTestHost.AttachPaneAsync(second, id, imagePolicy: 1);
        host.Fake(id).Emit(Kitty("m=0", png[20..]) + "\r\n" + ITerm(";width=4", TestImages.PngBase64(10, 10)) + "tail");
        await host.SettleAsync(id, first, second);

        Assert.Equal(2, host.Mux(id).Buffer.Images.Count);
        host.AssertPaneMatchesMux(id, firstPane, "attached throughout");
        host.AssertPaneMatchesMux(id, secondPane, "attached between the chunks");

        // A later client gets both images from the snapshot alone, and so does the text client.
        MuxClient third = await host.ConnectClientAsync(options);
        ClientPaneModel thirdPane = await MuxTestHost.AttachPaneAsync(third, id, imagePolicy: 1);
        MuxClient text = await host.ConnectClientAsync(options);
        MuxClientSession textSession = text.OpenSession(id, requiredImagePolicy: 1);
        using var model = new TextClientModel(textSession);
        await textSession.AttachAsync(10_000, MuxTestHost.DefaultPresentation, Ct);
        await host.SettleAsync(id, third, text);

        host.AssertPaneMatchesMux(id, thirdPane, "reattached");
        Assert.Equal(2, thirdPane.Buffer.Images.Count);
        TerminalStateAssert.AssertEquivalent("[text client]", host.Mux(id).Buffer, host.Mux(id).Parser, model.Buffer, model.Parser);
    }

    /// <summary>A root, a raw frame, a frame built on the root, then playback and a chosen frame: the same bytes a direct pane would get.</summary>
    private static readonly string AnimationCommands =
        Kitty("a=T,i=1,f=32,s=2,v=1,p=1,c=2,r=1,C=1", Convert.ToBase64String([255, 0, 0, 255, 0, 0, 255, 255]))
        + Kitty("a=f,i=1,f=32,s=2,v=1,z=100", Convert.ToBase64String([0, 255, 0, 255, 0, 255, 0, 255]))
        + Kitty("a=f,i=1,f=32,s=1,v=1,x=1,c=1,z=120", Convert.ToBase64String([9, 8, 7, 255]))
        + Kitty("a=a,i=1,c=3");

    [Fact]
    public async Task Every_client_of_a_policy_three_session_holds_the_same_animation_across_a_chunked_transfer_and_a_reattach()
    {
        using var host = new MuxTestHost();
        var options = new MuxClientOptions { MaxImagePolicy = PersistentImagePolicy.EnabledVersion };
        MuxClient first = await host.ConnectClientAsync(options);
        int policy = PersistentImagePolicy.AnimationImagesVersion;
        Guid id = await first.SpawnAsync(new SpawnParams { Command = "scripted", Cols = 80, Rows = 24, Title = "t", RequiredImagePolicy = policy }, Ct);
        ClientPaneModel firstPane = await MuxTestHost.AttachPaneAsync(first, id, imagePolicy: policy);

        // The root and the first frame arrive before the second pane attaches; the rest after it.
        host.Fake(id).Emit(AnimationCommands[..AnimationCommands.IndexOf("\x1b_Ga=f,i=1,f=32,s=1", StringComparison.Ordinal)]);
        await host.SettleAsync(id, first);
        MuxClient second = await host.ConnectClientAsync(options);
        ClientPaneModel secondPane = await MuxTestHost.AttachPaneAsync(second, id, imagePolicy: policy);
        host.Fake(id).Emit(AnimationCommands[AnimationCommands.IndexOf("\x1b_Ga=f,i=1,f=32,s=1", StringComparison.Ordinal)..]);
        await host.SettleAsync(id, first, second);

        string daemon = Frames(host.Mux(id).Buffer);
        Assert.Contains("\"frames\":[", daemon, StringComparison.Ordinal);
        Assert.Equal(daemon, Frames(firstPane.Buffer));
        Assert.Equal(daemon, Frames(secondPane.Buffer));
        host.AssertPaneMatchesMux(id, firstPane, "attached throughout");
        host.AssertPaneMatchesMux(id, secondPane, "attached between the frames");

        // A later client gets the frames and the playhead from the snapshot alone.
        MuxClient third = await host.ConnectClientAsync(options);
        ClientPaneModel thirdPane = await MuxTestHost.AttachPaneAsync(third, id, imagePolicy: policy);
        await host.SettleAsync(id, third);
        Assert.Equal(daemon, Frames(thirdPane.Buffer));
        host.AssertPaneMatchesMux(id, thirdPane, "reattached");
        Assert.True(thirdPane.Buffer.HasKittyAnimations);
    }

    private static readonly string ComposeCommands =
        Kitty("a=T,i=1,f=32,s=2,v=1,p=1,c=2,r=1,C=1", Convert.ToBase64String([255, 0, 0, 255, 0, 0, 255, 255]))
        + Kitty("a=f,i=1,f=32,s=2,v=1,z=100", Convert.ToBase64String([0, 255, 0, 255, 0, 255, 0, 255]))
        + Kitty("a=c,i=1,r=1,c=2,w=1,X=1,x=0,C=1")
        + Kitty("a=a,i=1,c=2");

    [Fact]
    public async Task Every_client_of_a_policy_four_session_composes_the_same_frames_and_a_policy_three_one_refuses_composition()
    {
        using var host = new MuxTestHost();
        var options = new MuxClientOptions { MaxImagePolicy = PersistentImagePolicy.EnabledVersion };
        MuxClient first = await host.ConnectClientAsync(options);
        int policy = PersistentImagePolicy.CompositionImagesVersion;
        Guid id = await first.SpawnAsync(new SpawnParams { Command = "scripted", Cols = 80, Rows = 24, Title = "t", RequiredImagePolicy = policy }, Ct);
        ClientPaneModel firstPane = await MuxTestHost.AttachPaneAsync(first, id, imagePolicy: policy);
        host.Fake(id).Emit(ComposeCommands[..ComposeCommands.IndexOf("\x1b_Ga=c", StringComparison.Ordinal)]);
        await host.SettleAsync(id, first);
        MuxClient second = await host.ConnectClientAsync(options);
        ClientPaneModel secondPane = await MuxTestHost.AttachPaneAsync(second, id, imagePolicy: policy);
        host.Fake(id).Emit(ComposeCommands[ComposeCommands.IndexOf("\x1b_Ga=c", StringComparison.Ordinal)..]);
        await host.SettleAsync(id, first, second);
        string daemon = Frames(host.Mux(id).Buffer);
        Assert.Contains("\"frames\":[", daemon, StringComparison.Ordinal);
        Assert.Equal(daemon, Frames(firstPane.Buffer));
        Assert.Equal(daemon, Frames(secondPane.Buffer));
        host.AssertPaneMatchesMux(id, firstPane, "attached throughout");
        host.AssertPaneMatchesMux(id, secondPane, "attached between the frames");

        // Under policy three the same composition is refused on the daemon, and every client then holds the frames it had before.
        (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession(PersistentImagePolicy.AnimationImagesVersion, conPtyFiltering: false);
        using (mux)
        {
            fake.Emit(ComposeCommands);
            await mux.FlushAsync();
            await TestWait.UntilAsync(() => fake.SentInput.Count >= 4, "the replies reached the child writer");
            Assert.Contains("ENOTSUP", fake.SentInput.ToArray()[2], StringComparison.Ordinal);
            // Frame 2 stayed green|green there, while the policy-four daemon (and so every client of it) holds the composed blue|green: the compose took effect only where the policy carries it.
            Assert.NotEqual(daemon, Frames(mux.Buffer));
        }
    }

    private static string Utf8Base64(string text) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task Every_client_of_a_policy_six_session_places_the_image_of_a_file_the_daemon_read_and_a_policy_five_one_refuses_t_f()
    {
        // #887: the daemon reads the file; every client is told what it read and places the same image from the same bytes, whether it was attached throughout, joined in the middle of the
        // command, or attached afterwards (from the snapshot), and a file the daemon refuses is refused for everyone.
        string temp = Path.Combine(Path.GetTempPath(), "ntilde-tf-" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(temp, Convert.FromBase64String(TestImages.PngBase64(30, 40)));
        try
        {
            using var host = new MuxTestHost();
            var options = new MuxClientOptions { MaxImagePolicy = PersistentImagePolicy.EnabledVersion };
            MuxClient first = await host.ConnectClientAsync(options);
            int policy = PersistentImagePolicy.FileTransportImagesVersion;
            Guid id = await first.SpawnAsync(new SpawnParams { Command = "scripted", Cols = 80, Rows = 24, Title = "t", RequiredImagePolicy = policy }, Ct);
            ClientPaneModel firstPane = await MuxTestHost.AttachPaneAsync(first, id, imagePolicy: policy);

            // The command is split across two chunks: its first half arrives before the second pane attaches, the rest (where the daemon reads the file) after it.
            string command = Kitty("a=T,i=1,t=f,f=100", Utf8Base64(temp));
            int split = command.Length - 7;
            host.Fake(id).Emit("start " + command[..split]);
            await host.SettleAsync(id, first);
            MuxClient second = await host.ConnectClientAsync(options);
            ClientPaneModel secondPane = await MuxTestHost.AttachPaneAsync(second, id, imagePolicy: policy);
            string outside = OperatingSystem.IsWindows() ? @"C:\Windows\win.ini" : "/etc/hosts";
            host.Fake(id).Emit(command[split..] + "\r\n" + Kitty("a=T,i=2,t=f,f=100", Utf8Base64(outside)) + Kitty("a=q,i=31,t=f,s=1,v=1,a=q", "AAAA") + "tail");
            await host.SettleAsync(id, first, second);

            // Replies reach the child on the session's input writer thread, after the parse thread the settle flushed.
            await TestWait.UntilAsync(() => host.Fake(id).SentInput.Count >= 3, "the three Kitty replies reached the child writer");
            string[] replies = host.Fake(id).SentInput.ToArray();
            Assert.Contains("\x1b_Gi=1;OK\x1b\\", replies);                                               // the file was read and placed
            Assert.Contains(replies, reply => reply.StartsWith("\x1b_Gi=2;EINVAL:", StringComparison.Ordinal));   // a path outside temp is refused
            Assert.Contains("\x1b_Gi=31;OK\x1b\\", replies);                                              // the probe for t=f is answered OK
            Assert.Single(host.Mux(id).Buffer.Images);
            host.AssertPaneMatchesMux(id, firstPane, "attached throughout");
            host.AssertPaneMatchesMux(id, secondPane, "attached in the middle of the command");

            // A later client and the text client get the image from the snapshot alone.
            MuxClient third = await host.ConnectClientAsync(options);
            ClientPaneModel thirdPane = await MuxTestHost.AttachPaneAsync(third, id, imagePolicy: policy);
            MuxClient text = await host.ConnectClientAsync(options);
            MuxClientSession textSession = text.OpenSession(id, requiredImagePolicy: policy);
            using var model = new TextClientModel(textSession);
            await textSession.AttachAsync(10_000, MuxTestHost.DefaultPresentation, Ct);
            await host.SettleAsync(id, third, text);
            host.AssertPaneMatchesMux(id, thirdPane, "reattached");
            Assert.Single(thirdPane.Buffer.Images);
            TerminalStateAssert.AssertEquivalent("[text client]", host.Mux(id).Buffer, host.Mux(id).Parser, model.Buffer, model.Parser);

            // A policy-5 session still has no reader: the probe answers ERR and the transmit is refused, so programs fall back to t=d.
            (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession(PersistentImagePolicy.PlaceholderImagesVersion, conPtyFiltering: false);
            using (mux)
            {
                fake.Emit(Kitty("a=q,i=31,t=f,s=1,v=1,a=q", "AAAA") + Kitty("a=T,i=1,t=f,f=100", Utf8Base64(temp)));
                await mux.FlushAsync();
                await TestWait.UntilAsync(() => fake.SentInput.Count >= 2, "the replies reached the child writer");
                string[] refused = fake.SentInput.ToArray();
                Assert.Equal("\x1b_Gi=31;ERR\x1b\\", refused[0]);
                Assert.StartsWith("\x1b_Gi=1;EINVAL:", refused[1], StringComparison.Ordinal);
                Assert.Empty(mux.Buffer.Images);
            }
        }
        finally { File.Delete(temp); }
    }

    [Fact]
    public async Task A_policy_six_session_bounds_what_one_chunk_stages_and_an_overlong_path_neither_faults_it_nor_reaches_a_client()
    {
        string temp = Path.Combine(Path.GetTempPath(), "ntilde-tf-" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(temp, Convert.FromBase64String(TestImages.PngBase64(8, 8)));
        try
        {
            using var host = new MuxTestHost();
            var options = new MuxClientOptions { MaxImagePolicy = PersistentImagePolicy.EnabledVersion };
            MuxClient first = await host.ConnectClientAsync(options);
            int policy = PersistentImagePolicy.FileTransportImagesVersion;
            Guid id = await first.SpawnAsync(new SpawnParams { Command = "scripted", Cols = 80, Rows = 24, Title = "t", RequiredImagePolicy = policy }, Ct);
            ClientPaneModel pane = await MuxTestHost.AttachPaneAsync(first, id, imagePolicy: policy);

            // An overlong path (past what a frame carries) is refused like any unreadable one, with a client attached, and the session goes on.
            string overlong = "/tmp/" + new string('a', MuxFrames.MaxFileTransferPathBytes + 10);
            // More reads in one chunk than a daemon stages: the first MaxFileTransfersPerChunk are read, the rest are unavailable for everyone.
            int total = MuxFrames.MaxFileTransfersPerChunk + 5;
            host.Fake(id).Emit(Kitty("a=T,i=500,t=f,f=100", Utf8Base64(overlong)) + string.Concat(Enumerable.Range(1, total).Select(n => Kitty($"a=T,i={n},t=f,f=100", Utf8Base64(temp)))) + "tail");
            await host.SettleAsync(id, first);

            await TestWait.UntilAsync(() => host.Fake(id).SentInput.Count >= total + 1, "every reply reached the child writer"); // on the input writer thread, after the flush
            string[] replies = host.Fake(id).SentInput.ToArray();
            Assert.StartsWith("\x1b_Gi=500;EINVAL:", replies[0], StringComparison.Ordinal);
            Assert.Equal(MuxFrames.MaxFileTransfersPerChunk, replies.Count(reply => reply.EndsWith(";OK\x1b\\", StringComparison.Ordinal)));
            Assert.Equal(5, replies.Count(reply => reply.Contains(";EINVAL:", StringComparison.Ordinal)) - 1);
            Assert.Equal(MuxFrames.MaxFileTransfersPerChunk, host.Mux(id).Buffer.Images.Count);
            host.AssertPaneMatchesMux(id, pane, "the client placed exactly what the daemon did");
            host.Fake(id).Emit("still alive");
            await host.SettleAsync(id, first);
            host.AssertPaneMatchesMux(id, pane, "the session was not faulted");
        }
        finally { File.Delete(temp); }
    }

    [Fact]
    public void One_chunk_cannot_stage_more_file_bytes_than_a_frame_carries()
    {
        string a = Path.Combine(Path.GetTempPath(), "ntilde-tf-" + Guid.NewGuid().ToString("N") + "-a.bin"), b = a.Replace("-a.bin", "-b.bin", StringComparison.Ordinal);
        try
        {
            long each = (MuxFrames.MaxFileTransferBytes / 2) + 1024; // two of them do not fit one frame's worth
            foreach (string file in new[] { a, b })
            {
                using var stream = new FileStream(file, FileMode.Create);
                stream.SetLength(each);
            }

            (HeadlessTerminalSession mux, _) = NewSession(PersistentImagePolicy.FileTransportImagesVersion, conPtyFiltering: false);
            using (mux)
            {
                Assert.Equal(each, mux.ReadTransportFile(a)!.Length);
                Assert.Null(mux.ReadTransportFile(b));                                      // what is left of the chunk's room is smaller than this file
            }
        }
        finally { File.Delete(a); File.Delete(b); }
    }

    private const string Placeholder = "\U0010EEEE";
    private static readonly string[] Marks = ["\u0305", "\u030D"]; // kitty's first two row/column diacritics: numbers 0 and 1
    private static string Mark(int number) => Marks[number];
    private static List<TerminalKittyVirtualPlacementState> Virtuals(TerminalBuffer buffer) =>
        buffer.ExportState(int.MaxValue).KittyImages?.SingleOrDefault()?.VirtualPlacements ?? [];

    /// <summary>An image and a 2 x 1 virtual placement named 7 (invisible), sent before a second client attaches.</summary>
    private static readonly string PlaceholderSetup =
        Kitty("a=t,i=42,f=32,s=1,v=1", Convert.ToBase64String([1, 2, 3, 255]))
        + Kitty("a=p,U=1,i=42,p=7,c=2,r=1");

    /// <summary>The two cells that show it, printed once everyone is attached.</summary>
    private static readonly string PlaceholderCells = $"\x1b[3;4H\x1b[38;5;42m\x1b[58;5;7m{Placeholder}{Mark(0)}{Mark(0)}{Placeholder}{Mark(0)}{Mark(1)}\x1b[0m";

    private static string PlaceholderRuns(TerminalBuffer buffer)
    {
        buffer.Lock.EnterReadLock();
        try
        {
            return string.Join("|", buffer.GetVisibleImagesSnapshot(buffer.TotalLines - buffer.Rows, buffer.Rows).Where(image => image.GridColumns > 0)
                .Select(run => $"{run.CellX},{run.CellY}+{run.CellWidth} g{run.GridColumn},{run.GridRow}/{run.GridColumns}x{run.GridRows}"));
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Fact]
    public async Task Every_client_of_a_policy_five_session_tiles_the_same_placeholders_and_a_policy_four_one_refuses_them()
    {
        using var host = new MuxTestHost();
        var options = new MuxClientOptions { MaxImagePolicy = PersistentImagePolicy.EnabledVersion };
        MuxClient first = await host.ConnectClientAsync(options);
        int policy = PersistentImagePolicy.PlaceholderImagesVersion;
        Guid id = await first.SpawnAsync(new SpawnParams { Command = "scripted", Cols = 80, Rows = 24, Title = "t", RequiredImagePolicy = policy }, Ct);
        ClientPaneModel firstPane = await MuxTestHost.AttachPaneAsync(first, id, imagePolicy: policy);
        host.Fake(id).Emit(PlaceholderSetup);
        await host.SettleAsync(id, first);
        MuxClient second = await host.ConnectClientAsync(options);
        ClientPaneModel secondPane = await MuxTestHost.AttachPaneAsync(second, id, imagePolicy: policy);   // restored from a snapshot carrying the virtual placement
        host.Fake(id).Emit(PlaceholderCells);
        await host.SettleAsync(id, first, second);
        string daemon = PlaceholderRuns(host.Mux(id).Buffer);
        Assert.Equal("3,", daemon[..2]);                                                                    // one run of two cells on row 3 (1-based) starting at column 4
        Assert.Equal(daemon, PlaceholderRuns(firstPane.Buffer));
        Assert.Equal(daemon, PlaceholderRuns(secondPane.Buffer));
        Assert.Equal(7u, Assert.Single(Virtuals(host.Mux(id).Buffer)).Placement);
        Assert.Equal(7u, Assert.Single(Virtuals(secondPane.Buffer)).Placement);
        host.AssertPaneMatchesMux(id, firstPane, "attached throughout");
        host.AssertPaneMatchesMux(id, secondPane, "attached after the placement");

        // Under policy four the same placement is refused on the daemon, so nobody holds a grid for the cells.
        (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession(PersistentImagePolicy.CompositionImagesVersion, conPtyFiltering: false);
        using (mux)
        {
            fake.Emit(PlaceholderSetup);
            await mux.FlushAsync();
            await TestWait.UntilAsync(() => fake.SentInput.Count >= 2, "the replies reached the child writer");
            Assert.Contains("ENOTSUP", fake.SentInput.ToArray()[1], StringComparison.Ordinal);
            Assert.Empty(Virtuals(mux.Buffer));
        }
    }

    [Fact]
    public async Task A_policy_two_session_still_refuses_animation_and_its_snapshot_carries_none()
    {
        (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession(PersistentImagePolicy.MultipartImagesVersion, conPtyFiltering: false);
        using (mux)
        {
            fake.Emit(AnimationCommands);
            await mux.FlushAsync();
            await TestWait.UntilAsync(() => fake.SentInput.Count >= 4, "the replies reached the child writer");
            Assert.Contains("ENOTSUP", fake.SentInput.ToArray()[1], StringComparison.Ordinal);
            Assert.Null(mux.Buffer.ExportState(int.MaxValue).KittyImages!.Single().Animation);
        }
    }

    /// <summary>The exported frames, without the elapsed time each side reads off its own clock.</summary>
    private static string Frames(TerminalBuffer buffer) => System.Text.RegularExpressions.Regex.Replace(
        System.Text.Json.JsonSerializer.Serialize(buffer.ExportState(int.MaxValue).KittyImages), "\"elapsed_ms\":\\d+", "\"elapsed_ms\":0");

    [Fact]
    public async Task Every_client_places_the_same_image_when_one_attaches_between_multipart_parts()
    {
        // imgcat's default form: the transfer is parser state, so a client attaching between FileParts
        // gets the parts so far in its snapshot and assembles the same image as the daemon.
        using var host = new MuxTestHost();
        var options = new MuxClientOptions { MaxImagePolicy = PersistentImagePolicy.EnabledVersion };
        MuxClient first = await host.ConnectClientAsync(options);
        Guid id = await first.SpawnAsync(new SpawnParams { Command = "scripted", Cols = 80, Rows = 24, Title = "t", RequiredImagePolicy = PersistentImagePolicy.MultipartImagesVersion }, Ct);
        ClientPaneModel firstPane = await MuxTestHost.AttachPaneAsync(first, id, imagePolicy: PersistentImagePolicy.MultipartImagesVersion);

        string png = Convert.ToBase64String([.. TestImages.PngHeader(30, 40), .. new byte[500]]);
        static string Part(string chunk) => $"\x1b]1337;FilePart={chunk}\x07";
        host.Fake(id).Emit("start \x1b]1337;MultipartFile=inline=1;size=533;width=6\x07" + Part(png[..200]) + Part(png[200..400]));
        await host.SettleAsync(id, first);
        MuxClient second = await host.ConnectClientAsync(options);
        ClientPaneModel secondPane = await MuxTestHost.AttachPaneAsync(second, id, imagePolicy: PersistentImagePolicy.MultipartImagesVersion);
        host.Fake(id).Emit(Part(png[400..600]) + Part(png[600..]) + "\x1b]1337;FileEnd\x07\ntail");
        await host.SettleAsync(id, first, second);

        TerminalImage image = Assert.Single(host.Mux(id).Buffer.Images);
        Assert.Equal(6, image.CellWidth);
        host.AssertPaneMatchesMux(id, firstPane, "attached throughout");
        host.AssertPaneMatchesMux(id, secondPane, "attached between the parts");

        // A later client gets the image from the snapshot alone, and so does the text client.
        MuxClient third = await host.ConnectClientAsync(options);
        ClientPaneModel thirdPane = await MuxTestHost.AttachPaneAsync(third, id, imagePolicy: PersistentImagePolicy.MultipartImagesVersion);
        MuxClient text = await host.ConnectClientAsync(options);
        MuxClientSession textSession = text.OpenSession(id, requiredImagePolicy: PersistentImagePolicy.MultipartImagesVersion);
        using var model = new TextClientModel(textSession);
        await textSession.AttachAsync(10_000, MuxTestHost.DefaultPresentation, Ct);
        await host.SettleAsync(id, third, text);

        host.AssertPaneMatchesMux(id, thirdPane, "reattached");
        Assert.Single(thirdPane.Buffer.Images);
        TerminalStateAssert.AssertEquivalent("[text client]", host.Mux(id).Buffer, host.Mux(id).Parser, model.Buffer, model.Parser);
    }

    [Fact]
    public async Task A_version_1_session_ignores_the_multipart_form_on_the_daemon_and_every_client()
    {
        // A peer from before the multipart form ignores it, and policy 1 is what such a peer negotiates: an updated
        // daemon or client in a version-1 session must ignore it too, or one side would place the image alone.
        using var host = new MuxTestHost();
        var options = new MuxClientOptions { MaxImagePolicy = PersistentImagePolicy.EnabledVersion };
        MuxClient first = await host.ConnectClientAsync(options);
        Guid id = await first.SpawnAsync(new SpawnParams { Command = "scripted", Cols = 80, Rows = 24, Title = "t", RequiredImagePolicy = PersistentImagePolicy.RetainedImagesVersion }, Ct);
        ClientPaneModel pane = await MuxTestHost.AttachPaneAsync(first, id, imagePolicy: PersistentImagePolicy.RetainedImagesVersion);

        string png = TestImages.PngBase64(30, 40);
        host.Fake(id).Emit("a\x1b]1337;MultipartFile=inline=1;width=6\x07\x1b]1337;FilePart=" + png + "\x07\x1b]1337;FileEnd\x07b"
            + ITerm(";width=2", TestImages.PngBase64(8, 8)));
        await host.SettleAsync(id, first);

        TerminalImage image = Assert.Single(host.Mux(id).Buffer.Images); // only the single-sequence image
        Assert.Equal(2, image.CellWidth);
        host.AssertPaneMatchesMux(id, pane, "version 1");
    }

    /// <summary>What a direct pane without a decoder replies to <paramref name="input"/>, configured as the policy configures one.</summary>
    private static List<string> DirectPaneReplies(string input)
    {
        var replies = new List<string>();
        var parser = new AnsiParser(new TerminalBuffer(80, 24), forceConPtyFiltering: false) { ImageDecoder = null };
        PersistentImagePolicy.Apply(parser, PersistentImagePolicy.RetainedImagesVersion);
        parser.OnResponse = replies.Add;
        parser.Process(input);
        return replies;
    }

    private static (HeadlessTerminalSession Mux, ScriptedTerminalSession Fake) NewSession(int imagePolicy, bool conPtyFiltering)
    {
        var fake = new ScriptedTerminalSession(new TerminalSessionRequest("scripted", string.Empty, string.Empty, 80, 24, null, false, null));
        var mux = new HeadlessTerminalSession(Guid.NewGuid(), fake, new HeadlessSessionOptions
        {
            Command = "scripted",
            Title = "t",
            Cols = 80,
            Rows = 24,
            ForceConPtyFiltering = conPtyFiltering,
            RequiredImagePolicy = imagePolicy,
        });
        return (mux, fake);
    }
}
