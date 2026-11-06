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

    [Fact]
    public async Task Every_client_places_the_same_image_when_one_attaches_between_multipart_parts()
    {
        // imgcat's default form: the transfer is parser state, so a client attaching between FileParts
        // gets the parts so far in its snapshot and assembles the same image as the daemon.
        using var host = new MuxTestHost();
        var options = new MuxClientOptions { MaxImagePolicy = PersistentImagePolicy.EnabledVersion };
        MuxClient first = await host.ConnectClientAsync(options);
        Guid id = await first.SpawnAsync(new SpawnParams { Command = "scripted", Cols = 80, Rows = 24, Title = "t", RequiredImagePolicy = 1 }, Ct);
        ClientPaneModel firstPane = await MuxTestHost.AttachPaneAsync(first, id, imagePolicy: 1);

        string png = Convert.ToBase64String([.. TestImages.PngHeader(30, 40), .. new byte[500]]);
        static string Part(string chunk) => $"\x1b]1337;FilePart={chunk}\x07";
        host.Fake(id).Emit("start \x1b]1337;MultipartFile=inline=1;size=533;width=6\x07" + Part(png[..200]) + Part(png[200..400]));
        await host.SettleAsync(id, first);
        MuxClient second = await host.ConnectClientAsync(options);
        ClientPaneModel secondPane = await MuxTestHost.AttachPaneAsync(second, id, imagePolicy: 1);
        host.Fake(id).Emit(Part(png[400..600]) + Part(png[600..]) + "\x1b]1337;FileEnd\x07\ntail");
        await host.SettleAsync(id, first, second);

        TerminalImage image = Assert.Single(host.Mux(id).Buffer.Images);
        Assert.Equal(6, image.CellWidth);
        host.AssertPaneMatchesMux(id, firstPane, "attached throughout");
        host.AssertPaneMatchesMux(id, secondPane, "attached between the parts");

        // A later client gets the image from the snapshot alone, and so does the text client.
        MuxClient third = await host.ConnectClientAsync(options);
        ClientPaneModel thirdPane = await MuxTestHost.AttachPaneAsync(third, id, imagePolicy: 1);
        MuxClient text = await host.ConnectClientAsync(options);
        MuxClientSession textSession = text.OpenSession(id, requiredImagePolicy: 1);
        using var model = new TextClientModel(textSession);
        await textSession.AttachAsync(10_000, MuxTestHost.DefaultPresentation, Ct);
        await host.SettleAsync(id, third, text);

        host.AssertPaneMatchesMux(id, thirdPane, "reattached");
        Assert.Single(thirdPane.Buffer.Images);
        TerminalStateAssert.AssertEquivalent("[text client]", host.Mux(id).Buffer, host.Mux(id).Parser, model.Buffer, model.Parser);
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
