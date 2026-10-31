using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Pty;
using Ntilde.VT;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ntilde.Mux.Tests.Headless;

/// <summary>The authoritative parser reports the current shared presentation to the child exactly once.</summary>
public sealed class HeadlessColorSchemeTests
{
    [Theory]
    [InlineData(null, 1)]
    [InlineData(0xff000000u, 1)]
    [InlineData(0xffffffffu, 2)]
    public async Task Startup_query_uses_the_supplied_background_before_any_attach(uint? background, int expected)
    {
        using var host = new MuxTestHost();
        host.Factory.StartupOutput = "\u001b[?996n";
        using var client = await host.ConnectClientAsync();
        var json = JsonNode.Parse(JsonSerializer.Serialize(new SpawnParams { Command = "scripted" }, MuxJsonContext.Default.SpawnParams))!;
        if (background is uint color) json["defaultBg"] = color;
        else Assert.False(json.AsObject().ContainsKey("defaultBg"));
        var request = JsonSerializer.Deserialize(json.ToJsonString(), MuxJsonContext.Default.SpawnParams)!;
        Guid id = await client.SpawnAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(0, host.Mux(id).AttachedClients);
        await host.Mux(id).FlushAsync();
        await TestWait.UntilAsync(() => !host.Fake(id).SentInput.IsEmpty, "the startup color query reached the child");
        Assert.Equal($"\u001b[?997;{expected}n", Assert.Single(host.Fake(id).SentInput));
    }

    [Fact]
    public async Task Startup_color_queries_use_the_wire_defaults_before_attach()
    {
        using var host = new MuxTestHost();
        host.Factory.StartupOutput = "\u001b]10;?;?;?\a\u001b]17;?\a\u001b]19;?\a\u001b]4;1;?\a";
        using var client = await host.ConnectClientAsync();
        var request = new SpawnParams
        {
            Command = "scripted",
            DefaultFg = 0xff112233,
            DefaultBg = 0xff445566,
            CursorColor = 0xff778899,
            SelectionFg = 0xff123456,
            SelectionBg = 0xffabcdef,
            AnsiPalette = Enumerable.Repeat(0xffaabbccu, 16).ToArray()
        };
        request = JsonSerializer.Deserialize(JsonSerializer.Serialize(request, MuxJsonContext.Default.SpawnParams), MuxJsonContext.Default.SpawnParams)!;
        Guid id = await client.SpawnAsync(request, TestContext.Current.CancellationToken);
        await host.Mux(id).FlushAsync();
        await TestWait.UntilAsync(() => host.Fake(id).SentInput.Count >= 6, "all startup color replies reached the child");
        Assert.Equal(new[]
        {
            "\u001b]10;rgb:1111/2222/3333\u001b\\", "\u001b]11;rgb:4444/5555/6666\u001b\\",
            "\u001b]12;rgb:7777/8888/9999\u001b\\", "\u001b]17;rgb:abab/cdcd/efef\u001b\\",
            "\u001b]19;rgb:1212/3434/5656\u001b\\", "\u001b]4;1;rgb:aaaa/bbbb/cccc\u001b\\"
        }, host.Fake(id).SentInput.ToArray());
    }

