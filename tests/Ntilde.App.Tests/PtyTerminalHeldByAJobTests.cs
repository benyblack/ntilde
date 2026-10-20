using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Ntilde.Pty;
using Xunit;

namespace Ntilde.Tests
{
    /// <summary>
    /// #548: on Unix a background job that ignores SIGHUP keeps the terminal open after its shell is gone, so the master
    /// never reports EOF. The session's read has to end anyway - when the tab closes, and when the shell exits on its own.
    /// Closing the tab then closes the terminal, as other terminals do: the job keeps running, without it.
    /// </summary>
    [Collection(PtyRealShellCollection.Name)]
    public class PtyTerminalHeldByAJobTests
    {
        private static readonly TimeSpan Patient = TimeSpan.FromSeconds(10);

        [Fact]
        [Trait("Category", "PtySmoke")]
        public async Task Closing_a_session_whose_terminal_a_job_still_holds_ends_its_read()
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix PTYs: on Windows the read is cancelled through ConPTY");
            var session = new RustPtySession("/bin/sh", 80, 24, args: "-c \"(trap '' HUP; exec sleep 60) & echo job:$!; exec sleep 60\"");
            int job = await JobPidAsync(session);
            try
            {
                session.Dispose();

                Assert.False(session.ReadLoopThread!.IsAlive, "the read thread is still blocked: the job holds the terminal");
            }
            finally
            {
                End(job);
                session.Dispose();
            }
        }

        [Fact]
        [Trait("Category", "PtySmoke")]
        public async Task A_shell_that_exits_leaving_a_job_on_its_terminal_is_reported_as_exited()
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix PTYs: on Windows the read is cancelled through ConPTY");
            var session = new RustPtySession("/bin/sh", 80, 24, args: "-c \"(trap '' HUP; exec sleep 60) & echo job:$!; sleep 1; exit 3\"");
            var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.OnExit += code => exited.TrySetResult(code);
            int job = await JobPidAsync(session);
            try
            {
                bool reported = await Task.WhenAny(exited.Task, Task.Delay(Patient, TestContext.Current.CancellationToken)) == exited.Task;

                Assert.True(reported, "the shell exited, but the read never ended, so the exit was never reported");
                Assert.Equal(3, await exited.Task);
            }
            finally
            {
                End(job);
                session.Dispose();
            }
        }

        /// <summary>The background job's pid, from the line the shell prints for it.</summary>
        private static async Task<int> JobPidAsync(RustPtySession session)
        {
            var pid = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var seen = new System.Text.StringBuilder();
            void OnOutput(string text)
            {
                lock (seen)
                {
                    seen.Append(text);
                    Match match = Regex.Match(seen.ToString(), @"job:(\d+)\r?\n");
                    if (match.Success) pid.TrySetResult(int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
                }
            }

            session.OnOutputReceived += OnOutput;
            try
            {
                return await pid.Task.WaitAsync(Patient, TestContext.Current.CancellationToken);
            }
            finally
            {
                session.OnOutputReceived -= OnOutput;
            }
        }

        private static void End(int pid)
        {
            try
            {
                using Process job = Process.GetProcessById(pid);
                job.Kill();
            }
            catch (ArgumentException) { /* already gone */ }
            catch (InvalidOperationException) { /* already gone */ }
        }
    }
}
