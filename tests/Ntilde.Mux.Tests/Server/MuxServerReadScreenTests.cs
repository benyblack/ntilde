using System.Text.Json.Serialization.Metadata;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Replay;
using Ntilde.VT;

namespace Ntilde.Mux.Tests.Server;

/// <summary>
/// Phase 5 Task 11: <c>readScreen</c> gives a caller that never attached (the agent host's windowless sessions)
/// the headless screen and the session's status. Every refusal is a request error with its own code; none of them
/// is <c>protocol_error</c>, which the client reads as "this daemon does not know readScreen".
/// </summary>
public sealed class MuxServerReadScreenTests
{
    private static async Task<MuxResponse> CallAsync<T>(RawMuxConnection raw, string method, T p, JsonTypeInfo<T> info)
    {
        long id = raw.Request(method, p, info);
        MuxResponse response = await raw.ReadResponseAsync();
        Assert.Equal(id, response.Id);
        return response;
    }

    private static Task<MuxResponse> ReadScreenAsync(RawMuxConnection raw, Guid sessionId, int maxScrollbackRows) =>
        CallAsync(raw, MuxMethods.ReadScreen, new ReadScreenParams { SessionId = sessionId, MaxScrollbackRows = maxScrollbackRows },
            MuxJsonContext.Default.ReadScreenParams);

    private static ReadScreenResult Result(MuxResponse response)
    {
        Assert.Null(response.Error);
        return MuxFrames.ParseParams(response.Result, MuxJsonContext.Default.ReadScreenResult);
    }

    private static async Task<TerminalStateSnapshot> SnapshotAsync(RawMuxConnection raw, Guid sessionId, int maxScrollbackRows) =>
        TerminalStateSerializer.FromBytes(Result(await ReadScreenAsync(raw, sessionId, maxScrollbackRows)).Snapshot);

    private static async Task<Guid> SpawnAsync(RawMuxConnection raw)
    {
        MuxResponse r = await CallAsync(raw, MuxMethods.Spawn, new SpawnParams { Command = "scripted", Cols = 80, Rows = 24, Title = "one" }, MuxJsonContext.Default.SpawnParams);
        Assert.Null(r.Error);
        return MuxFrames.ParseParams(r.Result, MuxJsonContext.Default.SpawnResult).SessionId;
    }

    private static async Task PingAsync(RawMuxConnection raw) =>
        Assert.Null((await CallAsync(raw, MuxMethods.Ping, new MuxEmpty(), MuxJsonContext.Default.MuxEmpty)).Error);

    private static string Lines(int count) => string.Concat(Enumerable.Range(0, count).Select(i => $"line {i}\r\n"));

    private static string[] ScreenLines(TerminalStateSnapshot snapshot)
    {
        var buffer = new TerminalBuffer(snapshot.Cols, snapshot.Rows);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { ImageDecoder = null, AllowNativeKittyGraphics = false };
        TerminalStateTransfer.Restore(buffer, parser, snapshot);
        return BufferSnapshot.Capture(buffer).Lines;
    }

    [Fact]
    public async Task ReadScreen_returns_the_headless_screen_and_the_sessions_status()
    {
        using var host = new MuxTestHost();
        RawMuxConnection raw = host.ConnectRaw();
        await raw.HelloAsync(1, 2);
        Guid id = await SpawnAsync(raw);
        host.Fake(id).HasActiveChildProcesses = true;

        ReadScreenResult before = Result(await ReadScreenAsync(raw, id, 0));
        host.Fake(id).Emit("hello");
        await host.Mux(id).FlushAsync();
        ReadScreenResult r = Result(await ReadScreenAsync(raw, id, 0));
        TerminalStateSnapshot snapshot = TerminalStateSerializer.FromBytes(r.Snapshot);

        Assert.Null(before.LastOutputUnixMs); // no output yet
        Assert.StartsWith("hello", ScreenLines(snapshot)[0], StringComparison.Ordinal);
        Assert.Equal((80, 24, 0, 5L), (snapshot.Cols, snapshot.Rows, snapshot.ScrollbackRowCount, snapshot.StreamSeq));
        Assert.Equal((true, (int?)null, true), (r.Running, r.ExitCode, r.HasActiveChildProcesses));
        Assert.Equal((0, (int?)0, "one", (string?)null), (r.AttachedClients, r.InteractiveClients, r.Title, r.Cwd));
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Assert.InRange(Assert.NotNull(r.LastOutputUnixMs), now - 10_000, now + 10_000);
        Assert.Equal(0, host.Mux(id).AttachedClients); // a read subscribes nothing
    }

    [Fact]
    public async Task A_v1_peer_is_not_told_the_interactive_count()
    {
        using var host = new MuxTestHost();
        RawMuxConnection raw = host.ConnectRaw();
        await raw.HelloAsync(1, 1);
        Guid id = await SpawnAsync(raw);

        ReadScreenResult r = Result(await ReadScreenAsync(raw, id, 0));

        Assert.Null(r.InteractiveClients); // as sessionInfo: a v2 figure
        Assert.Equal(0, r.AttachedClients);
    }

