using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Ntilde.Mux.Transport;

namespace Ntilde.Mux.Tests.Transport;

// Every test here Assert.SkipUnless(OperatingSystem.IsWindows())s at runtime, but the platform-compat
// analyzer does not treat that as a guard clause. Attributing the class as Windows-only tells the
// analyzer this type's own platform requirement already matches NamedPipeMuxListener's, so calling it
// needs no further guard - narrower than suppressing CA1416 project-wide.
[SupportedOSPlatform("windows")]
public sealed class NamedPipeMuxListenerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string UniqueName() => "ntilde-mux-test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task A_client_round_trips_bytes_through_an_accepted_pipe()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Named pipes are the Windows transport.");
        string name = UniqueName();
        using var listener = new NamedPipeMuxListener(name);
        Task<Stream?> accept = Task.Run(() => listener.Accept(Ct), Ct);

        using Stream client = MuxEndpointConnector.Connect(name, TimeSpan.FromSeconds(5));
        using Stream server = (await accept.WaitAsync(TimeSpan.FromSeconds(5), Ct))!;
        await client.WriteAsync(new byte[] { 1, 2, 3 }, Ct);
        byte[] got = new byte[3];
        await server.ReadExactlyAsync(got, Ct);
        Assert.Equal(new byte[] { 1, 2, 3 }, got);
    }

    [Fact]
    public async Task Two_clients_can_be_accepted_back_to_back()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Named pipes are the Windows transport.");
        string name = UniqueName();
        using var listener = new NamedPipeMuxListener(name);
        for (int i = 0; i < 2; i++)
        {
            Task<Stream?> accept = Task.Run(() => listener.Accept(Ct), Ct);
            using Stream client = MuxEndpointConnector.Connect(name, TimeSpan.FromSeconds(5));
            using Stream? server = await accept.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            Assert.NotNull(server);
        }
    }

    [Fact]
    public async Task Dispose_unblocks_a_pending_accept_with_null()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Named pipes are the Windows transport.");
        var listener = new NamedPipeMuxListener(UniqueName());
        Task<Stream?> accept = Task.Run(() => listener.Accept(Ct), Ct);
        await Task.Delay(100, Ct);
        listener.Dispose();
        Assert.Null(await accept.WaitAsync(TimeSpan.FromSeconds(5), Ct));
    }

    [Fact]
    public void Connecting_to_a_pipe_nobody_serves_times_out()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Named pipes are the Windows transport.");
        // Verified (not guessed): NamedPipeClientStream.Connect(int) throws TimeoutException, with
        // message "The operation has timed out.", when nothing serves the pipe within the timeout.
        Assert.Throws<TimeoutException>(() => MuxEndpointConnector.Connect(UniqueName(), TimeSpan.FromMilliseconds(300)));
    }

    /// <summary>
    /// PR #489 review 2, item 10: the old test only proved a same-user client could connect. This
    /// reads the pipe's DACL through a connected client handle and requires every access rule to
    /// name the current user - the default DACL would also grant Everyone/Anonymous read and
    /// Administrators/SYSTEM full control.
    /// </summary>
    [Fact]
    public async Task The_pipe_acl_grants_access_to_the_current_user_only()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Named pipes are the Windows transport.");
        string name = UniqueName();
        using var listener = new NamedPipeMuxListener(name);
        Task<Stream?> accept = Task.Run(() => listener.Accept(Ct), Ct);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.None);
        client.Connect(5000);
        using Stream? server = await accept.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        using WindowsIdentity me = WindowsIdentity.GetCurrent();
        PipeSecurity security = client.GetAccessControl();
        AuthorizationRuleCollection rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        Assert.NotEmpty(rules);
        foreach (PipeAccessRule rule in rules)
        {
            Assert.Equal(me.User, rule.IdentityReference);
        }
    }

    /// <summary>
    /// PR #489 review 2, item 8: the first instance is created FirstPipeInstance, so a name some
    /// other process already serves fails the start (reported as "in use") instead of mixing that
    /// process's instances with ours.
    /// </summary>
    [Fact]
    public void A_second_listener_on_the_same_name_is_refused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Named pipes are the Windows transport.");
        string name = UniqueName();
        using var first = new NamedPipeMuxListener(name);
        Assert.Throws<IOException>(() => new NamedPipeMuxListener(name).Dispose());
    }
}
