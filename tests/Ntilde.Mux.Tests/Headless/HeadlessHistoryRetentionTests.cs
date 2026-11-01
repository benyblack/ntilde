using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Pty;

namespace Ntilde.Mux.Tests.Headless;

public sealed class HeadlessHistoryRetentionTests
{
    [Theory]
    [InlineData(null, 10_000)]
    [InlineData(0, 0)]
    [InlineData(500, 500)]
    [InlineData(100_000, 100_000)]
    [InlineData(int.MaxValue, 100_000)]
    public async Task SpawnSetsInitialBoundedPolicyAndWelcomeReportsIndependentCaps(int? requested, int expected)
    {
        using var host = new MuxTestHost();
        using var client = await host.ConnectClientAsync();
        Guid id = await client.SpawnAsync(new SpawnParams { Command = "scripted", MaxHistory = requested }, TestContext.Current.CancellationToken);
        Assert.Equal(expected, host.Mux(id).HistoryRetentionRows);
        Assert.Equal(MuxHistoryLimits.DefaultMaxBytes, host.Mux(id).Buffer.MaxScrollbackBytesLimit);
        Assert.Equal(MuxHistoryLimits.DefaultMaxRows, client.HistoryLimits!.MaxRows);
        Assert.Equal(MuxHistoryLimits.DefaultMaxAttachRows, client.HistoryLimits.MaxAttachRows);
    }

    [Fact]
    public async Task InitialPolicyAppliesBeforeStartupOutputAndBeforeAnyAttach()
    {
        using var host = new MuxTestHost();
        host.Factory.StartupOutput = string.Concat(Enumerable.Range(0, 15_000).Select(i => $"startup {i:D5}\r\n"));
        using var client = await host.ConnectClientAsync();
        Guid id = await client.SpawnAsync(new SpawnParams { Command = "scripted", MaxHistory = 100_000 }, TestContext.Current.CancellationToken);
        await host.Mux(id).FlushAsync();
        Assert.Equal(0, host.Mux(id).AttachedClients);
        Assert.True(host.Mux(id).Buffer.Scrollback.Count > 14_000);
        Assert.Single(host.Mux(id).Buffer.FindMatches("startup 00000", false, true));
    }

    [Fact]
    public async Task InteractiveClientsRaiseTheHighWaterLimitAndObserversDoNot()
    {
        using var host = new MuxTestHost(new MuxServerOptions { MaxSessionHistoryRows = 60_000 });
        using var client = await host.ConnectClientAsync();
        Guid id = await client.SpawnAsync(new SpawnParams { Command = "scripted", MaxHistory = 500 }, TestContext.Current.CancellationToken);
        var session = host.Mux(id);
        var first = new RecordingFrameSink();
        var second = new RecordingFrameSink();
        session.PostAttach(first, 1, 0, MuxTestHost.DefaultPresentation with { MaxHistory = 50_000 }, MuxProtocol.MaxFrameBytes);
        await session.FlushAsync();
        Assert.Equal(50_000, session.HistoryRetentionRows);
        session.PostAttach(second, 2, 0, MuxTestHost.DefaultPresentation with { MaxHistory = 100_000 }, MuxProtocol.MaxFrameBytes, MuxAttachMode.ReadOnly);
        await session.FlushAsync();
        Assert.Equal(50_000, session.HistoryRetentionRows);
        session.PostResize(second, 80, 24, MuxTestHost.DefaultPresentation with { MaxHistory = 100_000 });
        await session.FlushAsync();
        Assert.Equal(50_000, session.HistoryRetentionRows);
        session.PostResize(80, 24, MuxTestHost.DefaultPresentation with { MaxHistory = 100 });
        await session.FlushAsync();
        Assert.Equal(50_000, session.HistoryRetentionRows);
        session.PostResize(80, 24, MuxTestHost.DefaultPresentation with { MaxHistory = 100_000 });
        await session.FlushAsync();
        Assert.Equal(60_000, session.HistoryRetentionRows);
        session.PostDetach(first);
        session.PostResize(80, 24, MuxTestHost.DefaultPresentation);
        await session.FlushAsync();
        Assert.Equal(60_000, session.HistoryRetentionRows);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CoalescedResizeRequestsKeepTheHighestRetentionAndNewestGeometry(bool interactiveClient, bool presentationOnly)
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new ScriptedTerminalSession(new TerminalSessionRequest("scripted", "", "", 80, 24, null, false, null));
        using var session = new HeadlessTerminalSession(Guid.NewGuid(), fake, new HeadlessSessionOptions { ForceConPtyFiltering = false });
        var sink = new RecordingFrameSink();
        session.PostAttach(sink, 1, 0, MuxTestHost.DefaultPresentation, MuxProtocol.MaxFrameBytes);
        await session.FlushAsync();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int calls = 0;
        fake.OnResizeCalled = (_, _) =>
        {
            if (Interlocked.Increment(ref calls) != 1) return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5), ct)) throw new TimeoutException("Resize was not released.");
        };
        session.PostResize(81, 24, null);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5), ct));
            session.PostResize(interactiveClient ? sink : null, 82, 24, MuxTestHost.DefaultPresentation with { MaxHistory = 100_000 });
            session.PostResize(interactiveClient ? sink : null, 83, 24, MuxTestHost.DefaultPresentation with { MaxHistory = 100 }, presentationOnly);
        }
        finally { release.Set(); }
        await session.FlushAsync();
        Assert.Equal(100_000, session.HistoryRetentionRows);
        Assert.Equal(presentationOnly ? 82 : 83, session.Buffer.Cols);
    }

    [Fact]
    public async Task DaemonByteCapSurvivesOutputAndWidthAndHeightChanges()
    {
        const long cap = 128 * 80 * 12;
        using var host = new MuxTestHost(new MuxServerOptions { MaxSessionScrollbackBytes = cap });
        using var client = await host.ConnectClientAsync();
        Guid id = await client.SpawnAsync(new SpawnParams { Command = "scripted", MaxHistory = 100_000 }, TestContext.Current.CancellationToken);
        host.Fake(id).Emit(string.Concat(Enumerable.Range(0, 500).Select(i => $"line {i}\r\n")));
        await host.Mux(id).FlushAsync();
        foreach (var size in new[] { (80, 12), (160, 12), (40, 24) })
        {
            host.Mux(id).PostResize(size.Item1, size.Item2, MuxTestHost.DefaultPresentation with { MaxHistory = int.MaxValue });
            await host.Mux(id).FlushAsync();
            Assert.InRange(host.Mux(id).Buffer.MaxScrollbackBytes, 1, cap);
            Assert.InRange(host.Mux(id).Buffer.Scrollback.CurrentBytes, 0, cap);
            Assert.Equal(100_000, host.Mux(id).HistoryRetentionRows);
        }
    }
}
