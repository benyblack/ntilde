using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using Ntilde.VT;

namespace Ntilde.Shell.Native
{
    /// <summary>
    /// A synchronization context whose blocking waits dispatch nothing. Installed on a thread for
    /// the length of one lock wait, so the wait cannot run unrelated code on that thread while the
    /// thread is queued on the lock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The CLR hands a blocking wait to <see cref="SynchronizationContext.Wait"/> whenever the
    /// current context asks for wait notification, and on Windows an STA thread with no such
    /// context waits through COM's modal loop. Either way the Win32 UI thread dispatches messages
    /// sent to its windows - by another process's SetWindowPos, an UpdateWindow, an accessibility
    /// client - before it blocks, and whatever handles them runs on top of the waiting frame.
    /// </para>
    /// <para>
    /// That froze the UI thread for good (2026-10-06): a resize waiting for the buffer's write lock
    /// behind a PTY batch dispatched a WM_PAINT, whose read lock then queued behind the resize's
    /// own waiting writer. <see cref="Register"/> puts every blocking buffer write wait in one of
    /// these; see <see cref="TerminalBuffer.BlockingWriteWaitScope"/>.
    /// </para>
    /// <para>
    /// This is the same remedy Avalonia applies to its own synchronous commits (NonPumpingLockHelper)
    /// and that <c>Dispatcher.DisableProcessing</c> provides. Ours is used instead because headless
    /// Avalonia implements no non-pumping wait, so only this one can be shown working in a test
    /// (TerminalViewResizeReentrancyTests).
    /// </para>
    /// </remarks>
    internal sealed class NonPumpingSynchronizationContext : SynchronizationContext, IDisposable
    {
        private const uint WaitFailed = 0xFFFFFFFF;

        private readonly SynchronizationContext? _inner;

        private NonPumpingSynchronizationContext(SynchronizationContext? inner)
        {
            _inner = inner;
            SetWaitNotificationRequired();
        }

        /// <summary>
        /// Makes every blocking wait for a <see cref="TerminalBuffer"/> write lock non-pumping on
        /// the threads where it would otherwise pump. Process-wide; idempotent.
        /// </summary>
        public static void Register() => TerminalBuffer.BlockingWriteWaitScope = InstallIfWaitsMayPump;

        /// <summary>
        /// Installs a non-pumping context on the current thread if a wait there could dispatch
        /// messages, and returns it for the caller to dispose, which restores the previous one;
        /// otherwise installs nothing and returns <c>null</c>.
        /// </summary>
        public static NonPumpingSynchronizationContext? InstallIfWaitsMayPump()
        {
            var current = Current;
            if (current is NonPumpingSynchronizationContext)
            {
                return null;
            }

            // A context that asks to be told about waits can do anything in them; Avalonia's
            // dispatches sent messages. With no such context, only a Windows STA thread pumps -
            // the PTY and render threads are MTA and pay nothing beyond these two checks.
            bool waitsMayPump =
                current?.IsWaitNotificationRequired() == true ||
                (OperatingSystem.IsWindows() && Thread.CurrentThread.GetApartmentState() == ApartmentState.STA);

            if (!waitsMayPump)
            {
                return null;
            }

            var nonPumping = new NonPumpingSynchronizationContext(current);
            SetSynchronizationContext(nonPumping);
            return nonPumping;
        }

        /// <summary>
        /// Puts back the context that was current when this one was installed.
        /// </summary>
        public void Dispose() => SetSynchronizationContext(_inner);

        // Only the lock wait runs while this context is current, so nothing should capture it;
        // anything that does still reaches the thread's real context.
        public override void Post(SendOrPostCallback d, object? state)
        {
            if (_inner != null) _inner.Post(d, state);
            else base.Post(d, state);
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (_inner != null) _inner.Send(d, state);
            else base.Send(d, state);
        }

        public override int Wait(IntPtr[] waitHandles, bool waitAll, int millisecondsTimeout)
        {
            if (OperatingSystem.IsWindows())
            {
                // Straight to the kernel: WaitHelper would come back to the CLR's STA wait, which
                // is the pumping one.
                uint result = WaitForMultipleObjectsEx(
                    (uint)waitHandles.Length,
                    waitHandles,
                    waitAll ? 1 : 0,
                    unchecked((uint)millisecondsTimeout),
                    bAlertable: 0);

                // WaitHandle counts every result but a timeout as signalled, so a failure handed
                // back as a number would send the lock round its retry loop indefinitely.
                if (result == WaitFailed)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                return unchecked((int)result);
            }

            // Nothing pumps on a wait outside Windows; this only bypasses the inner context.
            return WaitHelper(waitHandles, waitAll, millisecondsTimeout);
        }

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern uint WaitForMultipleObjectsEx(
            uint nCount, IntPtr[] lpHandles, int bWaitAll, uint dwMilliseconds, int bAlertable);
    }
}
