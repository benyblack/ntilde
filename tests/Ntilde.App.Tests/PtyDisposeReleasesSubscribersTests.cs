using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Ntilde.Pty;
using Xunit;

namespace Ntilde.Tests
{
    /// <summary>
    /// #548: on Unix, a background job that keeps the terminal open keeps the session's read blocked after
    /// <see cref="RustPtySession.Dispose"/>, and the read thread roots the session. Whatever the session still held
    /// on to stayed in memory with it - through a pane's handlers, the closed pane, its buffer and its scrollback.
    /// </summary>
    [Collection(PtyRealShellCollection.Name)]
    public class PtyDisposeReleasesSubscribersTests
    {
        /// <summary>
        /// The read the job keeps blocked, played by the read hook: it returns only when the test lets it,
        /// whatever the dispose does.
        /// </summary>
        [Fact]
        [Trait("Category", "PtySmoke")]
        public async Task A_disposed_session_whose_read_is_still_blocked_lets_its_subscribers_go()
        {
            using var readMayReturn = new ManualResetEventSlim();
            using var readEntered = new ManualResetEventSlim();
            int Read(RustPtySession.PtySafeHandle handle, byte[] buffer, int length)
            {
                readEntered.Set();
                readMayReturn.Wait();
                return 0;
            }

            var session = new RustPtySession(
                ShellHelper.GetDefaultShell(), 80, 24, args: null, cwd: null,
                skipPowerShellPostLaunchInit: true, environmentOverrides: null,
                readFromPty: Read);
            try
            {
                Assert.True(readEntered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "setup: the read never started");
                WeakReference subscriberState = Subscribe(session);

                session.Dispose();

                Assert.True(session.ReadLoopThread!.IsAlive, "setup: the read thread should still be blocked, as the job keeps it");
                var waited = Stopwatch.StartNew();
                while (subscriberState.IsAlive && waited.Elapsed < TimeSpan.FromSeconds(5))
                {
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                    GC.WaitForPendingFinalizers();
                    if (subscriberState.IsAlive) await Task.Delay(50, TestContext.Current.CancellationToken);
                }

                Assert.False(subscriberState.IsAlive, "the disposed session still holds its subscribers, so its read thread roots them");
                GC.KeepAlive(session);
            }
            finally
            {
                readMayReturn.Set();
                session.Dispose();
            }
        }

        // Not inlined, so no local of the test's frame keeps the handlers' state alive. The state stands for the pane.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference Subscribe(RustPtySession session)
        {
            var pane = new object();
            session.OnOutputReceived += _ => GC.KeepAlive(pane);
            session.OnExit += _ => GC.KeepAlive(pane);
            session.OnRawOutputReceived += _ => GC.KeepAlive(pane);
            return new WeakReference(pane);
        }
    }
}
