using Ntilde.Mux.Tests.Support;
using Ntilde.Mux.Transport;

namespace Ntilde.Mux.Tests.Server;

/// <summary>
/// PR #489 review 2, item 1: one failed accept used to end the accept loop for good, leaving a
/// daemon that held its lock and descriptor but accepted nobody.
/// </summary>
public sealed class MuxServerAcceptLoopTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MuxServerOptions FastRetry(int maxFailures = 10) => new()
    {
        ForceConPtyFiltering = false,
        AcceptRetryInitialDelay = TimeSpan.FromMilliseconds(5),
        AcceptRetryMaxDelay = TimeSpan.FromMilliseconds(20),
        MaxConsecutiveAcceptFailures = maxFailures,
    };

    [Fact]
    public async Task Transient_accept_failures_are_retried_and_the_server_keeps_accepting()
    {
        using var inner = new InMemoryMuxListener();
        var flaky = new FailingListener(inner, failures: 3);
        using var server = new MuxServer(new ScriptedSessionFactory(), FastRetry());
        int faulted = 0;
        server.AcceptLoopFaulted += _ => Interlocked.Increment(ref faulted);
        server.Start(flaky);

        using MuxClient client = await MuxClient.ConnectAsync(inner.Connect(), null, Ct);
        await client.PingAsync(Ct);
        using MuxClient second = await MuxClient.ConnectAsync(inner.Connect(), null, Ct);
        await second.PingAsync(Ct);

        Assert.True(flaky.Failed >= 3, $"the listener failed {flaky.Failed} times");
        Assert.Equal(0, Volatile.Read(ref faulted));
    }

    [Fact]
    public async Task A_listener_that_keeps_failing_ends_the_loop_with_AcceptLoopFaulted()
    {
        using var inner = new InMemoryMuxListener();
        var broken = new FailingListener(inner, failures: int.MaxValue);
        using var server = new MuxServer(new ScriptedSessionFactory(), FastRetry(maxFailures: 4));
        var faulted = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.AcceptLoopFaulted += ex => faulted.TrySetResult(ex);
        server.Start(broken);

        Exception? reason = await faulted.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.IsType<IOException>(reason);
        Assert.Equal(4, broken.Failed);
    }

    [Fact]
    public async Task A_listener_closing_under_the_loop_is_a_fault_but_Dispose_is_not()
    {
        var inner = new InMemoryMuxListener();
        using var server = new MuxServer(new ScriptedSessionFactory(), FastRetry());
        var faulted = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.AcceptLoopFaulted += ex => faulted.TrySetResult(ex);
        server.Start(new FailingListener(inner, failures: 0));

        inner.Dispose(); // Accept now returns null, and not because the server asked for it
        Assert.Null(await faulted.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct));

        using var inner2 = new InMemoryMuxListener();
        var server2 = new MuxServer(new ScriptedSessionFactory(), FastRetry());
        int faulted2 = 0;
        server2.AcceptLoopFaulted += _ => Interlocked.Increment(ref faulted2);
        server2.Start(new FailingListener(inner2, failures: 0));
        server2.Dispose();
        await Task.Delay(100, Ct);
        Assert.Equal(0, Volatile.Read(ref faulted2));
    }

    /// <summary>Throws IOException from the first <c>failures</c> Accept calls, then delegates.</summary>
    internal sealed class FailingListener(IMuxListener inner, int failures) : IMuxListener
    {
        private int _failed;

        public int Failed => Volatile.Read(ref _failed);

        public Stream? Accept(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _failed) < failures)
            {
                Interlocked.Increment(ref _failed);
                throw new IOException("scripted transient accept failure");
            }

            return inner.Accept(cancellationToken);
        }

        public void Dispose() => inner.Dispose();
    }
}
