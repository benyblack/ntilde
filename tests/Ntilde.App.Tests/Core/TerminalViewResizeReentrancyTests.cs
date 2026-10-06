using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.Shell;
using Ntilde.VT;

namespace Ntilde.Tests.Core;

/// <summary>
/// A window resized while its panes are printing must not freeze the UI thread.
/// </summary>
/// <remarks>
/// <para>
/// The frozen instance (2026-10-06) was resized from another process while three panes printed
/// 12k lines each. The layout pass reached <c>TerminalBuffer.Resize</c> while the PTY thread held
/// the buffer's write lock for a parser batch, so the UI thread queued as a waiting writer - and
/// on Windows that wait dispatches sent messages. A WM_PAINT dispatched there ran
/// <see cref="TerminalView.Render"/>, whose read lock queued behind the waiting writer on its own
/// stack: a self-deadlock, "Not Responding" at 0% CPU.
/// </para>
/// <para>
/// Headless Avalonia has no message pump, so the wait is given one: a synchronization context that
/// dispatches the paint from inside the wait, exactly where the Win32 one did. Driven through a
/// real window and layout pass rather than by calling Resize, so a blocking acquisition anywhere
/// on the view's resize path is caught, not only the one in the buffer. Ntilde.VT.Tests'
/// WriteLockReentrancyTests covers the buffer's other ways into the write lock.
/// </para>
/// </remarks>
public class TerminalViewResizeReentrancyTests
{
    [AvaloniaFact]
    [Trait("Category", "Regression")]
    public void AResizeThatWaitsOnAPtyBatch_DoesNotDispatchAPaintThatDeadlocksOnTheBuffer()
    {
        var buffer = new TerminalBuffer(80, 24);
        var view = new TerminalView();
        view.SetBuffer(buffer);

        var window = new Window { Content = view, Width = 800, Height = 400 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            (int colsBefore, _) = view.LastDispatchedGridForTest;
            Assert.True(colsBefore > 0, "nothing was dispatched on show");

            // Dispatch the next change inline, as a resize after a quiet spell does.
            view.ReleaseResizeThrottleForTest();

            bool? paintGotTheReadLock = null;
            using var ptyBatch = new PtyBatchStandIn(buffer);
            var win32Wait = new PumpingSynchronizationContext(sentMessage: () =>
            {
                ptyBatch.Release();
                // TerminalView.Render's read, minus the infinite wait.
                paintGotTheReadLock = buffer.Lock.TryEnterReadLock(TimeSpan.FromSeconds(1));
                if (paintGotTheReadLock == true) buffer.Lock.ExitReadLock();
            });

            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(win32Wait);
            try
            {
                // The view's own constraint rather than the window's: a headless window posts its
                // resize to the dispatcher, and running dispatcher jobs would swap this thread's
                // context back to Avalonia's. This keeps the whole layout pass - the part of the
                // WM_SIZE handler that reaches the buffer - on the pumping wait.
                view.Width = 400;
                window.UpdateLayout();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }

            ptyBatch.Join();
            Assert.True(
                ptyBatch.SawAWaitingWriter,
                "setup failed: the resize never queued behind the PTY batch, so nothing was tested");
            Assert.False(
                paintGotTheReadLock == false,
                "the resize dispatched a paint while it waited for the buffer's write lock, and the " +
                "paint could not take the read lock past its own thread's waiting writer - on the " +
                "UI thread, where Render waits without a timeout, the window hangs here");

            Assert.NotEqual(colsBefore, buffer.Cols);
            Assert.Equal((buffer.Cols, buffer.Rows), view.LastDispatchedGridForTest);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>
    /// The Win32 UI thread's wait, reduced to the part that matters: the CLR hands a blocking wait
    /// to a context that asks for wait notification, and this one dispatches one sent message - the
    /// paint - before waiting for real.
    /// </summary>
    private sealed class PumpingSynchronizationContext : SynchronizationContext
    {
        private readonly Action _sentMessage;
        private int _dispatched;

        public PumpingSynchronizationContext(Action sentMessage)
        {
            _sentMessage = sentMessage;
            SetWaitNotificationRequired();
        }

        public override int Wait(IntPtr[] waitHandles, bool waitAll, int millisecondsTimeout)
        {
            // The paint's own read wait comes back through here; dispatch it only once.
            if (Interlocked.Exchange(ref _dispatched, 1) == 0)
            {
                _sentMessage();
            }

            return WaitHelper(waitHandles, waitAll, millisecondsTimeout);
        }
    }

    /// <summary>
    /// Holds the write lock like the PTY thread's parser batch: until the UI thread has queued
    /// behind it, then until released or a short grace period passes (a fixed view never dispatches
    /// the paint that would release it).
    /// </summary>
    private sealed class PtyBatchStandIn : IDisposable
    {
        private readonly ManualResetEventSlim _held = new();
        private readonly ManualResetEventSlim _release = new();
        private readonly Thread _thread;

        public PtyBatchStandIn(TerminalBuffer buffer)
        {
            _thread = new Thread(() =>
            {
                buffer.Lock.EnterWriteLock();
                try
                {
                    _held.Set();
                    SawAWaitingWriter = SpinWait.SpinUntil(
                        () => buffer.Lock.WaitingWriteCount > 0, TimeSpan.FromSeconds(10));
                    _release.Wait(TimeSpan.FromMilliseconds(250));
                }
                finally
                {
                    buffer.Lock.ExitWriteLock();
                }
            })
            {
                IsBackground = true,
                Name = "PTY batch stand-in",
            };
            _thread.Start();
            _held.Wait();
        }

        public bool SawAWaitingWriter { get; private set; }

        public void Release() => _release.Set();

        public void Join() => _thread.Join();

        public void Dispose()
        {
            _release.Set();
            _thread.Join();
            _held.Dispose();
            _release.Dispose();
        }
    }
}
