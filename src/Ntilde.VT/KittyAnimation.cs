using System;
using System.Collections.Generic;

namespace Ntilde.VT;

/// <summary>Kitty animation run state (<c>a=a,s=</c>): 1 stop, 2 run waiting for more frames, 3 run and loop.</summary>
internal enum KittyAnimationState { Stopped = 1, Loading = 2, Running = 3 }

/// <summary>
/// One frame of a Kitty animation. The root frame (index 0) borrows the asset's own handle and is
/// charged through it; later frames own their handle and the straight-alpha RGBA bytes they were
/// built from, so a later frame can be edited or used as a base.
/// </summary>
internal sealed class KittyFrame(object handle, byte[]? rgba, int gapMs, long charge)
{
    internal object Handle { get; set; } = handle;
    /// <summary>Straight-alpha RGBA, width*height*4. Null for the root frame, whose pixels are not retained.</summary>
    internal byte[]? Rgba { get; } = rgba;
    /// <summary>Milliseconds the frame is shown. Negative: gapless, skipped. Zero: use the default.</summary>
    internal int GapMs { get; set; } = gapMs;
    /// <summary>Retained bytes charged to the screen pixel quota (handle pixels plus <see cref="Rgba"/>).</summary>
    internal long Charge { get; } = charge;
}

/// <summary>
/// The frame list and playback clock of one retained Kitty image. Time is supplied by the caller, so
/// the state machine is deterministic; the live view feeds it a monotonic millisecond clock.
/// </summary>
internal sealed class KittyAnimation
{
    /// <summary>Frames per image including the root (a deliberate bound; kitty bounds frames by storage only).</summary>
    internal const int MaxFrames = 64;
    /// <summary>Gap of a frame sent without <c>z</c>, and the hold applied to a zero-gap frame such as the root.</summary>
    internal const int DefaultGapMs = 40;
    internal KittyAnimation(object rootHandle) => Frames.Add(new KittyFrame(rootHandle, null, 0, 0));

    internal List<KittyFrame> Frames { get; } = new();
    internal KittyAnimationState State { get; private set; } = KittyAnimationState.Stopped;
    /// <summary>Zero-based index of the frame the playhead is in; it may rest on a gapless frame the display skips.</summary>
    internal int Current { get; private set; }
    /// <summary>Zero-based index of the frame whose pixels are shown: gapless frames are passed through, never displayed.</summary>
    internal int Displayed { get; private set; }
    /// <summary>Passes left after the current one; negative is infinite.</summary>
    internal int LoopsRemaining { get; private set; } = -1;
    internal long FrameStartMs { get; private set; }
    /// <summary>Loading mode is parked on the last frame until another one is appended.</summary>
    private bool _waiting;

    internal long ExtraCharge
    {
        get
        {
            long total = 0;
            foreach (KittyFrame frame in Frames) total += frame.Charge;
            return total;
        }
    }

    private static long Hold(KittyFrame frame) => frame.GapMs < 0 ? 0 : frame.GapMs == 0 ? DefaultGapMs : frame.GapMs;

    private long CycleMs()
    {
        long total = 0;
        foreach (KittyFrame frame in Frames) total += Hold(frame);
        return total;
    }

    private void MoveTo(int index)
    {
        Current = index;
        if (Hold(Frames[index]) > 0) Displayed = index;
    }

    /// <summary><c>a=a,c=</c>: the client picks the frame, so it is shown even when its own gap is negative.</summary>
    internal void SetCurrent(int index, long nowMs)
    {
        Current = Displayed = index;
        FrameStartMs = nowMs;
        _waiting = false;
    }

    internal void Start(KittyAnimationState state, long nowMs)
    {
        if (State == KittyAnimationState.Stopped) FrameStartMs = nowMs;
        State = state;
        if (state == KittyAnimationState.Running) _waiting = false;
    }

    internal void Stop()
    {
        State = KittyAnimationState.Stopped;
        LoopsRemaining = -1;
        _waiting = false;
    }

    /// <summary><c>v=</c>: 0 is ignored, 1 loops forever, n plays the animation n-1 times in total.</summary>
    internal void SetLoops(uint v)
    {
        if (v == 0) return;
        LoopsRemaining = v == 1 ? -1 : (int)Math.Min(v - 2, int.MaxValue);
    }

