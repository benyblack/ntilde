using System;
using System.Linq;

namespace Ntilde.VT;

internal enum KittyFrameStoreResult { Stored, ImageGone, TooManyFrames, NoSpace, Rejected }

/// <summary>The <c>a=a</c> operations of one command, applied together in the order kitty documents them.</summary>
internal readonly record struct KittyAnimationControl(KittyAnimationState? State, uint Loops, uint GapFrame, int Gap, uint CurrentFrame);

public partial class TerminalBuffer
{
    /// <summary>
    /// Monotonic milliseconds the animation playhead is measured on. The live view and the parser share
    /// the buffer, so one clock orders <c>a=a</c> commands and presentation; tests substitute their own.
    /// </summary>
    public Func<long> KittyAnimationClock { get; set; } = static () => Environment.TickCount64;

    /// <summary>Frames in the image including the root (1 for a still image).</summary>
    internal static int KittyFrameCount(KittyImageAsset asset) => asset.Animation?.Frames.Count ?? 1;

    /// <summary>
    /// Appends <paramref name="frame"/>, or replaces frame <paramref name="replaceIndex"/> when it is
    /// nonnegative. Quota pressure from the new bytes evicts other assets exactly as a new image would;
    /// a frame that cannot fit even then is refused and nothing changes.
    /// </summary>
    internal KittyFrameStoreResult StoreKittyFrame(KittyImageAsset asset, KittyFrame frame, int replaceIndex)
    {
        bool lockTaken = EnterWriteLockIfNeeded();
        try
        {
            if (!_kittyImageAssets.Contains(asset)) return KittyFrameStoreResult.ImageGone;
            KittyAnimation animation = asset.Animation ??= new KittyAnimation(asset.Handle);
            bool replacing = replaceIndex >= 0;
            if (!replacing && animation.Frames.Count >= KittyAnimation.MaxFrames) return KittyFrameStoreResult.TooManyFrames;
            long resulting = asset.PixelCharge + frame.Charge - (replacing ? animation.Frames[replaceIndex].Charge : 0);
            if (resulting > MaxKittyImagePixelBytes) return KittyFrameStoreResult.NoSpace;
            if (!TryAdmitImageHandle(frame.Handle)) return KittyFrameStoreResult.Rejected;
            foreach (KittyImageAsset victim in KittyQuotaVictims(asset, resulting)) FreeKittyImage(victim);

            if (!replacing)
            {
                animation.Frames.Add(frame);
                return KittyFrameStoreResult.Stored;
            }
            KittyFrame old = animation.Frames[replaceIndex];
            animation.Frames[replaceIndex] = frame;
            if (replaceIndex == animation.Displayed) ShowKittyFrame(asset, frame);
            RetireUnstoredKittyHandle(old.Handle); // no placement or asset references it any more
            return KittyFrameStoreResult.Stored;
        }
        finally
        {
            ExitWriteLockIfNeeded(Lock, lockTaken);
            Invalidate();
        }
    }

    /// <summary>Applies <c>a=a</c>. Frame numbers are one-based and were validated by the caller.</summary>
    internal void ControlKittyAnimation(KittyImageAsset asset, KittyAnimationControl control)
    {
        bool lockTaken = EnterWriteLockIfNeeded();
        try
        {
            if (!_kittyImageAssets.Contains(asset)) return;
            KittyAnimation animation = asset.Animation ??= new KittyAnimation(asset.Handle);
            long now = KittyAnimationClock();
            if (control.GapFrame != 0 && control.Gap != 0) animation.Frames[(int)control.GapFrame - 1].GapMs = control.Gap;
            if (control.CurrentFrame != 0)
            {
                animation.SetCurrent((int)control.CurrentFrame - 1, now);
                ShowKittyFrame(asset, animation.Frames[animation.Displayed]);
            }
            animation.SetLoops(control.Loops);
            if (control.State is { } state)
            {
                if (state == KittyAnimationState.Stopped) animation.Stop();
                else animation.Start(state, now);
            }
        }
        finally
        {
            ExitWriteLockIfNeeded(Lock, lockTaken);
            Invalidate();
        }
    }

    /// <summary>
    /// Moves every running animation on the active screen to the frame <paramref name="nowMs"/> falls in.
    /// <paramref name="nextDueMs"/> is the earliest later change among animations that have a placement
    /// (nothing is gained by waking for one nobody can see), or null when none is pending.
    /// </summary>
    public bool AdvanceKittyAnimations(long nowMs, out long? nextDueMs, bool invalidate = true)
    {
        nextDueMs = null;
        bool changed = false;
        bool lockTaken = EnterWriteLockIfNeeded();
        try
        {
            foreach (KittyImageAsset asset in _kittyImageAssets.Where(item => item.IsAltScreen == _isAltScreen && item.Animation is not null).ToArray())
            {
                KittyAnimation animation = asset.Animation!;
                if (animation.Advance(nowMs))
                {
                    ShowKittyFrame(asset, animation.Frames[animation.Displayed]);
                    changed = true;
                }
                if (animation.NextDueMs is { } due && HasKittyPlacement(asset) && (nextDueMs is null || due < nextDueMs)) nextDueMs = due;
            }
        }
        finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
        // A caller inside a frame passes false: the frame it is drawing already shows the new pixels.
        if (changed && invalidate) Invalidate();
        return changed;
    }

    /// <summary>Points the asset and every placement of it at <paramref name="frame"/>'s pixels.</summary>
    private void ShowKittyFrame(KittyImageAsset asset, KittyFrame frame)
    {
        asset.Handle = frame.Handle;
        foreach (TerminalImage image in _images.Where(image => ReferenceEquals(image.KittyAsset, asset))) image.ImageHandle = frame.Handle;
    }
}
