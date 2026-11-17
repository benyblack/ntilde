using System;
using System.Collections.Generic;
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

    /// <summary>
    /// A host that replays recorded output sets this while playback is not running: the playheads hold where they are and nothing
    /// asks to be woken, instead of following the wall clock.
    /// </summary>
    public bool KittyAnimationPaused { get; set; }

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
            // Settle the playhead to now before anything can refuse or change the frame list, so even a refused frame leaves the display
            // where elapsed time puts it and a change cannot rewrite time already elapsed.
            long now = KittyAnimationClock();
            if (!KittyAnimationPaused && animation.Advance(now)) ShowKittyFrame(asset, animation.Frames[animation.Displayed]);
            bool replacing = replaceIndex >= 0;
            if (!replacing && animation.Frames.Count >= KittyAnimation.MaxFrames) return KittyFrameStoreResult.TooManyFrames;
            long resulting = asset.PixelCharge + frame.Charge - (replacing ? animation.Frames[replaceIndex].Charge : 0);
            if (resulting > MaxKittyImagePixelBytes) return KittyFrameStoreResult.NoSpace;
            if (!TryAdmitImageHandle(frame.Handle)) return KittyFrameStoreResult.Rejected;
            List<KittyImageAsset> victims = KittyQuotaVictims(asset, resulting);

            // Publish the frame's ownership before any other asset is freed: a host decoder that shares an opaque handle cannot then
            // see it retired while the frame already owns it (the order StoreKittyImage keeps for whole images).
            KittyFrame? old = null;
            if (!replacing)
            {
                animation.Frames.Add(frame);
                // A parked loading animation resumes from this arrival, not from whenever the next render happens.
                if (!KittyAnimationPaused && animation.OnFrameAppended(now)) ShowKittyFrame(asset, animation.Frames[animation.Displayed]);
            }
            else
            {
                old = animation.Frames[replaceIndex];
                animation.Frames[replaceIndex] = frame;
                if (replaceIndex == animation.Displayed) ShowKittyFrame(asset, frame);
            }
            foreach (KittyImageAsset victim in victims) FreeKittyImage(victim);
            if (old is not null) RetireUnstoredKittyHandle(old.Handle); // no placement or asset references it any more
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
            // Settle first: a gap, frame or state change applies from now on and does not reach back over elapsed time.
            if (!KittyAnimationPaused && animation.Advance(now)) ShowKittyFrame(asset, animation.Frames[animation.Displayed]);
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
    /// <paramref name="nextDueMs"/> is the earliest later change among animations with a visible placement, or null when none is pending:
    /// nothing is gained by waking for pixels nobody can see. <paramref name="firstVisibleLine"/> is the absolute line at the top of the viewport
    /// (null counts any placement on the screen as visible).
    /// </summary>
    public bool AdvanceKittyAnimations(long nowMs, out long? nextDueMs, bool invalidate = true, int? firstVisibleLine = null)
    {
        nextDueMs = null;
        if (KittyAnimationPaused) return false;
        bool changed = false;
        bool lockTaken = EnterWriteLockIfNeeded();
        try
        {
            foreach (KittyImageAsset asset in _kittyImageAssets.Where(item => item.IsAltScreen == _isAltScreen).ToArray())
            {
                if (asset.Animation is not { } animation) continue;
                if (animation.Advance(nowMs))
                {
                    ShowKittyFrame(asset, animation.Frames[animation.Displayed]);
                    changed = true;
                }
                if (animation.NextDueMs is { } due && HasVisibleKittyPlacement(asset, firstVisibleLine) && (nextDueMs is null || due < nextDueMs)) nextDueMs = due;
            }
        }
        finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
        // A caller inside a frame passes false: the frame it is drawing already shows the new pixels.
        if (changed && invalidate) Invalidate();
        return changed;
    }

    private bool HasVisibleKittyPlacement(KittyImageAsset asset, int? firstVisibleLine) =>
        _images.Any(image => ReferenceEquals(image.KittyAsset, asset)
            && (firstVisibleLine is not { } top || (image.CellY + image.CellHeight > top && image.CellY < top + Rows)));

    /// <summary>Points the asset and every placement of it at <paramref name="frame"/>'s pixels.</summary>
    private void ShowKittyFrame(KittyImageAsset asset, KittyFrame frame)
    {
        asset.Handle = frame.Handle;
        foreach (TerminalImage image in _images.Where(image => ReferenceEquals(image.KittyAsset, asset))) image.ImageHandle = frame.Handle;
    }
}
