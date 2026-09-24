using System.Text.Json;
using Ntilde.Mux;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Shell.Mux;

namespace Ntilde.Tests.Shell.Mux;

public sealed class MuxCommandTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nmxc" + Guid.NewGuid().ToString("N")[..8]);
    private MuxDaemonHost? _host;

    public void Dispose()
    {
        _host?.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private async Task<Guid> StartDaemonWithOneSessionAsync()
    {
        var server = new MuxServer(new ScriptedSessionFactory(), new MuxServerOptions { ForceConPtyFiltering = false });
        _host = new MuxDaemonHost(server, new MuxDaemonOptions
        {
            Endpoint = MuxDiscovery.GetDefaultEndpoint(_root),
            DescriptorPath = MuxDiscovery.GetDescriptorPath(_root),
            IdleExitAfter = TimeSpan.Zero,
        });
        _host.Start();
        using Stream s = Ntilde.Mux.Transport.MuxEndpointConnector.Connect(MuxDiscovery.GetDefaultEndpoint(_root), TimeSpan.FromSeconds(5));
        using MuxClient c = await MuxClient.ConnectAsync(s, null, Ct);
        return await MuxTestHost.SpawnAsync(c);
    }

    private (int Code, string Out, string Err) Run(params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        int code = MuxCommand.Execute(args, o, e, _root);
        return (code, o.ToString(), e.ToString());
    }

    [Fact] public void Dispatch_matches_only_mux() { Assert.True(MuxCommand.IsSupportedCliMode(["mux", "ls"])); Assert.False(MuxCommand.IsSupportedCliMode(["backup"])); Assert.False(MuxCommand.IsSupportedCliMode([])); }
    [Fact] public void Serve_is_recognised() { Assert.True(MuxCommand.IsServe(["mux", "serve"])); Assert.False(MuxCommand.IsServe(["mux", "ls"])); }
    [Fact] public void Unknown_verb_is_exit_2() => Assert.Equal(2, Run("mux", "frobnicate").Code);
    [Fact] public void Missing_verb_is_exit_2() => Assert.Equal(2, Run("mux").Code);

    [Fact]
    public void Ls_without_a_daemon_is_exit_1_with_a_message()
    {
        var (code, _, err) = Run("mux", "ls");
        Assert.Equal(1, code);
        Assert.Contains("No multiplexer is running", err);
    }

    [Fact]
    public async Task Ls_lists_the_running_session()
    {
        Guid id = await StartDaemonWithOneSessionAsync();
        var (code, output, _) = Run("mux", "ls");
        Assert.Equal(0, code);
        Assert.Contains(id.ToString(), output);
        Assert.Contains("running", output);
    }

    [Fact]
    public async Task Ls_json_is_a_ListSessionsResult()
    {
        Guid id = await StartDaemonWithOneSessionAsync();
        var (code, output, _) = Run("mux", "ls", "--json");
        Assert.Equal(0, code);
        ListSessionsResult r = JsonSerializer.Deserialize(output, MuxJsonContext.Default.ListSessionsResult)!;
        Assert.Contains(r.Sessions, s => s.SessionId == id);
    }

    [Fact]
    public async Task Kill_unknown_session_is_exit_1_and_known_is_0()
    {
        Guid id = await StartDaemonWithOneSessionAsync();
        Assert.Equal(1, Run("mux", "kill", Guid.NewGuid().ToString()).Code);
        Assert.Equal(2, Run("mux", "kill", "not-a-guid").Code);
        Assert.Equal(0, Run("mux", "kill", id.ToString()).Code);
    }

    [Fact]
    public async Task Kill_server_stops_the_daemon()
    {
        await StartDaemonWithOneSessionAsync();
        Assert.Equal(0, Run("mux", "kill-server").Code);
        Assert.Equal("shutdown", await _host!.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct));
    }

    [Theory]
    [InlineData(new[] { "mux", "serve" }, 10, false)]
    [InlineData(new[] { "mux", "serve", "--idle-exit-minutes", "0" }, 0, false)]
    [InlineData(new[] { "mux", "serve", "--foreground", "--idle-exit-minutes", "3" }, 3, true)]
    public void Serve_arguments_parse(string[] args, int minutes, bool foreground)
    {
        Assert.True(MuxCommand.TryParseServe(args, out MuxServeOptions o, out _));
        Assert.Equal(TimeSpan.FromMinutes(minutes), o.IdleExitAfter);
        Assert.Equal(foreground, o.Foreground);
    }

    [Theory]
    [InlineData("--idle-exit-minutes")]
    [InlineData("--idle-exit-minutes", "-1")]
    [InlineData("--bogus")]
    public void Bad_serve_arguments_are_rejected(params string[] extra)
    {
        Assert.False(MuxCommand.TryParseServe(["mux", "serve", .. extra], out _, out string? error));
        Assert.NotNull(error);
    }
}
