using System.Net.Sockets;
using System.Runtime.Versioning;
using Ntilde.Mux.Transport;

namespace Ntilde.Mux.Tests.Transport;

// Every test here Assert.SkipWhen(OperatingSystem.IsWindows())s at runtime, but the platform-compat
// analyzer does not treat that as a guard clause. Attributing the class as Windows-unsupported tells
// the analyzer this type's own platform requirement already matches UnixSocketMuxListener's / the
// UnixFileMode APIs', so calling them needs no further guard - narrower than suppressing CA1416
// project-wide.
[UnsupportedOSPlatform("windows")]
public sealed class UnixSocketMuxListenerTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    // Short base: macOS sun_path is 104 bytes and TMPDIR there is already long.
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nmx" + Guid.NewGuid().ToString("N")[..8]);
    private string SocketPath => Path.Combine(_dir, "m.sock");

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    [Fact]
    public async Task Round_trips_bytes_and_creates_0700_dir_and_0600_socket()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix sockets are the Linux/macOS transport.");
        using var listener = new UnixSocketMuxListener(SocketPath);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(_dir));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(SocketPath));

        Task<Stream?> accept = Task.Run(() => listener.Accept(Ct), Ct);
        using Stream client = MuxEndpointConnector.Connect(SocketPath, TimeSpan.FromSeconds(5));
        using Stream server = (await accept.WaitAsync(TimeSpan.FromSeconds(5), Ct))!;
        await client.WriteAsync(new byte[] { 7 }, Ct);
        byte[] got = new byte[1];
        await server.ReadExactlyAsync(got, Ct);
        Assert.Equal(7, got[0]);
    }

    [Fact]
    public void Refuses_a_directory_that_exists_with_a_wider_mode()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix sockets are the Linux/macOS transport.");
        Directory.CreateDirectory(_dir);
        File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        var ex = Assert.Throws<MuxEndpointSecurityException>(() => new UnixSocketMuxListener(SocketPath));
        Assert.Contains(_dir, ex.Message);
    }

    [Fact]
    public void Replaces_a_stale_socket_file_nobody_listens_on()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix sockets are the Linux/macOS transport.");
        using (new UnixSocketMuxListener(SocketPath)) { } // leaves the 0700 directory, and no socket
        // A stale socket is what a crashed (SIGKILLed) daemon leaves: a socket inode at the path with
        // nothing bound to it any more. Disposing a bound .NET Socket unlinks the path it bound, so
        // bind a throwaway socket at a side path, rename its inode onto SocketPath, then dispose it:
        // the dispose unlinks the (now empty) side path, and SocketPath keeps a real socket file
        // whose owner is gone - connecting to it is refused, exactly as after a crash.
        string sidePath = Path.Combine(_dir, "d.sock");
        using (var dead = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            dead.Bind(new UnixDomainSocketEndPoint(sidePath));
            File.Move(sidePath, SocketPath, overwrite: true); // rename(2): same inode, new name
        }

        Assert.True(File.Exists(SocketPath), "setup: the stale socket file should outlive its socket");
        Assert.False(UnixSocketMuxListener.IsAlive(SocketPath), "setup: nothing may be listening on the stale socket");

        using var listener = new UnixSocketMuxListener(SocketPath); // must not throw
        Assert.True(UnixSocketMuxListener.IsAlive(SocketPath), "the replacement listener should accept connections");
    }

    [Fact]
    public void Refuses_to_start_over_a_live_socket()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix sockets are the Linux/macOS transport.");
        using var first = new UnixSocketMuxListener(SocketPath);
        Assert.Throws<IOException>(() => new UnixSocketMuxListener(SocketPath));
    }

    /// <summary>
    /// PR #489 review 2, item 3: a bind failure is a SocketException, which is not an IOException -
    /// escaping as itself it crashed `mux serve`. Something that is not a socket file occupying the
    /// path (here a directory, which the stale-socket check does not touch) makes bind fail.
    /// </summary>
    [Fact]
    public void A_bind_failure_is_reported_as_IOException()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix sockets are the Linux/macOS transport.");
        using (new UnixSocketMuxListener(SocketPath)) { } // the 0700 directory
        Directory.CreateDirectory(SocketPath);

        var ex = Assert.Throws<IOException>(() => new UnixSocketMuxListener(SocketPath));
        Assert.IsType<SocketException>(ex.InnerException);
    }

    [Fact]
    public void Connecting_to_a_socket_nobody_listens_on_throws_IOException()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix sockets are the Linux/macOS transport.");
        Directory.CreateDirectory(_dir);
        Assert.Throws<IOException>(() => MuxEndpointConnector.Connect(SocketPath, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Dispose_unblocks_accept_and_unlinks_the_socket()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix sockets are the Linux/macOS transport.");
        var listener = new UnixSocketMuxListener(SocketPath);
        Task<Stream?> accept = Task.Run(() => listener.Accept(Ct), Ct);
        await Task.Delay(100, Ct);
        listener.Dispose();
        Assert.Null(await accept.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.False(File.Exists(SocketPath));
    }
}
