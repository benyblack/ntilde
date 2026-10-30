using System;
using System.Diagnostics;
using System.Threading;

namespace Ntilde.Platform.Ssh.Native;

/// <summary>
/// How a native SSH poll loop waits when <see cref="INativeSshInterop.PollEvent"/> found nothing (#173): parked in
/// the native wait, so output reaches the terminal the moment the session queues it rather than up to a sleep later.
/// </summary>
/// <remarks>
/// The wait blocks, so the loops that use it run on threads of their own: a parked pool thread per SSH tab is the
/// starvation the #81 family was.
/// </remarks>
internal static class NativeSshEventWait
{
    /// <summary>
    /// The longest one park lasts. A stop request is seen within it, and a close's handle release, deferred while the
    /// wait holds the handle's reference, waits no longer.
    /// </summary>
    internal static readonly TimeSpan IdleWait = TimeSpan.FromMilliseconds(100);

    /// <summary>What the loops slept between polls before the native wait: the fallback when the wait cannot park.</summary>
    internal static readonly TimeSpan EarlyReturnBackoff = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// Parks until the session has an event to poll, closes, or <see cref="IdleWait"/> passes. Returns whether it fell
    /// back to sleeping instead.
    /// </summary>
    /// <remarks>
    /// A wait that ends early with nothing to poll means the native side reports the session closed on an empty queue,
    /// or the handle is already gone. Its worker never ends a session that way (it queues Closed first), but if one
    /// did, the loop would spin a core where it used to poll at 40 Hz; it sleeps as it used to instead.
    /// </remarks>
    internal static bool Park(INativeSshInterop interop, NovaSshSafeHandle handle)
    {
        long started = Stopwatch.GetTimestamp();
        if (interop.WaitForEvent(handle, IdleWait) || Stopwatch.GetElapsedTime(started) >= IdleWait / 2)
        {
            return false;
        }

        Thread.Sleep(EarlyReturnBackoff);
        return true;
    }
}
