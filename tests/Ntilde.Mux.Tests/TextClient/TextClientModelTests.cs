using Ntilde.Mux.Tests.Support;
using Ntilde.Mux.TextClient;

namespace Ntilde.Mux.Tests.TextClient;

public sealed class TextClientModelTests
{
    [Fact]
    public async Task The_model_equals_the_mux_after_snapshot_output_and_resize()
    {
        using var host = new MuxTestHost();
        MuxClient c = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(c);
        host.Fake(id).Emit("before attach\r\n\x1b[1mbold\x1b[0m\r\n");
        MuxClientSession session = c.OpenSession(id, "scripted");
        var model = new TextClientModel(session);
        int changes = 0;
        model.Changed += () => Interlocked.Increment(ref changes);

        await session.AttachAsync(0, MuxTestHost.DefaultPresentation, TestContext.Current.CancellationToken);
        host.Fake(id).Emit("after attach\r\n\x1b[c");               // a device query: the model must never answer it
        host.Mux(id).PostResize(100, 30, null);
        await host.SettleAsync(id, c);

        Ntilde.VT.Tests.StateTransfer.TerminalStateAssert.AssertEquivalent("[text client model]",
            host.Mux(id).Buffer, host.Mux(id).Parser, model.Buffer, model.Parser);
        Assert.True(Volatile.Read(ref changes) >= 3);
        await TestWait.UntilAsync(() => !host.Fake(id).SentInput.IsEmpty, "the mux answered the query");
        Assert.Single(host.Fake(id).SentInput);                     // once: the mux's answer, never the model's
    }
}
