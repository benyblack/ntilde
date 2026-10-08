using System.Net;
using System.Net.Sockets;
using Ntilde.Platform.Ssh.Interactions;
using Ntilde.Platform.Ssh.Models;
using Ntilde.Platform.Ssh.Native;
using Ntilde.Platform.Ssh.Sessions;
using Ntilde.Platform.Tests.Infra;
using Ntilde.VT;
using static Ntilde.Platform.Tests.Ssh.NativeSshDockerTestWaits;

namespace Ntilde.Platform.Tests.Ssh;

/// <summary>
/// The jump-host rows of docs/native-ssh/Native_SSH_Test_Matrix.md, against a real sshd.
///
/// Every hop dials the fixture container back into itself: the first hop is the mapped host port,
/// every later address is <see cref="DockerSshFixture.InContainerSshPort"/>, which the hop's sshd
/// resolves on its own loopback. That makes each hop a genuinely separate SSH session — its own
/// TCP-or-tunnel transport, handshake, host-key check and authentication — without the
/// multi-container network fixture the matrix used to say these rows were waiting for.
///
/// The hops authenticate as <see cref="DockerSshFixture.JumpUserName"/> and the target as
/// <see cref="DockerSshFixture.UserName"/>, with different passwords, and the handler answers each
/// prompt with the password of the user it names. Any password sent to the wrong hop therefore fails
/// authentication instead of passing unnoticed, as it did while every hop shared one password.
/// </summary>
public sealed class NativeSshDockerJumpChainE2eTests
{
    private static readonly TimeSpan PromptTimeout = TimeSpan.FromSeconds(30);

    [DockerFact]
    [Trait("Category", "DockerE2E")]
    [Trait("Target", "NativeSsh")]
    public async Task JumpHost_OneHop_ReachesAShellOnTheTarget()
    {
        await using var fixture = await DockerSshFixture.StartAsync();
        await fixture.CreateJumpUserAsync();

        var buffer = new TerminalBuffer(120, 30);
        var parser = new AnsiParser(buffer);
        NativeSshTestInteractionHandler handler = CreatePerHopHandler(fixture);

        SshProfile profile = CreateTunnelledProfile(fixture, hopCount: 1);

        using var session = new NativeSshSession(profile, cols: 120, rows: 30, interactionHandler: handler);
        session.OnOutputReceived += parser.Process;

        await WaitUntilAsync(() => SnapshotContains(buffer, "nova$"), PromptTimeout, "shell prompt through one jump hop");

        // Two distinct servers were verified from the client's point of view: the hop on the mapped
        // port and the target on the container-internal one. Both must have gone through the
        // host-key prompt — a single prompt would mean the "tunnel" was really a direct connect.
        Assert.Equal(
            2,
            handler.RequestSnapshot().Count(request => request.Kind == SshInteractionKind.UnknownHostKey));
        AssertEachServerAskedForItsOwnPassword(fixture, handler, hopCount: 1);
    }

    [DockerFact]
    [Trait("Category", "DockerE2E")]
    [Trait("Target", "NativeSsh")]
    public async Task JumpChain_TwoHops_ReachesALiveShellOnTheTarget()
    {
        await using var fixture = await DockerSshFixture.StartAsync();
        await fixture.CreateJumpUserAsync();

        var buffer = new TerminalBuffer(120, 30);
        var parser = new AnsiParser(buffer);
        NativeSshTestInteractionHandler handler = CreatePerHopHandler(fixture);

        SshProfile profile = CreateTunnelledProfile(fixture, hopCount: 2);

        using var session = new NativeSshSession(profile, cols: 120, rows: 30, interactionHandler: handler);
        session.OnOutputReceived += parser.Process;

        await WaitUntilAsync(() => SnapshotContains(buffer, "nova$"), PromptTimeout, "shell prompt through a two-hop chain");

        // A prompt alone could be buffered output from a session that has since wedged; make the
        // innermost shell compute something so the assertion spans the whole nested pipe both ways.
        session.SendInput("printf 'chain-%s\\n' \"$((6 * 7))\"\n");
        await WaitUntilAsync(() => SnapshotContains(buffer, "chain-42"), PromptTimeout, "command output through the chain");

        // Three sessions, three handshakes, three host-key verdicts.
        Assert.Equal(
            3,
            handler.RequestSnapshot().Count(request => request.Kind == SshInteractionKind.UnknownHostKey));
        AssertEachServerAskedForItsOwnPassword(fixture, handler, hopCount: 2);
    }

