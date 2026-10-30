using Ntilde.Platform.Ssh.Native;

namespace Ntilde.Platform.Tests.Ssh;

/// <summary>
/// #173: how the native SSH poll loops wait between events. They park in the native wait; only a wait that cannot park
/// falls back to the sleep the loops used to take.
/// </summary>
public sealed class NativeSshEventWaitTests
{
    private static readonly NovaSshSafeHandle Handle = new(new IntPtr(1), ownsHandle: false);

    [Fact]
    public void AReadyEvent_EndsThePark_WithoutASleep()
    {
        var interop = new NativeSshSessionTests.FakeNativeSshInterop { WaitBehavior = _ => true };

        Assert.False(NativeSshEventWait.Park(interop, Handle));
        Assert.Equal(1, interop.WaitCalls);
    }

    [Fact]
    public void AWaitThatTimesOut_IsNotFollowedByASleep()
    {
        // The normal idle case: nothing came within the bound, and the loop polls again at once.
        var interop = new NativeSshSessionTests.FakeNativeSshInterop
        {
            WaitBehavior = timeout =>
            {
                Thread.Sleep(timeout);
                return false;
            },
        };

        Assert.False(NativeSshEventWait.Park(interop, Handle));
    }

    [Fact]
    public void AWaitThatEndsAtOnceWithNothing_FallsBackToTheOldSleep_InsteadOfSpinning()
    {
        // A session the native side reports closed on an empty queue, or a handle already gone: the wait returns at
        // once, every time. Without the fallback the loop would spin a core.
        var interop = new NativeSshSessionTests.FakeNativeSshInterop { WaitBehavior = _ => false };
        var clock = System.Diagnostics.Stopwatch.StartNew();

        Assert.True(NativeSshEventWait.Park(interop, Handle));
        Assert.True(clock.Elapsed >= NativeSshEventWait.EarlyReturnBackoff - TimeSpan.FromMilliseconds(5), $"slept {clock.Elapsed}");
    }
}
