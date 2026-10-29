using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Ntilde.Platform.Ssh.Exec;
using Ntilde.Platform.Ssh.Interactions;
using Ntilde.Platform.Ssh.Models;
using Ntilde.Platform.Ssh.Native;
using Ntilde.Platform.Ssh.Sessions;

namespace Ntilde.Platform.Tests.Ssh;

public sealed class NativeSshConnectionPoolTests
{
    [Fact]
    public async Task TabsOfAMultiplexedProfile_ShareOneConnection()
    {
        var interop = new SharingInterop();
        var pool = new NativeSshConnectionPool();
        SshProfile profile = CreateProfile();

        using NativeSshSession first = new(profile, interop: interop, connectionPool: pool);
        using NativeSshSession second = new(profile, interop: interop, connectionPool: pool);

        Assert.Equal(1, interop.SharedConnectCount);
        Assert.Equal(0, interop.PlainConnectCount);
        Assert.Equal(2, interop.OpenedShells.Count);
        Assert.All(interop.OpenedShells, shell => Assert.Equal(interop.Connections.Single(), shell.Connection));
        Assert.Equal(1, pool.Count);

        // Each tab polls its own shell.
        var output = new ConcurrentQueue<string>();
        second.OnOutputReceived += output.Enqueue;
        interop.Enqueue(interop.OpenedShells[1].Handle, NativeSshEvent.Data("two"u8.ToArray()));
        await WaitUntilAsync(() => string.Concat(output) == "two");

        first.SendInput("ls\n");
        Assert.Equal(interop.OpenedShells[0].Handle, interop.Writes.Single().Handle);
    }

    [Fact]
    public void ProfileWithoutMultiplexing_ConnectsPerTab()
    {
        var interop = new SharingInterop();
        var pool = new NativeSshConnectionPool();
        SshProfile profile = CreateProfile();
        profile.MuxOptions.Enabled = false;

        using NativeSshSession first = new(profile, interop: interop, connectionPool: pool);
        using NativeSshSession second = new(profile, interop: interop, connectionPool: pool);

        Assert.Equal(2, interop.PlainConnectCount);
        Assert.Equal(0, interop.SharedConnectCount);
        Assert.Equal(0, pool.Count);
    }

    [Fact]
    public void MultiplexingWithoutControlMasterAuto_ConnectsPerTabAndSaysSo()
    {
        var interop = new SharingInterop();
        var pool = new NativeSshConnectionPool();
        SshProfile profile = CreateProfile();
        profile.MuxOptions.ControlMasterAuto = false;

        using NativeSshSession session = new(profile, interop: interop, connectionPool: pool);
        var output = new List<string>();
        session.OnOutputReceived += output.Add;

        Assert.Equal(1, interop.PlainConnectCount);
        Assert.Equal(0, interop.SharedConnectCount);
        Assert.Contains("ControlMaster auto", string.Concat(output), StringComparison.Ordinal);
    }

