using System.Net.Sockets;
using Ntilde.Mux.Daemon;

namespace Ntilde.Mux.Tests.Daemon;

/// <summary>The stable agent socket link remote shells use (Phase 5 task 8). Unix only: Windows skips.</summary>
public sealed class AgentSocketLinkTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nasl" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<Socket> _sockets = new();

    public AgentSocketLinkTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (Socket s in _sockets) s.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Listen(string name)
    {
        string path = Path.Combine(_dir, name);
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(path));
        socket.Listen(4);
        _sockets.Add(socket);
        return path;
    }

    private static void SkipOnWindows() => Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix sockets and symlinks only.");

    [Fact]
    public void PathFor_is_agent_sock_in_the_endpoint_directory()
    {
        Assert.Equal(Path.Combine("/run/x", "agent.sock"), AgentSocketLink.PathFor("/run/x"));
    }

    [Fact]
    public void TryRepoint_follows_the_latest_live_socket()
    {
        SkipOnWindows();
        string a = Listen("a.sock");
        string b = Listen("b.sock");
        string link = AgentSocketLink.PathFor(_dir);

        Assert.True(AgentSocketLink.TryRepoint(link, a));
        Assert.Equal(a, new FileInfo(link).LinkTarget);

        Assert.True(AgentSocketLink.TryRepoint(link, b));
        Assert.Equal(b, new FileInfo(link).LinkTarget);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void TryRepoint_refuses_a_regular_file_and_leaves_the_link()
    {
        SkipOnWindows();
        string a = Listen("a.sock");
        string link = AgentSocketLink.PathFor(_dir);
        Assert.True(AgentSocketLink.TryRepoint(link, a));
        string file = Path.Combine(_dir, "plain");
        File.WriteAllText(file, "x");

        Assert.False(AgentSocketLink.TryRepoint(link, file));

        Assert.Equal(a, new FileInfo(link).LinkTarget);
    }

    [Fact]
    public void TryRepoint_refuses_a_stale_socket_file_nobody_listens_on()
    {
        SkipOnWindows();
        string stale = Path.Combine(_dir, "stale.sock");
        using (var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            s.Bind(new UnixDomainSocketEndPoint(stale)); // never listens, then closes: the file stays
        }

        Assert.False(AgentSocketLink.TryRepoint(AgentSocketLink.PathFor(_dir), stale));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative.sock")]
    [InlineData("/no/such/socket")]
    public void TryRepoint_refuses_an_unset_or_invalid_target_and_keeps_the_last_good_link(string? target)
    {
        SkipOnWindows();
        string a = Listen("a.sock");
        string link = AgentSocketLink.PathFor(_dir);
        Assert.True(AgentSocketLink.TryRepoint(link, a));

        Assert.False(AgentSocketLink.TryRepoint(link, target));

        Assert.Equal(a, new FileInfo(link).LinkTarget);
    }

    [Fact]
    public void A_probe_against_a_full_backlog_returns_promptly_as_not_live()
    {
        SkipOnWindows();
        string path = Path.Combine(_dir, "hung.sock");
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(0);
        _sockets.Add(listener);
        // Fill the backlog with connects nobody accepts.
        for (int i = 0; i < 4; i++)
        {
            var filler = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified) { Blocking = false };
            _sockets.Add(filler);
            try { filler.Connect(new UnixDomainSocketEndPoint(path)); } catch (SocketException) { }
        }

        Task<bool> probe = Task.Run(() => AgentSocketLink.IsLiveSocket(path), TestContext.Current.CancellationToken);

        Assert.True(probe.Wait(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken), "the probe blocked on a full backlog");
        Assert.False(probe.Result);
    }

    [Fact]
    public void TryRepoint_refuses_a_socket_whose_listener_is_another_user()
    {
        SkipOnWindows();
        string a = Listen("a.sock");
        string link = AgentSocketLink.PathFor(_dir);

        Assert.False(AgentSocketLink.TryRepoint(link, a, () => uint.MaxValue - 1));
        Assert.False(new FileInfo(link).Exists || new FileInfo(link).LinkTarget is not null);
        Assert.False(AgentSocketLink.TryRepoint(link, a, () => null), "an euid that cannot be read fails closed");
        Assert.True(AgentSocketLink.TryRepoint(link, a));
    }

    [Fact]
    public void LinkPathForEndpoint_is_beside_the_endpoint_socket()
    {
        SkipOnWindows();
        Assert.Equal(Path.Combine(_dir, "agent.sock"), AgentSocketLink.LinkPathForEndpoint(Path.Combine(_dir, "mux.sock")));
    }

    [Fact]
    public void LinkPathForEndpoint_skips_and_logs_at_most_once_when_the_link_path_would_not_fit_a_socket_path()
    {
        SkipOnWindows();
        // mux.sock exactly at the budget is a legal endpoint; agent.sock, 2 bytes longer, does not fit.
        int budget = OperatingSystem.IsMacOS() ? 103 : 107;
        string endpoint = "/" + new string('d', budget - "/mux.sock".Length - 1) + "/mux.sock";
        Assert.Equal(budget, System.Text.Encoding.UTF8.GetByteCount(endpoint));
        var log = new List<string>();

        Assert.Null(AgentSocketLink.LinkPathForEndpoint(endpoint, log.Add));
        Assert.Null(AgentSocketLink.LinkPathForEndpoint(endpoint, log.Add));

        Assert.True(log.Count <= 1, "the process-wide once-flag allows one line, not two");
    }
}
