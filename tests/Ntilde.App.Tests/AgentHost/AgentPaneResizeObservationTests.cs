using Ntilde.AgentHost;
using Ntilde.VT;

namespace Ntilde.AppTests.AgentHost;

public sealed class AgentPaneResizeObservationTests
{
    [Theory]
    [InlineData(80, 24, 80, 24, true)]
    [InlineData(70, 20, 70, 20, false)]
    [InlineData(80, 24, 0, 0, false)]
    public void The_observation_reports_buffer_facts_and_requires_view_and_dispatch_agreement(
        int viewCols, int viewRows, int deliveredCols, int deliveredRows, bool settled)
    {
        var buffer = new TerminalBuffer(80, 24);
        Assert.True(AgentPaneResizeObservation.TryRead(buffer, viewCols, viewRows, (deliveredCols, deliveredRows), out var result));
        Assert.Equal((24, 80, settled), (result.Rows, result.Cols, result.LayoutSettled));
    }

    [Fact]
    public void A_busy_parser_does_not_make_the_UI_join_the_read_lock_wait_queue()
    {
        var buffer = new TerminalBuffer(80, 24);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        bool waited = false;
        var writer = new Thread(() =>
        {
            buffer.Lock.EnterWriteLock();
            try
            {
                entered.Set();
                while (!release.IsSet)
                {
                    if (buffer.Lock.WaitingReadCount > 0) { waited = true; break; }
                    Thread.Yield();
                }
            }
            finally { buffer.Lock.ExitWriteLock(); }
        })
        { IsBackground = true };
        writer.Start();
        entered.Wait(TestContext.Current.CancellationToken);
        try
        {
            bool captured = AgentPaneResizeObservation.TryRead(buffer, 80, 24, (80, 24), out _);
            Assert.False(captured);
            Assert.False(waited, "The UI must not queue a blocking read behind the parser.");
        }
        finally
        {
            release.Set();
            Assert.True(writer.Join(TimeSpan.FromSeconds(5)));
        }
    }
}
