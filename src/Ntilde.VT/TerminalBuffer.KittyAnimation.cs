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

    // True while any retained image has been addressed by a frame or control command. Read without the lock, so a buffer that never animates
    // costs the render path nothing (no lock, no clock).
    private volatile bool _hasKittyAnimations;

    /// <summary>Whether any retained image on either screen has an animation; false means rendering never needs to advance one.</summary>
    public bool HasKittyAnimations => _hasKittyAnimations;

    private const int AnimationLockRetryMs = 16;

    /// <summary>Frames in the image including the root (1 for a still image).</summary>
    internal static int KittyFrameCount(KittyImageAsset asset) => asset.Animation?.Frames.Count ?? 1;

    /// <summary>
    /// Whether a frame of <paramref name="charge"/> bytes would fit, answered before any pixels are decoded so a refused upload costs no bitmap.
    /// Stored means it fits now; <see cref="StoreKittyFrame"/> repeats the check under the same lock when it commits.
    /// </summary>
    internal KittyFrameStoreResult CheckKittyFrameRoom(KittyImageAsset asset, int replaceIndex, long charge)
    {
        bool lockTaken = EnterWriteLockIfNeeded(), moved = false;
        try
        {
            if (!_kittyImageAssets.Contains(asset)) return KittyFrameStoreResult.ImageGone;
            // Settle first, as every command does: even a refused frame leaves the display where elapsed time puts it.
            moved = SettleKittyPlayhead(asset);
            bool replacing = replaceIndex >= 0;
            int frames = KittyFrameCount(asset);
            if (!replacing && frames >= KittyAnimation.MaxFrames) return KittyFrameStoreResult.TooManyFrames;
            long replaced = replacing ? asset.Animation?.Frames[replaceIndex].Charge ?? 0 : 0;
            return asset.PixelCharge + charge - replaced > MaxKittyImagePixelBytes ? KittyFrameStoreResult.NoSpace : KittyFrameStoreResult.Stored;
        }
        finally
        {
            ExitWriteLockIfNeeded(Lock, lockTaken);
            if (moved) Invalidate();
        }
    }

    /// <summary>
    /// A fresh copy of frame <paramref name="index"/>'s straight-alpha RGBA (0 is the root), or null when it is not available. In an ordinary tab a frame keeps
    /// its pixels and the root's are read back from its decoded handle by <paramref name="decoder"/> under the lock, so the handle cannot be retired mid-read.
    /// Where frames are retained for snapshots (<paramref name="fromSources"/>, persistent sessions) no decoder is consulted: a frame's pixels are its packed
    /// retained source and the root's are its retained image bytes, so the daemon and every client decide alike and a source the budget evicted leaves the frame unavailable.
    /// </summary>
    internal byte[]? ReadKittyFramePixels(KittyImageAsset asset, int index, IImageDecoder? decoder, bool fromSources = false)
    {
        bool lockTaken = EnterWriteLockIfNeeded();
        try
        {
            if (!_kittyImageAssets.Contains(asset)) return null;
            KittyFrame? frame = asset.Animation is { } animation && index >= 0 && index < animation.Frames.Count ? animation.Frames[index] : null;
            if (frame?.Rgba is { } retained) return (byte[])retained.Clone();
            if (fromSources) return ReadKittyFramePixelsFromSources(asset, index, frame);
            if (index != 0 || decoder is null) return null;
            object root = frame?.Handle ?? asset.Handle;
            return decoder.TryReadPixels(root, out byte[] rgba, out int width, out int height) && width == asset.PixelWidth && height == asset.PixelHeight
                && rgba.LongLength == (long)width * height * 4 ? rgba : null;
        }
        finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
    }

    private static byte[]? ReadKittyFramePixelsFromSources(KittyImageAsset asset, int index, KittyFrame? frame)
    {
        // A frame with a source of its own (a later frame, or a root that was edited) is only what that source still holds: once evicted its pixels are
        // gone, and the retained image bytes must not stand in for an edit.
        if (frame?.Source is { } own)
            return own.Data is { } packed && KittyFrameCodec.TryDecode(packed, asset.PixelWidth * asset.PixelHeight, out byte[] pixels) ? pixels : null;
        if (index != 0 || asset.Source?.Data is not { } data || asset.Encoding is not { } encoding) return null;
        return RootPixelsFromRetainedBytes(asset, data, encoding);
    }

    /// <summary>The root image's RGBA from the bytes it was sent as: raw pixels directly, a PNG through <see cref="PngPixelReader"/>; any other container is not read here.</summary>
    private static byte[]? RootPixelsFromRetainedBytes(KittyImageAsset asset, byte[] data, KittyImageEncoding encoding)
    {
        if (RetainedKittyPixels(data, encoding) is not { } bytes) return null;
        if (!IsRaw(encoding))
            return PngPixelReader.TryRead(bytes, Math.Max(asset.PixelWidth, asset.PixelHeight), out int width, out int height, out byte[] png) && width == asset.PixelWidth && height == asset.PixelHeight ? png : null;
        int bytesPerPixel = RawBytesPerPixel(encoding), pixels = asset.PixelWidth * asset.PixelHeight;
        if (encoding.RawWidth != asset.PixelWidth || encoding.RawHeight != asset.PixelHeight || bytes.LongLength != (long)pixels * bytesPerPixel) return null;
        var rgba = new byte[pixels * 4];
        for (int i = 0; i < pixels; i++)
        {
            Array.Copy(bytes, i * bytesPerPixel, rgba, i * 4, 3);
            rgba[i * 4 + 3] = bytesPerPixel == 4 ? bytes[i * 4 + 3] : (byte)255;
        }
        return rgba;
    }

    /// <summary>
    /// Appends <paramref name="frame"/>, or replaces frame <paramref name="replaceIndex"/> when it is
    /// nonnegative. Quota pressure from the new bytes evicts other assets exactly as a new image would;
    /// a frame that cannot fit even then is refused and nothing changes.
    /// </summary>
    internal KittyFrameStoreResult StoreKittyFrame(KittyImageAsset asset, KittyFrame frame, int replaceIndex, byte[]? packed = null)
    {
        bool lockTaken = EnterWriteLockIfNeeded();
        try
        {
            if (!_kittyImageAssets.Contains(asset)) return KittyFrameStoreResult.ImageGone;
            KittyAnimation animation = asset.Animation ??= new KittyAnimation(asset.Handle);
            _hasKittyAnimations = true;
            // Settle the playhead to now before anything can refuse or change the frame list, so even a refused frame leaves the display
            // where elapsed time puts it and a change cannot rewrite time already elapsed.
            long now = KittyAnimationClock();
            SettleKittyPlayhead(asset, now);
            bool replacing = replaceIndex >= 0;
            if (!replacing && animation.Frames.Count >= KittyAnimation.MaxFrames) return KittyFrameStoreResult.TooManyFrames;
            long resulting = asset.PixelCharge + frame.Charge - (replacing ? animation.Frames[replaceIndex].Charge : 0);
            if (resulting > MaxKittyImagePixelBytes) return KittyFrameStoreResult.NoSpace;
            if (!TryAdmitImageHandle(frame.Handle)) return KittyFrameStoreResult.Rejected;
            List<KittyImageAsset> victims = KittyQuotaVictims(asset, resulting);
            // Where frames are retained for snapshots, the frame's packed pixels are charged to the image-source budget only after everything this frame
            // replaces or evicts has released its bytes, so the budget never evicts a surviving image's bytes to make room for a moment (as StoreKittyImage keeps).
            if (packed is not null)
            {
                foreach (KittyImageAsset victim in victims) ReleaseKittySources(victim);
                if (replacing) animation.Frames[replaceIndex].Source?.Release();
                frame.Source = ImageSources.Retain(packed, 4L * asset.PixelWidth * asset.PixelHeight);
            }

            // Publish the frame's ownership before any other asset is freed: a host decoder that shares an opaque handle cannot then
            // see it retired while the frame already owns it (the order StoreKittyImage keeps for whole images).
            KittyFrame? old = PublishKittyFrame(asset, animation, frame, replaceIndex, now);
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
            _hasKittyAnimations = true;
            long now = KittyAnimationClock();
            // Settle first: a gap, frame or state change applies from now on and does not reach back over elapsed time.
            SettleKittyPlayhead(asset, now);
            if (control.GapFrame != 0 && control.Gap != 0)
            {
                animation.Frames[(int)control.GapFrame - 1].GapMs = control.Gap;
                if ((int)control.GapFrame - 1 == animation.Current && !KittyAnimationPaused) animation.RebaseCurrentGap(now);
            }
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
    // S2222 reads ReaderWriterLockSlim.TryEnterWriteLock as always acquiring, so it reports the failed-acquire path as a leaked lock. Only
    // the success path holds the lock, and it releases it in the finally below.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Critical Bug", "S2222:Locks should be released on all exit paths",
        Justification = "A failed TryEnterWriteLock(0) takes nothing; a successful one is released in the finally of the same method.")]
    public bool AdvanceKittyAnimations(long nowMs, out long? nextDueMs, bool invalidate = true, int? firstVisibleLine = null)
    {
        nextDueMs = null;
        if (KittyAnimationPaused || !_hasKittyAnimations) return false;
        // The render path calls this: it must never queue behind sustained output as a waiting writer. If the lock is busy, try again next frame.
        bool changed;
        if (Lock.IsWriteLockHeld) changed = AdvanceKittyAnimationsLocked(nowMs, firstVisibleLine, out nextDueMs);   // the caller already owns the lock
        else if (Lock.TryEnterWriteLock(0))
        {
            try { changed = AdvanceKittyAnimationsLocked(nowMs, firstVisibleLine, out nextDueMs); }
            finally { Lock.ExitWriteLock(); }
        }
        else
        {
            nextDueMs = nowMs + AnimationLockRetryMs;
            return false;
        }
        // A caller inside a frame passes false: the frame it is drawing already shows the new pixels.
        if (changed && invalidate) Invalidate();
        return changed;
    }

    private bool AdvanceKittyAnimationsLocked(long nowMs, int? firstVisibleLine, out long? nextDueMs)
    {
        nextDueMs = null;
        bool changed = false;
        foreach (KittyImageAsset asset in _kittyImageAssets.Where(item => item.IsAltScreen == _isAltScreen).ToArray())
        {
            if (asset.Animation is not { } animation) continue;
            changed |= SettleKittyPlayhead(asset, nowMs);
            if (animation.NextDueMs is { } due && HasVisibleKittyPlacement(asset, firstVisibleLine) && (nextDueMs is null || due < nextDueMs)) nextDueMs = due;
        }
        return changed;
    }

    /// <summary>Settles to the clock's now; the clock is read only when the asset animates and the host is not holding it.</summary>
    private bool SettleKittyPlayhead(KittyImageAsset asset) =>
        !KittyAnimationPaused && asset.Animation is not null && SettleKittyPlayhead(asset, KittyAnimationClock());

    /// <summary>Moves the asset's playhead to <paramref name="nowMs"/> and shows the frame it lands on; true when the display changed. A held host never advances by time.</summary>
    private bool SettleKittyPlayhead(KittyImageAsset asset, long nowMs)
    {
        if (KittyAnimationPaused || asset.Animation is not { } animation || !animation.Advance(nowMs)) return false;
        ShowKittyFrame(asset, animation.Frames[animation.Displayed]);
        return true;
    }

    /// <summary>Adds the frame or puts it in place of the edited one, keeping the playhead consistent; returns the frame it replaced, if any.</summary>
    private KittyFrame? PublishKittyFrame(KittyImageAsset asset, KittyAnimation animation, KittyFrame frame, int replaceIndex, long now)
    {
        if (replaceIndex < 0)
        {
            animation.Frames.Add(frame);
            // A parked loading animation resumes from this arrival, not from whenever the next render happens.
            if (!KittyAnimationPaused && animation.OnFrameAppended(now)) ShowKittyFrame(asset, animation.Frames[animation.Displayed]);
            return null;
        }
        KittyFrame old = animation.Frames[replaceIndex];
        animation.Frames[replaceIndex] = frame;
        if (replaceIndex == animation.Displayed) ShowKittyFrame(asset, frame);
        // Only a gap that changed applies from now on: a replacement with the gap it had (a pixel-only edit, an a=c composition) leaves the playhead where it was.
        if (replaceIndex == animation.Current && !KittyAnimationPaused && old.GapMs != frame.GapMs) animation.RebaseCurrentGap(now);
        return old;
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