    [Fact]
    public async Task Attach_snapshot_and_presentation_changes_preserve_the_application_color_stack()
    {
        var fake = new ScriptedTerminalSession(new TerminalSessionRequest("scripted", "", "", 80, 24, null, false, null));
        using var mux = new HeadlessTerminalSession(Guid.NewGuid(), fake, new HeadlessSessionOptions { ForceConPtyFiltering = false });
        var sink = new RecordingFrameSink();
        var dark = new MuxPresentation { Cols = 80, Rows = 24, DefaultFg = 0xff112233, DefaultBg = 0xff000000 };
        mux.PostAttach(sink, 1, 0, dark, MuxProtocol.MaxFrameBytes);
        await mux.FlushAsync();
        fake.Emit("\u001b]30001\a\u001b]10;#abcdef\a");
        await mux.FlushAsync();
        mux.PostResize(sink, 80, 24, dark with { DefaultFg = 0xff445566 });
        await mux.FlushAsync();
        var snapshot = await mux.InvokeAsync(() =>
        {
            var state = mux.Buffer.ExportState(0);
            state.Parser = mux.Parser.ExportState();
            return TerminalStateSerializer.FromBytes(TerminalStateSerializer.ToBytes(state));
        });
        var target = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(target) { DefaultForeground = TermColor.FromUint(0xff445566) };
        var replies = new List<string>();
        parser.OnResponse = replies.Add;
        TerminalStateTransfer.Restore(target, parser, snapshot);
        parser.Process("\u001b]10;?\a\u001b]30101\a\u001b]10;?\a");
        Assert.Equal(new[] { "\u001b]10;rgb:abab/cdcd/efef\u001b\\", "\u001b]10;rgb:4444/5555/6666\u001b\\" }, replies);
        fake.Emit("\u001b]10;?\a\u001b]30101\a\u001b]10;?\a");
        await mux.FlushAsync();
        await TestWait.UntilAsync(() => fake.SentInput.Count >= 2, "the authoritative runtime color replies reached the child");
        Assert.Equal(replies, fake.SentInput.ToArray());
    }

    [Fact]
    public async Task Presentation_updates_and_reattach_preserve_the_color_scheme_subscription()
    {
        var fake = new ScriptedTerminalSession(new TerminalSessionRequest("scripted", "", "", 80, 24, null, false, null));
        using var mux = new HeadlessTerminalSession(Guid.NewGuid(), fake, new HeadlessSessionOptions
        {
            Command = "scripted",
            Title = "color scheme",
            ForceConPtyFiltering = false
        });
        var dark = new MuxPresentation { Cols = 80, Rows = 24, DefaultBg = TermColor.Black.ToUint() };
        var light = dark with { DefaultBg = TermColor.White.ToUint() };
        var interactive = new RecordingFrameSink();
        var observer = new RecordingFrameSink();
        mux.PostAttach(interactive, 1, 0, dark, MuxProtocol.MaxFrameBytes);
        await mux.FlushAsync();
        fake.Emit("\u001b[?996n\u001b[?2031h");
        await mux.FlushAsync();
        await TestWait.UntilAsync(() => !fake.SentInput.IsEmpty, "initial color query reached the child");
        Assert.Equal(new[] { "\u001b[?997;1n" }, fake.SentInput.ToArray());
        mux.PostResize(interactive, 80, 24, light);
        await mux.FlushAsync();
        await TestWait.UntilAsync(() => fake.SentInput.Count >= 2, "light presentation reached the child");
        mux.PostResize(interactive, 80, 24, light);
        mux.PostAttach(observer, 2, 0, dark, MuxProtocol.MaxFrameBytes, MuxAttachMode.ReadOnly);
        await mux.FlushAsync();
        Assert.Equal(new[] { "\u001b[?997;1n", "\u001b[?997;2n" }, fake.SentInput.ToArray());
        mux.PostDetach(interactive);
        await mux.FlushAsync();
        mux.PostAttach(interactive, 3, 0, dark, MuxProtocol.MaxFrameBytes);
        await mux.FlushAsync();
        await TestWait.UntilAsync(() => fake.SentInput.Count >= 3, "reattached dark presentation reached the child");
        Assert.Equal(new[] { "\u001b[?997;1n", "\u001b[?997;2n", "\u001b[?997;1n" }, fake.SentInput.ToArray());
        var snapshot = await mux.InvokeAsync(() => mux.Parser.ExportState());
        Assert.True(snapshot.ColorSchemeReportsEnabled);
        fake.Emit("\u001b[?2031l");
        await mux.FlushAsync();
        mux.PostResize(interactive, 80, 24, light);
        await mux.FlushAsync();
        Assert.Equal(3, fake.SentInput.Count);
    }
}