    /// <summary>Moves the playhead to where <paramref name="nowMs"/> falls. True when the displayed frame changed.</summary>
    internal bool Advance(long nowMs)
    {
        if (State == KittyAnimationState.Stopped) return false;
        int before = Displayed;
        if (_waiting)
        {
            // A frame arrived while parked: it is shown now and its gap counts from now, not from the old timeline.
            if (Current + 1 >= Frames.Count) return false;
            _waiting = false;
            MoveTo(Current + 1);
            FrameStartMs = nowMs;
        }
        if (CycleMs() == 0)
        {
            // Every frame is gapless: nothing can be shown for a time, so the playhead cannot move.
            FrameStartMs = nowMs;
            return false;
        }
        SkipWholeCycles(nowMs);
        for (int steps = 0, limit = Frames.Count * 4 + 8; steps < limit; steps++)
        {
            long hold = Hold(Frames[Current]);
            if (hold > 0 && nowMs - FrameStartMs < hold) break;
            if (Current + 1 < Frames.Count)
            {
                FrameStartMs += hold;
                MoveTo(Current + 1);
                continue;
            }
            // Past the last frame.
            if (State == KittyAnimationState.Loading) { _waiting = true; break; }
            if (LoopsRemaining == 0) { Stop(); break; }
            if (LoopsRemaining > 0) LoopsRemaining--;
            FrameStartMs += hold;
            MoveTo(0);
        }
        return Displayed != before;
    }

    /// <summary>
    /// Whole cycles that elapsed since the current frame began leave the playhead where it is, so they are skipped in one step
    /// (a view resumed after hours does not replay them). Finite playback consumes the passes it skips and finishes below.
    /// </summary>
    private void SkipWholeCycles(long nowMs)
    {
        if (State != KittyAnimationState.Running) return;
        long cycle = CycleMs(), whole = (nowMs - FrameStartMs) / cycle;
        if (whole <= 0) return;
        if (LoopsRemaining >= 0) whole = Math.Min(whole, LoopsRemaining);
        FrameStartMs += whole * cycle;
        if (LoopsRemaining >= 0) LoopsRemaining -= (int)whole;
    }

    /// <summary>When the next frame change is due, or null when nothing is running.</summary>
    internal long? NextDueMs
    {
        get
        {
            if (State == KittyAnimationState.Stopped) return null;
            if (_waiting) return Current + 1 < Frames.Count ? FrameStartMs : null;
            if (State == KittyAnimationState.Loading && Current + 1 >= Frames.Count && Hold(Frames[Current]) == 0) return null;
            return FrameStartMs + Math.Max(1, Hold(Frames[Current]));
        }
    }
}

/// <summary>Straight-alpha RGBA composition of a frame rectangle onto a canvas.</summary>
internal static class KittyFrameCompositor
{
    internal static byte[] SolidCanvas(int width, int height, uint rgba)
    {
        var canvas = new byte[checked(width * height * 4)];
        if (rgba == 0) return canvas;
        byte r = (byte)(rgba >> 24), g = (byte)(rgba >> 16), b = (byte)(rgba >> 8), a = (byte)rgba;
        for (int i = 0; i < canvas.Length; i += 4) { canvas[i] = r; canvas[i + 1] = g; canvas[i + 2] = b; canvas[i + 3] = a; }
        return canvas;
    }

    /// <summary>
    /// Writes <paramref name="source"/> (<paramref name="bytesPerPixel"/> 3 or 4, top-down rows) over the
    /// rectangle at (<paramref name="x"/>, <paramref name="y"/>); the caller has checked it fits.
    /// <paramref name="overwrite"/> replaces canvas pixels, otherwise they are alpha-blended.
    /// </summary>
    internal static void Draw(byte[] canvas, int canvasWidth, byte[] source, int bytesPerPixel, int x, int y, int width, int height, bool overwrite)
    {
        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                int s = (row * width + col) * bytesPerPixel;
                int d = ((y + row) * canvasWidth + x + col) * 4;
                byte sr = source[s], sg = source[s + 1], sb = source[s + 2];
                byte sa = bytesPerPixel == 4 ? source[s + 3] : (byte)255;
                if (overwrite || sa == 255)
                {
                    canvas[d] = sr; canvas[d + 1] = sg; canvas[d + 2] = sb; canvas[d + 3] = sa;
                    continue;
                }
                if (sa == 0) continue;
                int da = canvas[d + 3];
                // out alpha = sa + da*(1-sa); colors are un-premultiplied again by out alpha.
                int outA255 = sa * 255 + da * (255 - sa);
                if (outA255 == 0) continue;
                canvas[d] = Mix(sr, sa * 255, canvas[d], da * (255 - sa), outA255);
                canvas[d + 1] = Mix(sg, sa * 255, canvas[d + 1], da * (255 - sa), outA255);
                canvas[d + 2] = Mix(sb, sa * 255, canvas[d + 2], da * (255 - sa), outA255);
                canvas[d + 3] = (byte)((outA255 + 127) / 255);
            }
        }
    }

    private static byte Mix(int sourceColor, int sourceWeight, int destColor, int destWeight, int total) =>
        (byte)((sourceColor * sourceWeight + destColor * destWeight + total / 2) / total);
}
