using System.Diagnostics;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Pty;

namespace Ntilde.Mux.Tests.Headless;

/// <summary>Phase 3 spec §4, §5.</summary>
public sealed class SessionEventsTests
{
    private static readonly MuxPresentation P = new() { Cols = 80, Rows = 24, CellWidthPx = 10, CellHeightPx = 20 };

    private static (HeadlessTerminalSession Mux, ScriptedTerminalSession Fake) NewSession(TimeSpan interval)
    {
        var fake = new ScriptedTerminalSession(new TerminalSessionRequest("scripted", string.Empty, string.Empty, 80, 24, null, false, null));
        var mux = new HeadlessTerminalSession(Guid.NewGuid(), fake, new HeadlessSessionOptions
        {
            Command = "scripted",
            Title = "t",
            ForceConPtyFiltering = false,
            SessionChangedInterval = interval,
        });
        return (mux, fake);
    }

    [Fact]
    public async Task An_attach_is_announced_to_v2_sinks_only()
    {
        (HeadlessTerminalSession mux, _) = NewSession(TimeSpan.FromMilliseconds(100));
        using (mux)
        {
            var v2 = new RecordingFrameSink { WantsSessionEvents = true };
            var v1 = new RecordingFrameSink();
            mux.PostAttach(v2, 1, 0, P, MuxProtocol.MaxFrameBytes);
            mux.PostAttach(v1, 2, 0, P, MuxProtocol.MaxFrameBytes);

            await TestWait.UntilAsync(() => v2.SessionChanges.Count > 0 && v2.SessionChanges[^1].AttachedClients == 2, "the v2 sink hears the final count");
            Assert.Equal("t", v2.SessionChanges[^1].Title);
            Assert.DoesNotContain(v1.Described, d => d.StartsWith("SessionChanged", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_burst_is_rate_bounded_and_the_trailing_notification_needs_no_further_activity()
    {
        TimeSpan interval = TimeSpan.FromMilliseconds(200);
        (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession(interval);
        using (mux)
        {
            var sink = new RecordingFrameSink { WantsSessionEvents = true };
            var sw = Stopwatch.StartNew();
            mux.PostAttach(sink, 1, 0, P, MuxProtocol.MaxFrameBytes);
            for (int i = 0; i < 20; i++) fake.Emit($"\x1b]2;title-{i}\x07");
            await mux.FlushAsync();

            // Nothing else happens from here on: the last title must still arrive, flushed by the
            // parse loop's own bounded wait (no timer, no thread pool).
            await TestWait.UntilAsync(() => sink.SessionChanges.Count > 0 && sink.SessionChanges[^1].Title == "title-19",
                "the trailing notification carries the last title", TimeSpan.FromSeconds(5));
            long elapsedMs = sw.ElapsedMilliseconds;

            int allowed = 1 + (int)Math.Ceiling(elapsedMs / interval.TotalMilliseconds);
            Assert.InRange(sink.SessionChanges.Count, 1, allowed);
            IReadOnlyList<long> times = sink.SessionChangeTimesMs;
            for (int i = 1; i < times.Count; i++)
            {
                // TickCount64 resolution is ~15 ms on Windows: allow that much jitter, never more.
                Assert.True(times[i] - times[i - 1] >= interval.TotalMilliseconds - 20, $"notifications {i - 1} and {i} were {times[i] - times[i - 1]} ms apart");
            }

            int settled = sink.SessionChanges.Count;
            await Task.Delay(interval * 3, TestContext.Current.CancellationToken);
            Assert.Equal(settled, sink.SessionChanges.Count); // no change, no notification
        }
    }

    [Fact]
    public async Task Title_and_cwd_come_from_the_mux_parser()
    {
        (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession(TimeSpan.FromMilliseconds(50));
        using (mux)
        {
            var sink = new RecordingFrameSink { WantsSessionEvents = true };
            mux.PostAttach(sink, 1, 0, P, MuxProtocol.MaxFrameBytes);
            fake.Emit("\x1b]2;build\x07\x1b]7;file://localhost/tmp/work\x07");

            await TestWait.UntilAsync(() => sink.SessionChanges.Count > 0 && sink.SessionChanges[^1].Cwd is not null, "the cwd was announced");
            SessionChangedNotification last = sink.SessionChanges[^1];
            Assert.Equal("build", last.Title);
            Assert.EndsWith("work", last.Cwd, StringComparison.Ordinal);
            Assert.Equal("build", mux.Title);
            Assert.EndsWith("work", mux.Cwd, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Kill_sends_killed_before_exited_to_other_v2_sinks_only()
    {
        (HeadlessTerminalSession mux, _) = NewSession(TimeSpan.FromMilliseconds(50));
        using (mux)
        {
            var killer = new RecordingFrameSink { WantsSessionEvents = true };
            var other = new RecordingFrameSink { WantsSessionEvents = true };
            var v1 = new RecordingFrameSink();
            mux.PostAttach(killer, 1, 0, P, MuxProtocol.MaxFrameBytes);
            mux.PostAttach(other, 2, 0, P, MuxProtocol.MaxFrameBytes);
            mux.PostAttach(v1, 3, 0, P, MuxProtocol.MaxFrameBytes);
            await mux.InvokeAsync(() => 0);

            mux.Kill(killer, "ntilde-cli");

            await TestWait.UntilAsync(() => other.Described.Contains("Exited:-1"), "the other sink saw the exit");
            string[] ending = other.Described.Where(d => d.StartsWith("Killed", StringComparison.Ordinal) || d.StartsWith("Exited", StringComparison.Ordinal)).ToArray();
            Assert.Equal(["Killed:ntilde-cli", "Exited:-1"], ending);
            Assert.DoesNotContain(killer.Described, d => d.StartsWith("Killed", StringComparison.Ordinal));
            Assert.Contains("Exited:-1", killer.Described);
            Assert.DoesNotContain(v1.Described, d => d.StartsWith("Killed", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_natural_exit_sends_no_killed()
    {
        (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession(TimeSpan.FromMilliseconds(50));
        using (mux)
        {
            var sink = new RecordingFrameSink { WantsSessionEvents = true };
            mux.PostAttach(sink, 1, 0, P, MuxProtocol.MaxFrameBytes);
            fake.Exit(3);

            await TestWait.UntilAsync(() => sink.Described.Contains("Exited:3"), "the exit was announced");
            Assert.DoesNotContain(sink.Described, d => d.StartsWith("Killed", StringComparison.Ordinal));
        }
    }
}