    [DockerFact]
    [Trait("Category", "DockerE2E")]
    [Trait("Target", "NativeSsh")]
    public async Task NativeSftp_ThroughAPasswordBastion_SendsEachHopItsOwnPassword()
    {
        // The transfer path cannot prompt; it used to send its one password to every hop. With the
        // bastion's and target's passwords different, only per-hop credentials can complete this.
        await using var fixture = await DockerSshFixture.StartAsync();
        await fixture.CreateJumpUserAsync();

        string tempRoot = Path.Combine(Path.GetTempPath(), $"ntilde-native-sftp-bastion-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            const string remotePath = "/tmp/native-sftp-bastion-source.txt";
            const string expected = "native-sftp-through-a-password-bastion";
            await fixture.WriteTextFileAsync(remotePath, expected);

            // One sshd answers both addresses, so one host key covers both known-hosts entries.
            SshHostKeyInfo hostKey = await fixture.GetHostKeyAsync();
            string knownHostsPath = Path.Combine(tempRoot, "native_known_hosts.json");
            var knownHosts = new NativeKnownHostsStore(knownHostsPath);
            knownHosts.TrustHost(fixture.Host, fixture.Port, hostKey.Algorithm, hostKey.Fingerprint);
            knownHosts.TrustHost("127.0.0.1", fixture.InContainerSshPort, hostKey.Algorithm, hostKey.Fingerprint);

            string localPath = Path.Combine(tempRoot, "downloaded.txt");
            var connection = new NativeSshConnectionOptions
            {
                Host = "127.0.0.1",
                User = fixture.UserName,
                Port = fixture.InContainerSshPort,
                Password = fixture.Password,
                KnownHostsFilePath = knownHostsPath,
                JumpHops =
                [
                    new SshJumpHop { Host = fixture.Host, User = fixture.JumpUserName, Port = fixture.Port }
                ],
                JumpHopPasswords = [fixture.JumpPassword]
            };
            var transfer = new NativeSftpTransferOptions
            {
                Direction = NativeSftpTransferDirection.Download,
                Kind = NativeSftpTransferKind.File,
                RemotePath = remotePath,
                LocalPath = localPath
            };

            new NativeSshInterop().RunSftpTransfer(connection, transfer, progress: null, CancellationToken.None);

            Assert.Equal(expected, File.ReadAllText(localPath));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [DockerFact]
    [Trait("Category", "DockerE2E")]
    [Trait("Target", "NativeSsh")]
    public async Task DynamicForward_ThroughAJumpHop_CarriesRealBytesToTheEchoService()
    {
        await using var fixture = await DockerSshFixture.StartAsync();
        await fixture.CreateJumpUserAsync();

        int localPort = GetFreeLocalPort();
        var buffer = new TerminalBuffer(120, 30);
        var parser = new AnsiParser(buffer);
        NativeSshTestInteractionHandler handler = CreatePerHopHandler(fixture);

        SshProfile profile = CreateTunnelledProfile(fixture, hopCount: 1);
        profile.Forwards.Add(new PortForward
        {
            Kind = PortForwardKind.Dynamic,
            BindAddress = "127.0.0.1",
            SourcePort = localPort
        });

        using var session = new NativeSshSession(profile, cols: 120, rows: 30, interactionHandler: handler);
        session.OnOutputReceived += parser.Process;

        await WaitUntilAsync(() => SnapshotContains(buffer, "nova$"), PromptTimeout, "shell prompt before forwarding through the hop");

        // The forward's direct-tcpip channels ride the target session, which itself rides the
        // hop's tunnel — so these bytes cross two nested SSH transports each way.
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, localPort);
        NetworkStream stream = client.GetStream();

        await Socks5ConnectAsync(stream, "127.0.0.1", fixture.EchoServicePort, TimeSpan.FromSeconds(30));

        const string payload = "socks5-through-a-jump-hop";
        string echoed = await EchoRoundTripAsync(stream, payload, TimeSpan.FromSeconds(30));

        Assert.Equal(payload, echoed);
    }

    /// <summary>
    /// A profile whose first hop is the container's mapped host port and whose remaining hops and
    /// target are the container-internal sshd port, resolved by each hop on its own loopback.
    /// Only the first hop is reachable from the host at all — reaching the prompt proves the
    /// tunnels, not just the addresses.
    /// </summary>
    private static SshProfile CreateTunnelledProfile(DockerSshFixture fixture, int hopCount)
    {
        var profile = new SshProfile
        {
            Id = Guid.NewGuid(),
            Name = $"Docker Native SSH ({hopCount}-hop jump)",
            BackendKind = SshBackendKind.Native,
            Host = "127.0.0.1",
            Port = fixture.InContainerSshPort,
            User = fixture.UserName
        };

        profile.JumpHops.Add(new SshJumpHop
        {
            Host = fixture.Host,
            User = fixture.JumpUserName,
            Port = fixture.Port
        });
        for (int i = 1; i < hopCount; i++)
        {
            profile.JumpHops.Add(new SshJumpHop
            {
                Host = "127.0.0.1",
                User = fixture.JumpUserName,
                Port = fixture.InContainerSshPort
            });
        }

        return profile;
    }

    /// <summary>Answers each password prompt with the password of the user that prompt names.</summary>
    private static NativeSshTestInteractionHandler CreatePerHopHandler(DockerSshFixture fixture)
    {
        return new NativeSshTestInteractionHandler(
            fixture.Password,
            passwordsByUser: new Dictionary<string, string>
            {
                [fixture.JumpUserName] = fixture.JumpPassword,
                [fixture.UserName] = fixture.Password
            });
    }

    /// <summary>
    /// Every hop, then the target, asked for a password — in connect order, each naming its own
    /// server and saying whether it is a jump hop. That identity is what the app keys credential
    /// reuse on, so it is asserted against the real native layer, not only against fakes.
    /// </summary>
    private static void AssertEachServerAskedForItsOwnPassword(
        DockerSshFixture fixture,
        NativeSshTestInteractionHandler handler,
        int hopCount)
    {
        SshInteractionRequest[] passwordPrompts = handler.RequestSnapshot()
            .Where(request => request.Kind == SshInteractionKind.Password)
            .ToArray();

        Assert.Equal(hopCount + 1, passwordPrompts.Length);

        SshInteractionRequest firstHop = passwordPrompts[0];
        Assert.True(firstHop.IsJumpHop);
        Assert.Equal(fixture.Host, firstHop.Host);
        Assert.Equal(fixture.Port, firstHop.Port);
        Assert.Equal(fixture.JumpUserName, firstHop.User);

        foreach (SshInteractionRequest laterHop in passwordPrompts.Skip(1).Take(hopCount - 1))
        {
            Assert.True(laterHop.IsJumpHop);
            Assert.Equal("127.0.0.1", laterHop.Host);
            Assert.Equal(fixture.InContainerSshPort, laterHop.Port);
            Assert.Equal(fixture.JumpUserName, laterHop.User);
        }

        SshInteractionRequest target = passwordPrompts[^1];
        Assert.False(target.IsJumpHop);
        Assert.Equal("127.0.0.1", target.Host);
        Assert.Equal(fixture.InContainerSshPort, target.Port);
        Assert.Equal(fixture.UserName, target.User);
    }
}
