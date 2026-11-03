using Ntilde.Shell.Clipboard;

namespace Ntilde.Tests.Shell;

public class PrimaryReceiveDeadlineTests
{
    private sealed class Clock : TimeProvider
    {
        public long Now;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Now;
    }

    [Fact]
    public void Incoming_progress_refreshes_idle_time_beyond_the_original_ten_second_deadline()
    {
        var clock = new Clock(); var deadline = new PrimaryReceiveDeadline(clock);
        deadline.Progress();
        for (int chunk = 1; chunk <= 5; chunk++)
        {
            clock.Now = chunk * 9000;
            deadline.Check(); deadline.Progress();
        }
        clock.Now += 9999; deadline.Check();
        clock.Now++; Assert.Throws<IOException>(deadline.Check);
    }

    [Fact]
    public void Steady_chunks_cannot_extend_the_overall_receive_limit()
    {
        var clock = new Clock(); var deadline = new PrimaryReceiveDeadline(clock);
        deadline.Progress();
        for (int chunk = 1; chunk <= 6; chunk++)
        {
            clock.Now = chunk * 9000;
            deadline.Check(); deadline.Progress();
        }
        clock.Now = 59999; deadline.Check();
        clock.Now++; Assert.Throws<IOException>(deadline.Check);
    }

    [Fact]
    public void An_unstarted_receive_cannot_skip_deadline_validation()
    {
        Assert.Throws<InvalidOperationException>(new PrimaryReceiveDeadline(new Clock()).Check);
    }
}
