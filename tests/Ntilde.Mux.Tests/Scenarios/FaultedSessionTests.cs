using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;

namespace Ntilde.Mux.Tests.Scenarios;

public sealed class FaultedSessionTests
{
    [Fact]
    public async Task A_faulted_session_ends_its_own_stream_and_the_connection_and_other_sessions_carry_on()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid a = await MuxTestHost.SpawnAsync(client);
        Guid b = await MuxTestHost.SpawnAsync(client);
        ClientPaneModel paneA = await MuxTestHost.AttachPaneAsync(client, a);
        ClientPaneModel paneB = await MuxTestHost.AttachPaneAsync(client, b);
        var faults = new List<string>();
        paneA.Session.Faulted += faults.Add;
        paneB.Session.Faulted += faults.Add;
        host.Fake(a).Emit("before the fault");
        await host.SettleAsync(a, client);

        // The mux answers DA through the session's SendInput, which throws inside Process: to the
        // parse thread that is a parser failure, and the session faults.
        host.Fake(a).ThrowOnSendInput = true;
        host.Fake(a).Emit("\x1b[c");
        await host.SettleAsync(a, client);
        Assert.True(host.Mux(a).IsFaulted);

        // A's offset has moved on without an Output frame. Nothing after the fault may reach A's
        // clients: a ResizeEvent at the new offset would read as a gap and drop the whole connection.
        paneA.Session.Resize(100, 30);
        host.Fake(a).Emit("after the fault");
        host.Fake(b).Emit("B keeps streaming");
        await host.SettleAsync(a, client);
        await host.SettleAsync(b, client);

        Assert.True(client.IsConnected, client.DisconnectReason);
        host.AssertPaneMatchesMux(b, paneB, "B after A faulted");
        Assert.Equal(0, host.Mux(a).AttachedClients);
        Assert.Equal("out:before the fault", paneA.Events[^1]);

        // A's pane was told, once, and detached locally; B's was not told anything.
        string reason = Assert.Single(faults);
        Assert.Contains("parser", reason, StringComparison.Ordinal);
        Assert.True(paneA.Session.IsFaulted);
        Assert.False(paneA.Session.IsAttached);
        Assert.False(paneB.Session.IsFaulted);
        Assert.True(paneB.Session.IsAttached);
        SessionSummary summary = Assert.Single(await client.ListSessionsAsync(TestContext.Current.CancellationToken), s => s.SessionId == a);
        Assert.True(summary.Faulted);
    }

    [Fact]
    public async Task Attaching_to_a_faulted_session_is_refused_without_touching_the_connection()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid a = await MuxTestHost.SpawnAsync(client);
        host.Fake(a).ThrowOnSendInput = true;
        host.Fake(a).Emit("\x1b[c");
        await host.Mux(a).FlushAsync();

        MuxClientSession session = client.OpenSession(a, "scripted");
        var ex = await Assert.ThrowsAsync<MuxProtocolException>(() => session.AttachAsync(100, MuxTestHost.DefaultPresentation, TestContext.Current.CancellationToken));

        Assert.Equal(MuxErrorCodes.Internal, ex.Code);
        Assert.True(client.IsConnected);
    }
}
