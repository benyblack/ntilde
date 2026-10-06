using Ntilde.VT;

namespace Ntilde.VT.Tests;

// A hung Release build (2026-10-06) froze on its UI thread with this stack, bottom to top:
//
//   WM_SIZE -> layout -> TerminalView.OnSizeChanged -> SendThrottledResize -> TerminalBuffer.Resize
//     -> Lock.EnterWriteLock -> AvaloniaSynchronizationContext.Wait -> (sent message dispatched
//     during the wait) -> WM_PAINT -> TerminalView.Render -> Lock.EnterReadLock -> blocked forever
//
// The PTY thread held the write lock for a parser batch, so Resize queued as a waiting writer. On
// the STA UI thread the CLR hands that wait to the synchronization context, and Avalonia's waits
// dispatch sent messages (a cross-process SetWindowPos, a snapping tool, an accessibility client)
// before they block. A ReaderWriterLockSlim admits no new reader while a writer is waiting, and the
// waiting writer was the same thread, lower on its own stack - so the paint could never get its
// read lock, and the writer could never resume to take the write lock it had been granted.
//
// The paint's read lock is not the only casualty: Avalonia's WM_PAINT path then blocks on the
// render thread finishing the frame, and the render thread's snapshot read queues behind the same
// waiting writer. So the fix has to be on the writer's side: a thread waiting for the write lock
// must not dispatch anything while it waits.
//
// VT is a leaf with no native interop, so the non-dispatching wait itself is the host's
// (Ntilde.App's NonPumpingSynchronizationContext, proven in TerminalViewResizeReentrancyTests).
// What the buffer owns, and these tests pin for each way into the write lock the UI thread uses,
// is that a wait which has to block happens inside the host's TerminalBuffer.BlockingWriteWaitScope.
public class WriteLockReentrancyTests
{
    // Long enough that a read which is not starved gets in many times over; short enough that the
    // failing case (every read starved) costs a second per entry point rather than a hang.
    private static readonly TimeSpan ReentrantReadTimeout = TimeSpan.FromSeconds(1);

    private static readonly Dictionary<string, Action<TerminalBuffer>> WriteEntryPoints = new()
    {
        ["Resize"] = b => b.Resize(100, 30),
        ["Write"] = b => b.Write("x"),
        ["WriteChar"] = b => b.WriteChar('x'),
        // Reached on the UI thread by the "Debug: Box Drawing Test Screen" command.
        ["WriteContent"] = b => b.WriteContent("x"),
        ["BlockCursorSuppression"] = b => b.BlockCursorSuppression(TimeSpan.FromMilliseconds(10)),
        ["UpdateThemeColors"] = b => b.UpdateThemeColors(b.Theme),
    };

    public static TheoryData<string> WriteEntryPointNames => new(WriteEntryPoints.Keys);

    [Theory]
    [MemberData(nameof(WriteEntryPointNames))]
    [Trait("Category", "Regression")]
    public void WaitingForTheWriteLock_DoesNotDispatchAPaintThatNeedsTheReadLock(string entryPoint)
    {
        var buffer = new TerminalBuffer(80, 24);
        bool? reentrantReadGotIn = null;
        int hostScopesOpened = 0;

        var previousHostScope = TerminalBuffer.BlockingWriteWaitScope;
        TerminalBuffer.BlockingWriteWaitScope = () =>
        {
            hostScopesOpened++;
            return new NonDispatchingWaitScope();
        };

        using var ptyBatch = new PtyBatchStandIn(buffer);
        var uiThreadWait = new PumpingSynchronizationContext(sentMessage: () =>
        {
            // The PTY batch ends while the paint is being handled, as it did in the dump: from
            // here on nothing holds the lock, and only the waiting writer can keep readers out.
            ptyBatch.Release();

            // What TerminalView.Render does, minus the infinite wait.
            reentrantReadGotIn = buffer.Lock.TryEnterReadLock(ReentrantReadTimeout);
            if (reentrantReadGotIn == true) buffer.Lock.ExitReadLock();
        });

        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(uiThreadWait);
        try
        {
            WriteEntryPoints[entryPoint](buffer);

            Assert.Same(uiThreadWait, SynchronizationContext.Current);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
            TerminalBuffer.BlockingWriteWaitScope = previousHostScope;
        }

        ptyBatch.Join();
        Assert.True(
            ptyBatch.SawAWaitingWriter,
            $"setup failed: {entryPoint} never queued behind the PTY batch, so nothing was tested");
        Assert.True(
            hostScopesOpened > 0,
            $"{entryPoint} blocked on the write lock outside the host's BlockingWriteWaitScope");
        Assert.False(
            reentrantReadGotIn == false,
            $"{entryPoint} dispatched a paint while it waited for the write lock, and the paint " +
            "could not take the read lock: the only thing keeping it out was its own thread's " +
            "waiting writer. Render waits without a timeout, so on the UI thread this is a hang.");
    }

    [Fact]
    [Trait("Category", "Regression")]
    public void AResizeThatWaitedForTheWriteLock_StillAppliesTheNewSize()
    {
        var buffer = new TerminalBuffer(80, 24);

        using var ptyBatch = new PtyBatchStandIn(buffer);
        var uiThreadWait = new PumpingSynchronizationContext(sentMessage: ptyBatch.Release);

        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(uiThreadWait);
        try
        {
            buffer.Resize(100, 30);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        ptyBatch.Join();
        Assert.True(ptyBatch.SawAWaitingWriter, "setup failed: the resize never had to wait");
        Assert.Equal((100, 30), (buffer.Cols, buffer.Rows));
    }

    /// <summary>
    /// The UI thread's wait, reduced to the part that matters: the CLR hands every blocking wait on
    /// a thread whose context asks for wait notification to <see cref="Wait"/>, and the Win32 UI
    /// thread's wait dispatches sent messages before it blocks. This one dispatches exactly one -
    /// the paint - and then waits for real.
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
            // The paint's own wait for the read lock comes back through here; it must not
            // dispatch the paint a second time.
            if (Interlocked.Exchange(ref _dispatched, 1) == 0)
            {
                _sentMessage();
            }

            return WaitHelper(waitHandles, waitAll, millisecondsTimeout);
        }
    }

    /// <summary>
    /// The host's scope reduced to its effect: for the length of the wait, the thread's context is
    /// one whose waits dispatch nothing. (Ntilde.App's waits in the kernel; a test thread has no
    /// windows, so the plain WaitHelper already dispatches nothing here.)
    /// </summary>
    private sealed class NonDispatchingWaitScope : SynchronizationContext, IDisposable
    {
        private readonly SynchronizationContext? _previous = Current;

        public NonDispatchingWaitScope()
        {
            SetWaitNotificationRequired();
            SetSynchronizationContext(this);
        }

        public override int Wait(IntPtr[] waitHandles, bool waitAll, int millisecondsTimeout)
            => WaitHelper(waitHandles, waitAll, millisecondsTimeout);

        public void Dispose() => SetSynchronizationContext(_previous);
    }

    /// <summary>
    /// Holds the write lock the way the PTY processing thread does for a parser batch: until the
    /// thread under test has queued behind it as a waiting writer, and then until released - or,
    /// for a buffer that never dispatches the paint that would release it, a short grace period.
    /// </summary>
    private sealed class PtyBatchStandIn : IDisposable
    {
        private static readonly TimeSpan GracePeriod = TimeSpan.FromMilliseconds(250);

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
                    _release.Wait(GracePeriod);
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