    [Fact]
    public void MultiplexedProfile_NoLongerWarnsThatNativeIgnoresIt()
    {
        var interop = new SharingInterop();
        using NativeSshSession session = new(CreateProfile(), interop: interop, connectionPool: new NativeSshConnectionPool());
        var output = new List<string>();
        session.OnOutputReceived += output.Add;

        Assert.DoesNotContain("Warning", string.Concat(output), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LastTabClosing_ClosesTheConnection_WhenControlPersistIsZero()
    {
        var interop = new SharingInterop();
        var pool = new NativeSshConnectionPool();
        SshProfile profile = CreateProfile();

        NativeSshSession first = new(profile, interop: interop, connectionPool: pool);
        NativeSshSession second = new(profile, interop: interop, connectionPool: pool);
        interop.Enqueue(interop.Connections.Single(), Connected());
        await WaitUntilAsync(() => interop.ConnectedSeen);

        first.Dispose();
        Assert.DoesNotContain(interop.Connections.Single(), interop.ClosedHandles);
        Assert.Equal(1, pool.Count);

        second.Dispose();
        Assert.Contains(interop.Connections.Single(), interop.ClosedHandles);
        Assert.Equal(0, pool.Count);

        // The next tab connects afresh.
        using NativeSshSession third = new(profile, interop: interop, connectionPool: pool);
        Assert.Equal(2, interop.SharedConnectCount);
    }

    [Fact]
    public async Task ControlPersist_KeepsTheConnectionForATabThatComesBack_ThenClosesIt()
    {
        var interop = new SharingInterop();
        var pool = new NativeSshConnectionPool();
        SshProfile profile = CreateProfile();
        profile.MuxOptions.ControlPersistSeconds = 1;

        NativeSshSession first = new(profile, interop: interop, connectionPool: pool);
        interop.Enqueue(interop.Connections.Single(), Connected());
        await WaitUntilAsync(() => interop.ConnectedSeen);
        first.Dispose();

        NativeSshSession second = new(profile, interop: interop, connectionPool: pool);
        Assert.Equal(1, interop.SharedConnectCount);
        Assert.Empty(interop.ClosedHandles.Where(handle => handle == interop.Connections.Single()));

        second.Dispose();
        await WaitUntilAsync(() => interop.ClosedHandles.Contains(interop.Connections.Single()), TimeSpan.FromSeconds(5));
        Assert.Equal(0, pool.Count);
    }

    [Fact]
    public void ATabThatLeavesBeforeTheLoginFinishes_DoesNotKeepTheConnection()
    {
        var interop = new SharingInterop();
        var pool = new NativeSshConnectionPool();
        SshProfile profile = CreateProfile();
        profile.MuxOptions.ControlPersistSeconds = 600;

        NativeSshSession session = new(profile, interop: interop, connectionPool: pool);
        session.Dispose();

        Assert.Contains(interop.Connections.Single(), interop.ClosedHandles);
        Assert.Equal(0, pool.Count);
    }

    [Fact]
    public void DifferentConnectionSettings_GetDifferentConnections()
    {
        var interop = new SharingInterop();
        var pool = new NativeSshConnectionPool();
        SshProfile profile = CreateProfile();

        using NativeSshSession first = new(profile, interop: interop, connectionPool: pool);
        profile.Port = 2222;
        using NativeSshSession second = new(profile, interop: interop, connectionPool: pool);
        SshProfile other = CreateProfile();
        other.Id = Guid.NewGuid();
        using NativeSshSession third = new(other, interop: interop, connectionPool: pool);

        Assert.Equal(3, interop.SharedConnectCount);
        Assert.Equal(3, pool.Count);
    }

    [Fact]
    public void ForwardsBelongToTheConnection_SoASecondTabDoesNotBindThemAgain()
    {
        // Before connection sharing, the second native tab of a profile with a forward failed here: its own
        // forward session could not bind the port the first tab's already held.
        // Port 0, and the connection is asked about its listener: a port picked free and bound later can be taken
        // in between, and then the forward is skipped (continueOnBindFailure) and the port reads as free (#581).
        var interop = new SharingInterop();
        var pool = new NativeSshConnectionPool();
        SshProfile profile = CreateProfile();
        profile.Forwards.Add(new PortForward
        {
            Kind = PortForwardKind.Dynamic,
            BindAddress = "127.0.0.1",
            SourcePort = 0
        });

        NativeSshSession first = new(profile, interop: interop, connectionPool: pool);
        NativeSshSession second = new(profile, interop: interop, connectionPool: pool);
        NativeSshSharedConnection connection = Assert.IsType<NativeSshSharedConnection>(first.SharedConnectionForTest);
        Assert.Same(connection, second.SharedConnectionForTest);
        Assert.Single(connection.GetForwardListenerEndpoints());

        // The listener outlives the tab that opened the connection.
        first.Dispose();
        Assert.True(connection.HasBoundForwardListener(), "the forward's listener should still be up");

        second.Dispose();
        Assert.False(connection.HasBoundForwardListener(), "the forward's listener should close with the connection");
    }

    [Fact]
    public async Task ConnectionPrompts_AreAnsweredOnTheConnectionHandle()
    {
        var interop = new SharingInterop();
        var handler = new NativeSshTestInteractionHandler("secret");
        using NativeSshSession session = new(
            CreateProfile(),
            interop: interop,
            interactionHandler: handler,
            connectionPool: new NativeSshConnectionPool());

        interop.Enqueue(
            interop.Connections.Single(),
            new NativeSshEvent(NativeSshEventKind.PasswordPrompt, """{"host":"native.example","port":22,"user":"nova","prompt":"Password:"}"""u8.ToArray()));

        await WaitUntilAsync(() => !interop.Responses.IsEmpty);
        (nint handle, NativeSshResponseKind kind) = interop.Responses.Single();
        Assert.Equal(interop.Connections.Single(), handle);
        Assert.Equal(NativeSshResponseKind.Password, kind);
    }

    [Fact]
    public async Task AConnectionThatEnded_IsReplacedForTheNextTab()
    {
        var interop = new SharingInterop();
        var pool = new NativeSshConnectionPool();
        SshProfile profile = CreateProfile();

        using NativeSshSession first = new(profile, interop: interop, connectionPool: pool);
        nint ended = interop.Connections.Single();
        interop.Enqueue(ended, NativeSshEvent.Closed());
        await WaitUntilAsync(() => pool.Count == 0);

        using NativeSshSession second = new(profile, interop: interop, connectionPool: pool);
        Assert.Equal(2, interop.SharedConnectCount);
        Assert.NotEqual(ended, interop.OpenedShells[1].Connection);
    }

    [Fact]
    public void AConnectionThatRefusesTheShell_IsAbandonedForANewOne()
    {
        // The native side ended the connection, but its Closed event has not been polled yet.
        var interop = new SharingInterop();
        var pool = new NativeSshConnectionPool();
        SshProfile profile = CreateProfile();

        using NativeSshSession first = new(profile, interop: interop, connectionPool: pool);
        nint ended = interop.Connections.Single();
        interop.DeadConnections.Add(ended);

        using NativeSshSession second = new(profile, interop: interop, connectionPool: pool);

        Assert.Equal(2, interop.SharedConnectCount);
        Assert.Contains(ended, interop.ClosedHandles);
        Assert.NotEqual(ended, interop.OpenedShells[^1].Connection);
    }

    [Fact]
    public void KeyFor_IgnoresTheTerminalButNotTheConnection()
    {
        SshProfile profile = CreateProfile();
        NativeSshConnectionOptions small = NativeSshConnectionOptionsFactory.Create(profile, 80, 24);
        NativeSshConnectionOptions large = NativeSshConnectionOptionsFactory.Create(profile, 200, 60);
        Assert.Equal(NativeSshConnectionPool.KeyFor(profile, small), NativeSshConnectionPool.KeyFor(profile, large));

        profile.JumpHops.Add(new SshJumpHop { Host = "bastion", User = "ops" });
        NativeSshConnectionOptions viaBastion = NativeSshConnectionOptionsFactory.Create(profile, 80, 24);
        Assert.NotEqual(NativeSshConnectionPool.KeyFor(profile, small), NativeSshConnectionPool.KeyFor(profile, viaBastion));
    }

    [Fact]
    public async Task AnExec_JoinsTheConnectionATabOpened_AndLetsGoWhenItEnds()
    {
        var interop = new SharingInterop();
        var pool = new NativeSshConnectionPool();
        SshProfile profile = CreateProfile();

        NativeSshSession tab = new(profile, interop: interop, connectionPool: pool);
        var transport = new NativeSshExecTransport(profile, interop, interactionHandler: null, NativeSshConnectionOptionsFactory.Create, log: _ => { }, connectionPool: pool);
        ISshExecChannel channel = transport.Start("ntilde-mux proxy --stdio", TestContext.Current.CancellationToken);

        Assert.Equal(1, interop.SharedConnectCount);
        Assert.Equal(0, interop.PlainExecCount);
        (nint connection, nint execHandle, string command) = interop.OpenedExecs.Single();
        Assert.Equal(interop.Connections.Single(), connection);
        Assert.Equal("ntilde-mux proxy --stdio", command);

        interop.Enqueue(execHandle, NativeSshEvent.Data("ready\n"u8.ToArray()));
        interop.Enqueue(execHandle, NativeSshEvent.ExitStatus(0));
        interop.Enqueue(execHandle, NativeSshEvent.Closed());
        Assert.Equal(0, await channel.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        channel.Dispose();

        // The tab still holds the connection; once it goes, nothing does.
        Assert.DoesNotContain(connection, interop.ClosedHandles);
        tab.Dispose();
        Assert.Contains(connection, interop.ClosedHandles);
    }

    [Fact]
    public void AnExecOnAProfileWithoutMultiplexing_ConnectsOnItsOwn()
    {
        var interop = new SharingInterop();
        SshProfile profile = CreateProfile();
        profile.MuxOptions.Enabled = false;
        var transport = new NativeSshExecTransport(profile, interop, interactionHandler: null, NativeSshConnectionOptionsFactory.Create, log: _ => { }, connectionPool: new NativeSshConnectionPool());

        using ISshExecChannel channel = transport.Start("true", TestContext.Current.CancellationToken);

        Assert.Equal(1, interop.PlainExecCount);
        Assert.Equal(0, interop.SharedConnectCount);
    }

    [Fact]
    public void ATabOpenedAfterAnExec_JoinsTheConnectionTheExecOpened()
    {
        var interop = new SharingInterop();
        var pool = new NativeSshConnectionPool();
        SshProfile profile = CreateProfile();
        var transport = new NativeSshExecTransport(profile, interop, interactionHandler: null, NativeSshConnectionOptionsFactory.Create, log: _ => { }, connectionPool: pool);

        using ISshExecChannel channel = transport.Start("ntilde-mux proxy --stdio", TestContext.Current.CancellationToken);
        using NativeSshSession tab = new(profile, interop: interop, connectionPool: pool);

        Assert.Equal(1, interop.SharedConnectCount);
        Assert.Equal(interop.Connections.Single(), interop.OpenedShells.Single().Connection);
    }

    [Fact]
    public async Task AnExecWhosePromptHandlerThrows_FailsWithTheHandlersMessage()
    {
        // An unshared exec reports its handler's exception (the mux's automatic reconnect relies on it reading as a
        // failure, not a cancel). On a shared connection the handler runs on the connection, and the native side only
        // says the connection closed: the channel must still report the handler's message.
        var interop = new SharingInterop();
        var pool = new NativeSshConnectionPool();
        SshProfile profile = CreateProfile();
        var transport = new NativeSshExecTransport(profile, interop, new ThrowingHandler(), NativeSshConnectionOptionsFactory.Create, log: _ => { }, connectionPool: pool);
        ISshExecChannel channel = transport.Start("ntilde-mux proxy --stdio", TestContext.Current.CancellationToken);
        nint connection = interop.Connections.Single();
        nint execHandle = interop.OpenedExecs.Single().Handle;

        interop.Enqueue(connection, new NativeSshEvent(NativeSshEventKind.PasswordPrompt, """{"host":"native.example","port":22,"user":"nova","prompt":"Password:"}"""u8.ToArray()));
        await WaitUntilAsync(() => interop.ClosedHandles.Contains(connection));
        interop.Enqueue(execHandle, new NativeSshEvent(NativeSshEventKind.Error, """{"message":"the shared SSH connection was closed"}"""u8.ToArray()));
        interop.Enqueue(execHandle, NativeSshEvent.Closed());

        await channel.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var failure = await Assert.ThrowsAsync<SshExecTransportException>(
            () => channel.Stdout.ReadAsync(new byte[16], TestContext.Current.CancellationToken).AsTask());
        Assert.Contains("no automatic answer", failure.Message, StringComparison.Ordinal);
        channel.Dispose();
    }

    [Fact]
    public async Task Sftp_RunsOnALoggedInSharedConnection_AndNeverStartsOne()
    {
        var interop = new SharingInterop();
        var pool = new NativeSshConnectionPool();
        SshProfile profile = CreateProfile();
        var transfer = new NativeSftpTransferOptions
        {
            Direction = NativeSftpTransferDirection.Download,
            Kind = NativeSftpTransferKind.File,
            RemotePath = "/tmp/a",
            LocalPath = "/tmp/b"
        };

        // No connection: the caller connects on its own, and the pool starts nothing.
        Assert.False(pool.TryRunSftpTransfer(profile, transfer, progress: null, TestContext.Current.CancellationToken));
        Assert.Null(pool.TryListRemoteDirectory(profile, "~", TestContext.Current.CancellationToken));
        Assert.Equal(0, interop.SharedConnectCount);

        using NativeSshSession tab = new(profile, interop: interop, connectionPool: pool);
        // Still logging in: SFTP cannot answer its prompts, so it does not wait on them either.
        Assert.False(pool.TryRunSftpTransfer(profile, transfer, progress: null, TestContext.Current.CancellationToken));

        interop.Enqueue(interop.Connections.Single(), Connected());
        await WaitUntilAsync(() => pool.TryListRemoteDirectory(profile, "~", TestContext.Current.CancellationToken) is not null);

        Assert.True(pool.TryRunSftpTransfer(profile, transfer, progress: null, TestContext.Current.CancellationToken));
        Assert.Equal(2, interop.SharedSftpCalls.Count);
        Assert.All(interop.SharedSftpCalls, call => Assert.Equal(interop.Connections.Single(), call));
    }

    [Fact]
    public async Task Sftp_OnAProfileWithoutMultiplexing_NeverJoins()
    {
        var interop = new SharingInterop();
        var pool = new NativeSshConnectionPool();
        SshProfile profile = CreateProfile();
        using NativeSshSession tab = new(profile, interop: interop, connectionPool: pool);
        interop.Enqueue(interop.Connections.Single(), Connected());
        await WaitUntilAsync(() => interop.ConnectedSeen);

        profile.MuxOptions.Enabled = false;

        Assert.Null(pool.TryListRemoteDirectory(profile, "~", TestContext.Current.CancellationToken));
        Assert.Empty(interop.SharedSftpCalls);
    }

    [Fact]
    public void ABusyForwardPort_WarnsEveryTabOnTheSharedConnection_InsteadOfFailingIt()
    {
        using var squatter = new TcpListener(IPAddress.Loopback, 0);
        squatter.Start();
        int busyPort = ((IPEndPoint)squatter.LocalEndpoint).Port;
        var interop = new SharingInterop();
        var pool = new NativeSshConnectionPool();
        SshProfile profile = CreateProfile();
        profile.Forwards.Add(new PortForward { Kind = PortForwardKind.Dynamic, BindAddress = "127.0.0.1", SourcePort = busyPort });

        // A persistent tab opening the connection is what used to fail here.
        var transport = new NativeSshExecTransport(profile, interop, interactionHandler: null, NativeSshConnectionOptionsFactory.Create, log: _ => { }, connectionPool: pool);
        using ISshExecChannel channel = transport.Start("ntilde-mux proxy --stdio", TestContext.Current.CancellationToken);

        using NativeSshSession tab = new(profile, interop: interop, connectionPool: pool);
        var output = new List<string>();
        tab.OnOutputReceived += output.Add;

        // One connection: a forward that cannot bind is not a connection that ended, so nothing reconnects.
        Assert.Equal(1, interop.SharedConnectCount);
        Assert.Contains($"127.0.0.1:{busyPort}", string.Concat(output), StringComparison.Ordinal);
        Assert.Contains("continuing without this forward", string.Concat(output), StringComparison.Ordinal);
    }

    [Fact]
    public void ABusyForwardPort_StillFailsATabWithAConnectionOfItsOwn()
    {
        using var squatter = new TcpListener(IPAddress.Loopback, 0);
        squatter.Start();
        int busyPort = ((IPEndPoint)squatter.LocalEndpoint).Port;
        SshProfile profile = CreateProfile();
        profile.MuxOptions.Enabled = false;
        profile.Forwards.Add(new PortForward { Kind = PortForwardKind.Dynamic, BindAddress = "127.0.0.1", SourcePort = busyPort });

        Assert.Throws<InvalidOperationException>(() => new NativeSshSession(profile, interop: new SharingInterop(), connectionPool: new NativeSshConnectionPool()));
    }

    /// <summary>
    /// #610: only the tab that opened a shared connection answers its prompts. One it refuses ends the connection, and
    /// every shell on it, so each tab that joined while it was logging in is told what was refused - before the refusal
    /// is sent, so before its shell can end - and can say why its shell ended too.
    /// </summary>
    [Fact]
    public async Task APromptTheOpenerRefused_IsReportedToEveryTabThatJoined_BeforeItIsSent()
    {
        var interop = new SharingInterop();
        var pool = new NativeSshConnectionPool();
        SshProfile profile = CreateProfile();
        var opener = new RecordingHandler(SshInteractionResponse.Cancel());
        var joiner = new RecordingHandler(SshInteractionResponse.Cancel());
        int refusalsSeenWhenSent = -1;
        interop.BeforeResponse = () => refusalsSeenWhenSent = joiner.RefusedElsewhere.Count;

        using NativeSshSession first = new(profile, interop: interop, interactionHandler: opener, connectionPool: pool);
        using NativeSshSession second = new(profile, interop: interop, interactionHandler: joiner, connectionPool: pool);
        interop.Enqueue(interop.Connections.Single(), PasswordPrompt());

        await WaitUntilAsync(() => !interop.Responses.IsEmpty);
        Assert.Equal(SshInteractionKind.Password, Assert.Single(opener.Asked));
        Assert.Empty(joiner.Asked);
        Assert.Equal(SshInteractionKind.Password, Assert.Single(joiner.RefusedElsewhere).Kind);
        Assert.Equal(1, refusalsSeenWhenSent);
        Assert.Empty(opener.RefusedElsewhere); // it refused the prompt itself
    }

    /// <summary>A prompt the opener answered is nothing the tabs that joined need to hear about.</summary>
    [Fact]
    public async Task APromptTheOpenerAnswered_IsNotReportedToTheTabsThatJoined()
    {
        var interop = new SharingInterop();
        var pool = new NativeSshConnectionPool();
        SshProfile profile = CreateProfile();
        var opener = new RecordingHandler(SshInteractionResponse.FromSecret("secret"));
        var joiner = new RecordingHandler(SshInteractionResponse.Cancel());

        using NativeSshSession first = new(profile, interop: interop, interactionHandler: opener, connectionPool: pool);
        using NativeSshSession second = new(profile, interop: interop, interactionHandler: joiner, connectionPool: pool);
        interop.Enqueue(interop.Connections.Single(), PasswordPrompt());

        await WaitUntilAsync(() => !interop.Responses.IsEmpty);
        Assert.Single(opener.Asked);
        Assert.Empty(joiner.RefusedElsewhere);
    }

    private static NativeSshEvent PasswordPrompt() =>
        new(NativeSshEventKind.PasswordPrompt, """{"host":"native.example","port":22,"user":"nova","prompt":"Password:"}"""u8.ToArray());

    /// <summary>Answers every prompt with one response, and records what it was asked and what was refused elsewhere.</summary>
    private sealed class RecordingHandler(SshInteractionResponse response) : ISshInteractionHandler
    {
        private readonly ConcurrentQueue<SshInteractionKind> _asked = new();
        private readonly ConcurrentQueue<SshInteractionRequest> _refusedElsewhere = new();

        public IReadOnlyList<SshInteractionKind> Asked => _asked.ToArray();
        public IReadOnlyList<SshInteractionRequest> RefusedElsewhere => _refusedElsewhere.ToArray();

        public Task<SshInteractionResponse> HandleAsync(SshInteractionRequest request, CancellationToken cancellationToken)
        {
            _asked.Enqueue(request.Kind);
            return Task.FromResult(response);
        }

        public void RefusedOnSharedConnection(SshInteractionRequest request) => _refusedElsewhere.Enqueue(request);
    }

    private sealed class ThrowingHandler : ISshInteractionHandler
    {
        public Task<SshInteractionResponse> HandleAsync(SshInteractionRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no automatic answer for this prompt");
    }

    private static NativeSshEvent Connected() =>
        new(NativeSshEventKind.Connected, """{"host":"native.example","port":22,"user":"nova"}"""u8.ToArray());

    private static SshProfile CreateProfile()
    {
        var profile = new SshProfile
        {
            Id = Guid.Parse("8d3f6c1e-3c43-4f6b-9a55-0c2b1a7e4d21"),
            BackendKind = SshBackendKind.Native,
            Host = "native.example",
            User = "nova",
            Port = 22
        };
        profile.MuxOptions.Enabled = true;
        return profile;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan? timeout = null)
    {
        DateTime deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(2));
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.True(predicate(), "Condition was not met before timeout.");
    }

    /// <summary>
    /// Fake native layer with a queue per handle, so a test can feed the connection and each shell separately.
    /// </summary>
    private sealed class SharingInterop : INativeSshInterop
    {
        private readonly ConcurrentDictionary<nint, ConcurrentQueue<NativeSshEvent>> _events = new();
        private int _nextHandle = 100;

        public int PlainConnectCount { get; private set; }
        private readonly ConcurrentQueue<nint> _connections = new();

        public int SharedConnectCount => Connections.Count;

        // Read by the poll loops while a test opens more; a snapshot keeps both sides safe.
        public IReadOnlyList<nint> Connections => _connections.ToArray();
        public List<(nint Connection, nint Handle)> OpenedShells { get; } = [];
        public HashSet<nint> DeadConnections { get; } = [];
        public ConcurrentBag<nint> ClosedHandles { get; } = [];
        public ConcurrentQueue<(nint Handle, byte[] Data)> WriteLog { get; } = new();
        public IReadOnlyList<(nint Handle, byte[] Data)> Writes => WriteLog.ToArray();
        public ConcurrentQueue<(nint Handle, NativeSshResponseKind Kind)> Responses { get; } = new();
        public bool ConnectedSeen { get; private set; }
        public int PlainExecCount { get; private set; }
        public List<(nint Connection, nint Handle, string Command)> OpenedExecs { get; } = [];
        public ConcurrentQueue<nint> SharedSftpLog { get; } = new();
        public IReadOnlyList<nint> SharedSftpCalls => SharedSftpLog.ToArray();

        public NovaSshSafeHandle Exec(NativeSshConnectionOptions options, string command)
        {
            PlainExecCount++;
            return NewHandle();
        }

        public NovaSshSafeHandle OpenExec(NovaSshSafeHandle connection, NativeSshConnectionOptions options, string command)
        {
            nint connectionValue = connection.DangerousGetHandle();
            if (DeadConnections.Contains(connectionValue) || ClosedHandles.Contains(connectionValue))
            {
                throw new InvalidOperationException("connection ended");
            }

            NovaSshSafeHandle handle = NewHandle();
            OpenedExecs.Add((connectionValue, handle.DangerousGetHandle(), command));
            return handle;
        }

        public bool TryRunSftpTransferShared(NovaSshSafeHandle connection, NativeSftpTransferOptions transferOptions, Action<NativeSftpTransferProgress>? progress, CancellationToken cancellationToken)
        {
            SharedSftpLog.Enqueue(connection.DangerousGetHandle());
            return true;
        }

        public IReadOnlyList<NativeRemotePathEntry>? TryListRemoteDirectoryShared(NovaSshSafeHandle connection, string remotePath, CancellationToken cancellationToken)
        {
            SharedSftpLog.Enqueue(connection.DangerousGetHandle());
            return [];
        }

        public void SendEof(NovaSshSafeHandle sessionHandle)
        {
        }


        public NovaSshSafeHandle Connect(NativeSshConnectionOptions options)
        {
            PlainConnectCount++;
            return NewHandle();
        }

        public NovaSshSafeHandle ConnectShared(NativeSshConnectionOptions options)
        {
            NovaSshSafeHandle handle = NewHandle();
            _connections.Enqueue(handle.DangerousGetHandle());
            return handle;
        }

        public NovaSshSafeHandle OpenShell(NovaSshSafeHandle connection, NativeSshConnectionOptions options)
        {
            nint connectionValue = connection.DangerousGetHandle();
            if (DeadConnections.Contains(connectionValue) || ClosedHandles.Contains(connectionValue))
            {
                throw new InvalidOperationException("connection ended");
            }

            NovaSshSafeHandle handle = NewHandle();
            OpenedShells.Add((connectionValue, handle.DangerousGetHandle()));
            return handle;
        }

        public void Enqueue(nint handle, NativeSshEvent nextEvent) => Queue(handle).Enqueue(nextEvent);

        public NativeSshEvent? PollEvent(NovaSshSafeHandle sessionHandle)
        {
            if (!Queue(sessionHandle.DangerousGetHandle()).TryDequeue(out NativeSshEvent? nextEvent))
            {
                return null;
            }

            if (nextEvent.Kind == NativeSshEventKind.Connected && Connections.Contains(sessionHandle.DangerousGetHandle()))
            {
                ConnectedSeen = true;
            }

            return nextEvent;
        }

        public void Write(NovaSshSafeHandle sessionHandle, ReadOnlySpan<byte> data) =>
            WriteLog.Enqueue((sessionHandle.DangerousGetHandle(), data.ToArray()));

        public void Resize(NovaSshSafeHandle sessionHandle, int cols, int rows)
        {
        }

        /// <summary>Runs as a response is submitted, before it is recorded: what a test sees the moment a prompt is answered.</summary>
        public Action? BeforeResponse { get; set; }

        public void SubmitResponse(NovaSshSafeHandle sessionHandle, NativeSshResponseKind responseKind, ReadOnlySpan<byte> data)
        {
            BeforeResponse?.Invoke();
            Responses.Enqueue((sessionHandle.DangerousGetHandle(), responseKind));
        }

        public void Close(NovaSshSafeHandle sessionHandle) => ClosedHandles.Add(sessionHandle.DangerousGetHandle());

        public int OpenDirectTcpIp(NovaSshSafeHandle sessionHandle, NativePortForwardOpenOptions options) => 1;

        public void WriteChannel(NovaSshSafeHandle sessionHandle, int channelId, ReadOnlySpan<byte> data)
        {
        }

        public void SendChannelEof(NovaSshSafeHandle sessionHandle, int channelId)
        {
        }

        public void CloseChannel(NovaSshSafeHandle sessionHandle, int channelId)
        {
        }

        public void RunSftpTransfer(NativeSshConnectionOptions connectionOptions, NativeSftpTransferOptions transferOptions, Action<NativeSftpTransferProgress>? progress, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public IReadOnlyList<NativeRemotePathEntry> ListRemoteDirectory(NativeSshConnectionOptions connectionOptions, string remotePath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private NovaSshSafeHandle NewHandle() => new(new IntPtr(Interlocked.Increment(ref _nextHandle)), ownsHandle: false);

        private ConcurrentQueue<NativeSshEvent> Queue(nint handle) => _events.GetOrAdd(handle, _ => new ConcurrentQueue<NativeSshEvent>());
    }
}
