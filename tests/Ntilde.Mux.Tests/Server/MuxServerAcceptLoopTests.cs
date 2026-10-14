using Ntilde.Mux.Tests.Support;
using Ntilde.Mux.Transport;

namespace Ntilde.Mux.Tests.Server;

/// <summary>
/// PR #489 review 2, item 1: one failed accept used to end the accept loop for good, leaving a
/// daemon that held its lock and descriptor but accepted nobody. The loop now retries forever and
/// reports how long it has been failing; the host decides when to give up (MuxDaemonHostTests).
/// </summary>
public sealed class MuxServerAcceptLoopTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static MuxServerOptions FastRetry() => new()
    {
        ForceConPtyFiltering = false,
        AcceptRetryInitialDelay = TimeSpan.FromMilliseconds(5),
        AcceptRetryMaxDelay = TimeSpan.FromMilliseconds(20),
    };

    [Fact]
    public async Task Transient_accept_failures_are_retried_and_the_failure_clock_resets()
    {
        using var inner = new InMemoryMuxListener();
        var flaky = new ScriptedListener(inner, call => call < 3);
        using var server = new MuxServer(new ScriptedSessionFactory(), FastRetry());
        server.Start(flaky);

        using MuxClient client = await MuxClient.ConnectAsync(inner.Connect(), null, Ct);
        await client.PingAsync(Ct);
        using MuxClient second = await MuxClient.ConnectAsync(inner.Connect(), null, Ct);
        await second.PingAsync(Ct);

        Assert.True(flaky.Failed >= 3, $"the listener failed {flaky.Failed} times");
        Assert.Null(server.AcceptFailingFor);
    }

    [Fact]
    public async Task A_listener_that_keeps_failing_is_retried_forever_and_the_clock_runs()
    {
        using var inner = new InMemoryMuxListener();
        var broken = new ScriptedListener(inner, _ => true);
        using var server = new MuxServer(new ScriptedSessionFactory(), FastRetry());
        server.Start(broken);

        await TestWait.UntilAsync(() => broken.Failed >= 30, "the loop kept retrying well past any give-up count");
        TimeSpan first = server.AcceptFailingFor ?? throw new Xunit.Sdk.XunitException("the failure clock is not running");
        await Task.Delay(100, Ct);
        Assert.True(server.AcceptFailingFor > first, "the failure clock keeps running from the streak's first failure");
    }

    [Fact]
    public async Task A_listener_closing_under_the_loop_counts_as_failing_but_Dispose_does_not()
    {
        var inner = new InMemoryMuxListener();
        using var server = new MuxServer(new ScriptedSessionFactory(), FastRetry());
        server.Start(new ScriptedListener(inner, _ => false));
        inner.Dispose(); // Accept now returns null, and not because the server asked for it
        await TestWait.UntilAsync(() => server.AcceptFailingFor is not null, "an unexpected close counts as failing");

        using var inner2 = new InMemoryMuxListener();
        var server2 = new MuxServer(new ScriptedSessionFactory(), FastRetry());
        server2.Start(new ScriptedListener(inner2, _ => false));
        server2.Dispose();
        Assert.Null(server2.AcceptFailingFor);
    }

    /// <summary>Accept call <c>n</c> (0-based) throws IOException when <c>failOnCall(n)</c>, else delegates.</summary>
    internal sealed class ScriptedListener(IMuxListener inner, Func<int, bool> failOnCall) : IMuxListener
    {
        private int _calls;
        private int _failed;

        public int Failed => Volatile.Read(ref _failed);

        public Stream? Accept(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (failOnCall(Interlocked.Increment(ref _calls) - 1))
            {
                Interlocked.Increment(ref _failed);
                throw new IOException("scripted accept failure");
            }

            return inner.Accept(cancellationToken);
        }

        public void Dispose() => inner.Dispose();
    }
}
