using System.Text.Json.Serialization.Metadata;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;

namespace Ntilde.Mux.Tests.Server;

/// <summary>
/// Codex E1 (Phase 4 spec §3): a spawn may name the id its session takes (<see cref="SpawnParams.SessionId"/>), so a
/// client whose spawn reply is lost still knows which session to end. Without it the daemon picks one, as before.
/// The GUI's own daemon runs this same server, so it behaves the same.
/// </summary>
public sealed class RequestedSessionIdTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SpawnParams Scripted => new() { Command = "scripted", Cols = 80, Rows = 24, Title = "test" };

    private static async Task<MuxResponse> CallAsync<T>(RawMuxConnection raw, string method, T p, JsonTypeInfo<T> info)
    {
        long id = raw.Request(method, p, info);
        MuxResponse response = await raw.ReadResponseAsync();
        Assert.Equal(id, response.Id);
        return response;
    }

    [Fact]
    public async Task A_spawn_that_names_an_id_creates_exactly_that_session()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid requested = Guid.NewGuid();

        Guid id = await client.SpawnAsync(Scripted, requested, Ct);

        Assert.Equal(requested, id);
        Assert.Equal(new[] { requested }, host.Server.GetSessionIds());

        // The shell is told the id it runs under, as for one the daemon picked.
        Assert.Equal(requested.ToString("D"), Assert.Single(host.Factory.Requests).EnvironmentOverrides![MuxServer.SessionEnvironmentVariable]);
    }

    [Fact]
    public async Task A_spawn_that_names_no_id_still_gets_one_from_the_daemon()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();

        Guid first = await client.SpawnAsync(Scripted, Ct);
        Guid second = await client.SpawnAsync(Scripted, sessionId: null, Ct);

        Assert.NotEqual(Guid.Empty, first);
        Assert.NotEqual(Guid.Empty, second);
        Assert.NotEqual(first, second);
        Assert.Equal(new[] { first, second }.Order(), host.Server.GetSessionIds().Order());
    }

    /// <summary>
    /// An id in use is refused before anything is started, and the session holding it is never taken over or touched:
    /// still running, still the same shell, with the clients it had. A request error, not a disconnect.
    /// </summary>
    [Fact]
    public async Task A_spawn_that_names_an_id_in_use_is_refused_and_leaves_that_session_alone()
    {
        using var host = new MuxTestHost();
        MuxClient owner = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(owner); // the daemon's own pick: a requested id cannot take that over either
        await MuxTestHost.AttachPaneAsync(owner, id);
        await host.SettleAsync(id, owner);
        ScriptedTerminalSession shell = host.Fake(id);
        MuxClient other = await host.ConnectClientAsync();

        MuxProtocolException refused = await Assert.ThrowsAsync<MuxProtocolException>(() => other.SpawnAsync(Scripted, id, Ct));

        Assert.Equal(MuxErrorCodes.SessionExists, refused.Code);
        await host.SettleAsync(id, owner, other);
        Assert.Single(host.Factory.Requests);   // no second shell was started
        Assert.Equal(new[] { id }, host.Server.GetSessionIds());
        Assert.Same(shell, host.Fake(id));
        Assert.False(shell.Disposed);
        Assert.False(host.Mux(id).IsExited);
        Assert.Equal(1, host.Mux(id).AttachedClients);
        Assert.True(other.IsConnected);
        Assert.True(owner.IsConnected);
    }

    /// <summary>
    /// Not an id: refused with the request-level <c>protocol_error</c> (as an unknown attach mode is), nothing is
    /// started, and the connection - every other pane on it - stays up. Only the "D" form is an id on this wire, as for
    /// every other id; the empty Guid is what a missing id reads as elsewhere.
    /// </summary>
    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("")]
    [InlineData("{6f9619ff-8b86-d011-b42d-00c04fc964ff}")]
    [InlineData("6f9619ff8b86d011b42d00c04fc964ff")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task A_malformed_requested_id_is_refused_and_the_connection_survives(string requested)
    {
        using var host = new MuxTestHost();
        RawMuxConnection raw = host.ConnectRaw();
        await raw.HelloAsync();

        MuxResponse r = await CallAsync(raw, MuxMethods.Spawn, Scripted with { SessionId = requested }, MuxJsonContext.Default.SpawnParams);

        Assert.Equal(MuxErrorCodes.ProtocolError, r.Error?.Code);
        Assert.Empty(host.Factory.Requests);
        Assert.Empty(host.Server.GetSessionIds());
        Assert.Null((await CallAsync(raw, MuxMethods.Ping, new MuxEmpty(), MuxJsonContext.Default.MuxEmpty)).Error);
    }

    /// <summary>
    /// Two connections naming one id at once, each past the in-use check while the other is still starting its shell:
    /// exactly one session is published under the id, and the other spawn's shell is ended and the spawn refused.
    /// </summary>
    [Fact]
    public async Task Two_spawns_racing_for_one_id_publish_exactly_one()
    {
        using var host = new MuxTestHost();
        MuxClient a = await host.ConnectClientAsync();
        MuxClient b = await host.ConnectClientAsync();
        using var gate = new ManualResetEventSlim();
        host.Factory.CreateGate = gate;
        Guid id = Guid.NewGuid();

        Task<Guid>[] spawns = [a.SpawnAsync(Scripted, id, Ct), b.SpawnAsync(Scripted, id, Ct)];
        await TestWait.UntilAsync(() => host.Factory.Requests.Count == 2, "both spawns are starting their shells");
        gate.Set();
        await Task.WhenAll(spawns.Select(t => t.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default)))
            .WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(id, Assert.Single(spawns, t => t.IsCompletedSuccessfully).Result);
        Task<Guid> loser = Assert.Single(spawns, t => t.IsFaulted);
        Assert.Equal(MuxErrorCodes.SessionExists, Assert.IsType<MuxProtocolException>(loser.Exception!.InnerException).Code);
        Assert.Equal(new[] { id }, host.Server.GetSessionIds());
        ScriptedTerminalSession kept = host.Fake(id);
        Assert.False(kept.Disposed);
        Assert.Equal(2, host.Factory.Sessions.Count);
        Assert.True(Assert.Single(host.Factory.Sessions, s => !ReferenceEquals(s, kept)).Disposed);
        Assert.True(a.IsConnected);
        Assert.True(b.IsConnected);
    }
}
