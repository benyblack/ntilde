using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;

namespace Ntilde.Mux.Tests.Server;

public sealed class MuxServerRequestTests
{
    private static async Task<MuxResponse> CallAsync<T>(RawMuxConnection raw, string method, T p, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
    {
        long id = raw.Request(method, p, info);
        MuxResponse response = await raw.ReadResponseAsync();
        Assert.Equal(id, response.Id);
        return response;
    }

    private static async Task<Guid> SpawnAsync(RawMuxConnection raw, SpawnParams? p = null)
    {
        MuxResponse r = await CallAsync(raw, MuxMethods.Spawn, p ?? new SpawnParams { Command = "scripted", Cols = 80, Rows = 24, Title = "one" }, MuxJsonContext.Default.SpawnParams);
        Assert.Null(r.Error);
        return MuxFrames.ParseParams(r.Result, MuxJsonContext.Default.SpawnResult).SessionId;
    }

    [Fact]
    public async Task Spawn_forwards_the_request_and_the_session_is_listed()
    {
        using var host = new MuxTestHost();
        RawMuxConnection raw = host.ConnectRaw();
        await raw.HelloAsync();

        Guid id = await SpawnAsync(raw, new SpawnParams
        {
            Command = "pwsh",
            Arguments = "-NoProfile",
            StartingDirectory = "C:\\w",
            Cols = 100,
            Rows = 30,
            EnvironmentOverrides = new Dictionary<string, string> { ["A"] = "1" },
            SkipPowerShellPostLaunchInit = true,
            Title = "work",
        });
        MuxResponse list = await CallAsync(raw, MuxMethods.ListSessions, new MuxEmpty(), MuxJsonContext.Default.MuxEmpty);

        var request = Assert.Single(host.Factory.Requests);
        Assert.Equal(("pwsh", "-NoProfile", "C:\\w", 100, 30, true), (request.Command, request.Arguments, request.StartingDirectory, request.Cols, request.Rows, request.SkipPowerShellPostLaunchInit));
        Assert.Equal("1", request.EnvironmentOverrides!["A"]);
        Assert.Null(request.Ssh);
        SessionSummary summary = Assert.Single(MuxFrames.ParseParams(list.Result, MuxJsonContext.Default.ListSessionsResult).Sessions);
        Assert.Equal((id, "work", "pwsh", 100, 30, true, 0), (summary.SessionId, summary.Title, summary.Command, summary.Cols, summary.Rows, summary.Running, summary.AttachedClients));
    }

    [Fact]
    public async Task A_failing_spawn_is_reported_and_the_connection_survives()
    {
        using var host = new MuxTestHost();
        RawMuxConnection raw = host.ConnectRaw();
        await raw.HelloAsync();
        host.Factory.FailNext = true;

        MuxResponse r = await CallAsync(raw, MuxMethods.Spawn, new SpawnParams { Command = "x" }, MuxJsonContext.Default.SpawnParams);

        Assert.Equal(MuxErrorCodes.SpawnFailed, r.Error?.Code);
        Assert.Null((await CallAsync(raw, MuxMethods.Ping, new MuxEmpty(), MuxJsonContext.Default.MuxEmpty)).Error);
    }

    [Fact]
    public async Task A_session_without_a_byte_tap_is_refused_and_disposed()
    {
        using var host = new MuxTestHost();
        RawMuxConnection raw = host.ConnectRaw();
        await raw.HelloAsync();
        host.Factory.ProduceSessionsWithoutByteTap = true;

        MuxResponse r = await CallAsync(raw, MuxMethods.Spawn, new SpawnParams { Command = "x" }, MuxJsonContext.Default.SpawnParams);

        Assert.Equal(MuxErrorCodes.SpawnFailed, r.Error?.Code);
        Assert.True(host.Factory.LastNoTapSession!.Disposed);
    }

    [Fact]
    public async Task Unknown_sessions_and_methods_are_request_errors_not_disconnects()
    {
        using var host = new MuxTestHost();
        RawMuxConnection raw = host.ConnectRaw();
        await raw.HelloAsync();
        var nobody = new SessionIdParams { SessionId = Guid.NewGuid() };

        Assert.Equal(MuxErrorCodes.UnknownSession, (await CallAsync(raw, MuxMethods.Kill, nobody, MuxJsonContext.Default.SessionIdParams)).Error?.Code);
        Assert.Equal(MuxErrorCodes.UnknownSession, (await CallAsync(raw, MuxMethods.SessionInfo, nobody, MuxJsonContext.Default.SessionIdParams)).Error?.Code);
        Assert.Equal(MuxErrorCodes.UnknownSession, (await CallAsync(raw, MuxMethods.Attach,
            new AttachParams { SessionId = nobody.SessionId, Presentation = MuxTestHost.DefaultPresentation }, MuxJsonContext.Default.AttachParams)).Error?.Code);
        Assert.Equal(MuxErrorCodes.ProtocolError, (await CallAsync(raw, "noSuchMethod", new MuxEmpty(), MuxJsonContext.Default.MuxEmpty)).Error?.Code);
        Assert.Null((await CallAsync(raw, MuxMethods.Ping, new MuxEmpty(), MuxJsonContext.Default.MuxEmpty)).Error);
    }

    [Fact]
    public async Task SessionInfo_reports_the_probe_and_the_exit()
    {
        using var host = new MuxTestHost();
        RawMuxConnection raw = host.ConnectRaw();
        await raw.HelloAsync();
        Guid id = await SpawnAsync(raw);
        host.Fake(id).HasActiveChildProcesses = true;

        SessionInfoResult running = MuxFrames.ParseParams((await CallAsync(raw, MuxMethods.SessionInfo, new SessionIdParams { SessionId = id }, MuxJsonContext.Default.SessionIdParams)).Result, MuxJsonContext.Default.SessionInfoResult);
        host.Fake(id).Exit(3);
        await host.Mux(id).FlushAsync();
        SessionInfoResult exited = MuxFrames.ParseParams((await CallAsync(raw, MuxMethods.SessionInfo, new SessionIdParams { SessionId = id }, MuxJsonContext.Default.SessionIdParams)).Result, MuxJsonContext.Default.SessionInfoResult);

        Assert.Equal((true, (int?)null, true, (int?)null), (running.Running, running.ExitCode, running.HasActiveChildProcesses, running.Pid));
        Assert.Equal((false, (int?)3, false), (exited.Running, exited.ExitCode, exited.HasActiveChildProcesses));
    }

    [Fact]
    public async Task Kill_disposes_the_child_and_removes_the_session()
    {
        using var host = new MuxTestHost();
        RawMuxConnection raw = host.ConnectRaw();
        await raw.HelloAsync();
        Guid id = await SpawnAsync(raw);
        ScriptedTerminalSession fake = host.Fake(id);

        Assert.Null((await CallAsync(raw, MuxMethods.Kill, new SessionIdParams { SessionId = id }, MuxJsonContext.Default.SessionIdParams)).Error);

        Assert.True(fake.Disposed);
        Assert.DoesNotContain(id, host.Server.GetSessionIds());
    }

    [Fact]
    public async Task A_resize_with_id_zero_gets_no_response()
    {
        using var host = new MuxTestHost();
        RawMuxConnection raw = host.ConnectRaw();
        await raw.HelloAsync();
        Guid id = await SpawnAsync(raw);

        raw.Request(MuxMethods.Resize, new ResizeParams { SessionId = id, Cols = 90, Rows = 20 }, MuxJsonContext.Default.ResizeParams, id: 0);
        long ping = raw.Request(MuxMethods.Ping, new MuxEmpty(), MuxJsonContext.Default.MuxEmpty);

        Assert.Equal(ping, (await raw.ReadResponseAsync()).Id); // the ping reply is the very next frame
        await host.Mux(id).InvokeAsync(() => 0);
        Assert.Equal((90, 20), (host.Mux(id).Cols, host.Mux(id).Rows));
    }

    [Fact]
    public async Task A_session_operation_that_throws_is_a_request_error_not_a_disconnect()
    {
        // One session failing to start recording (an unwritable path) must not close the shared
        // connection - that would disconnect every other pane on the same client.
        using var host = new MuxTestHost();
        RawMuxConnection raw = host.ConnectRaw();
        await raw.HelloAsync();
        Guid id = await SpawnAsync(raw);
        host.Fake(id).ThrowOnStartRecording = true;

        MuxResponse r = await CallAsync(raw, MuxMethods.StartRecording,
            new StartRecordingParams { SessionId = id, Path = "Z:\\nowhere\\rec.nrec" }, MuxJsonContext.Default.StartRecordingParams);

        Assert.Equal(MuxErrorCodes.Internal, r.Error?.Code);
        Assert.Contains("denied", r.Error!.Message, StringComparison.Ordinal);
        Assert.Null((await CallAsync(raw, MuxMethods.Ping, new MuxEmpty(), MuxJsonContext.Default.MuxEmpty)).Error);
    }

    [Fact]
    public async Task A_peer_chosen_flight_budget_is_capped_by_the_server()
    {
        // The budget decides how much output the mux retains in memory; a peer must not be able to
        // pick long.MaxValue and grow the daemon without bound.
        using var host = new MuxTestHost(new MuxServerOptions { MaxFlightRecordingBytes = 4 * 1024 * 1024, ForceConPtyFiltering = false });
        RawMuxConnection raw = host.ConnectRaw();
        await raw.HelloAsync();
        Guid id = await SpawnAsync(raw);

        MuxResponse r = await CallAsync(raw, MuxMethods.EnableFlightRecording,
            new EnableFlightRecordingParams { SessionId = id, MaxBytes = long.MaxValue }, MuxJsonContext.Default.EnableFlightRecordingParams);

        Assert.Null(r.Error);
        Assert.Equal(4 * 1024 * 1024, host.Fake(id).LastFlightBudget);
    }

    [Fact]
    public async Task Input_reaches_the_session_as_the_sent_string()
    {
        using var host = new MuxTestHost();
        RawMuxConnection raw = host.ConnectRaw();
        await raw.HelloAsync();
        Guid id = await SpawnAsync(raw);

        raw.Send(MuxFrames.Input(id, "é\r"u8));
        await CallAsync(raw, MuxMethods.Ping, new MuxEmpty(), MuxJsonContext.Default.MuxEmpty);

        Assert.Equal("é\r", Assert.Single(host.Fake(id).SentInput));
    }
}
