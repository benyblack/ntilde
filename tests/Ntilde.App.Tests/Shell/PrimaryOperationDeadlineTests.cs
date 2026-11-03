using Ntilde.Shell.Clipboard;

namespace Ntilde.Tests.Shell;

public class PrimaryOperationDeadlineTests
{
    private sealed class Clock : TimeProvider
    {
        public long Now;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Now;
    }

    [Fact]
    public void Individually_fast_replies_cannot_restart_the_cumulative_capture_clock()
    {
        var clock = new Clock();
        var deadline = new PrimaryOperationDeadline(TimeSpan.FromMinutes(1), clock);
        for (int reply = 1; reply <= 29; reply++)
        {
            clock.Now = reply * 2000;
            deadline.Check();
        }
        clock.Now = 60000;
        Assert.Throws<IOException>(deadline.Check);
    }

    [Fact]
    public void Target_discovery_has_a_shorter_budget_than_the_surrounding_capture()
    {
        var clock = new Clock();
        var capture = new PrimaryOperationDeadline(TimeSpan.FromMinutes(1), clock);
        clock.Now = 1000;
        var targets = new PrimaryOperationDeadline(TimeSpan.FromSeconds(10), clock);
        clock.Now = 10999; targets.Check(); capture.Check();
        clock.Now = 11000; capture.Check();
        Assert.Throws<IOException>(targets.Check);
    }
}