    [Fact]
    public async Task A_negative_row_count_is_read_as_zero()
    {
        using var host = new MuxTestHost();
        RawMuxConnection raw = host.ConnectRaw();
        await raw.HelloAsync();
        Guid id = await SpawnAsync(raw);
        host.Fake(id).Emit(Lines(40));
        await host.Mux(id).FlushAsync();

        TerminalStateSnapshot negative = await SnapshotAsync(raw, id, -5);
        TerminalStateSnapshot some = await SnapshotAsync(raw, id, 100);

        Assert.Equal(0, negative.ScrollbackRowCount);
        Assert.True(some.ScrollbackRowCount > 0, "there was scrollback to leave out");
    }

    [Fact]
    public async Task A_huge_row_count_is_clamped_to_the_limit_not_refused()
    {
        using var host = new MuxTestHost();
        RawMuxConnection raw = host.ConnectRaw();
        await raw.HelloAsync();
        Guid id = await SpawnAsync(raw);
        host.Fake(id).Emit(Lines(3000));
        await host.Mux(id).FlushAsync();

        TerminalStateSnapshot snapshot = await SnapshotAsync(raw, id, 1_000_000);

        Assert.Equal(MuxReadScreenLimits.MaxScrollbackRows, snapshot.ScrollbackRowCount);
        Assert.Equal(2000, MuxReadScreenLimits.MaxScrollbackRows);
    }

    [Fact]
    public async Task An_unknown_session_is_unknown_session_and_the_connection_carries_on()
    {
        using var host = new MuxTestHost();
        RawMuxConnection raw = host.ConnectRaw();
        await raw.HelloAsync();

        MuxResponse r = await ReadScreenAsync(raw, Guid.NewGuid(), 0);

        Assert.Equal(MuxErrorCodes.UnknownSession, r.Error?.Code);
        await PingAsync(raw);
    }

    [Fact]
    public async Task An_oversized_screen_is_snapshot_too_large_and_the_connection_carries_on()
    {
        using var host = new MuxTestHost(new MuxServerOptions { ForceConPtyFiltering = false, MaxReadScreenBytes = 1024 });
        RawMuxConnection raw = host.ConnectRaw();
        await raw.HelloAsync();
        Guid id = await SpawnAsync(raw);

        MuxResponse r = await ReadScreenAsync(raw, id, 0);

        Assert.Equal(MuxErrorCodes.SnapshotTooLarge, r.Error?.Code);
        await PingAsync(raw);
        Assert.Equal(4 * 1024 * 1024, new MuxServerOptions().MaxReadScreenBytes);
        Assert.Equal(MuxReadScreenLimits.MaxSnapshotBytes, new MuxServerOptions().MaxReadScreenBytes);
    }

    [Fact]
    public async Task An_exited_unreaped_session_answers_with_its_last_screen()
    {
        using var host = new MuxTestHost();
        RawMuxConnection raw = host.ConnectRaw();
        await raw.HelloAsync();
        Guid id = await SpawnAsync(raw);
        host.Fake(id).HasActiveChildProcesses = true; // the probe is not asked once the session has exited
        host.Fake(id).Emit("bye");
        host.Fake(id).Exit(3);
        await host.Mux(id).FlushAsync();

        ReadScreenResult r = Result(await ReadScreenAsync(raw, id, 0));

        Assert.StartsWith("bye", ScreenLines(TerminalStateSerializer.FromBytes(r.Snapshot))[0], StringComparison.Ordinal);
        Assert.Equal((false, (int?)3, false), (r.Running, r.ExitCode, r.HasActiveChildProcesses));
    }

    [Fact]
    public async Task A_faulted_session_is_internal_error()
    {
        using var host = new MuxTestHost();
        RawMuxConnection raw = host.ConnectRaw();
        await raw.HelloAsync();
        Guid id = await SpawnAsync(raw);
        await host.Mux(id).MakeParserThrowOnReplyAsync();
        host.Fake(id).Emit("\x1b[c");
        await host.Mux(id).FlushAsync();
        Assert.True(host.Mux(id).IsFaulted);

        MuxResponse r = await ReadScreenAsync(raw, id, 0);

        Assert.Equal(MuxErrorCodes.Internal, r.Error?.Code);
        await PingAsync(raw);
    }

    [Fact]
    public async Task A_read_the_stopped_session_never_runs_is_session_exited()
    {
        using var host = new MuxTestHost();
        RawMuxConnection raw = host.ConnectRaw();
        await raw.HelloAsync();
        Guid id = await SpawnAsync(raw);

        // Stopped behind the server's back, so the server still lists it: the work item is dropped, not run.
        HeadlessTerminalSession mux = host.Mux(id);
        mux.Kill();
        await TestWait.UntilAsync(() => !mux.IsParseThreadAliveForTest, "the parse thread stopped");

        MuxResponse r = await ReadScreenAsync(raw, id, 0);

        Assert.Equal(MuxErrorCodes.SessionExited, r.Error?.Code);
        await PingAsync(raw);
    }
}
