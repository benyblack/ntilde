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
    private const int MaxAdvanceSteps = 4096;

    internal KittyAnimation(object rootHandle) => Frames.Add(new KittyFrame(rootHandle, null, 0, 0));

    internal List<KittyFrame> Frames { get; } = new();
    internal KittyAnimationState State { get; private set; } = KittyAnimationState.Stopped;
    /// <summary>Zero-based index of the frame on display.</summary>
    internal int Current { get; private set; }
    /// <summary>Loops left after the current pass; negative is infinite.</summary>
    internal int LoopsRemaining { get; private set; } = -1;
    internal long FrameStartMs { get; private set; }

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

    internal void SetCurrent(int index, long nowMs)
    {
        Current = index;
        FrameStartMs = nowMs;
    }

    internal void Start(KittyAnimationState state, long nowMs)
    {
        if (State == KittyAnimationState.Stopped) FrameStartMs = nowMs;
        State = state;
    }

    internal void Stop()
    {
        State = KittyAnimationState.Stopped;
        LoopsRemaining = -1;
    }

    /// <summary><c>v=</c>: 0 is ignored, 1 loops forever, n loops n-1 more times.</summary>
    internal void SetLoops(uint v)
    {
        if (v == 0) return;
        LoopsRemaining = v == 1 ? -1 : (int)Math.Min(v - 1, int.MaxValue);
    }

    /// <summary>Moves the playhead to where <paramref name="nowMs"/> falls. True when the displayed frame changed.</summary>
    internal bool Advance(long nowMs)
    {
        if (State == KittyAnimationState.Stopped) return false;
        int before = Current;
        for (int steps = 0; ; steps++)
        {
            long hold = Hold(Frames[Current]);
            if (hold > 0 && nowMs - FrameStartMs < hold) break;
            if (steps >= MaxAdvanceSteps) { FrameStartMs = nowMs; break; }
            if (Current + 1 < Frames.Count)
            {
                FrameStartMs += hold;
                Current++;
                continue;
            }
            // Past the last frame.
            if (State == KittyAnimationState.Loading) break; // waits for more frames
            if (LoopsRemaining == 0) { Stop(); break; }
            if (LoopsRemaining > 0) LoopsRemaining--;
            FrameStartMs += hold;
            Current = 0;
            // A single skipped (gapless) frame loops forever without time passing; the step bound ends it.
        }
        return Current != before;
    }

    /// <summary>When the next frame change is due, or null when nothing is running.</summary>
    internal long? NextDueMs
    {
        get
        {
            if (State == KittyAnimationState.Stopped) return null;
            if (State == KittyAnimationState.Loading && Current + 1 >= Frames.Count) return null;
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
