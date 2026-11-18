using System;
using System.Collections.Generic;
using System.Linq;

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
    /// <summary>
    /// Where frames are retained for snapshots (persistent sessions) the frame's pixels, packed by <see cref="KittyFrameCodec"/>, as an entry of the shared
    /// image-source budget: evicted with it, and null here when this frame keeps its <see cref="Rgba"/> in memory instead.
    /// </summary>
    internal ImageSource? Source { get; set; }
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
    internal bool Waiting => _waiting;

    /// <summary>Sets the frames and playhead exactly as a snapshot carried them (#1017); the caller has validated every index.</summary>
    internal void Restore(List<KittyFrame> frames, KittyAnimationState state, int current, int displayed, int loopsRemaining, long frameStartMs, bool waiting)
    {
        Frames.Clear();
        Frames.AddRange(frames);
        (State, Current, Displayed, LoopsRemaining, FrameStartMs, _waiting) = (state, current, displayed, loopsRemaining, frameStartMs, waiting);
    }

    internal long ExtraCharge
    {
        get
        {
            long total = 0;
            foreach (KittyFrame frame in Frames) total += frame.Charge;
            return total;
        }
    }

    private static long Hold(KittyFrame frame)
    {
        if (frame.GapMs < 0) return 0;
        return frame.GapMs == 0 ? DefaultGapMs : frame.GapMs;
    }

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
        else if (_waiting && state == KittyAnimationState.Running)
        {
            // Parked on a frame whose gap has long run out: running continues past it from now, and the time spent parked is not playback.
            FrameStartMs = nowMs - Hold(Frames[Current]);
        }
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
        if (_waiting && !ResumeParked(nowMs)) return false;
        if (CycleMs() == 0)
        {
            // Every frame is gapless: nothing can be shown for a time, so the playhead cannot move.
            FrameStartMs = nowMs;
            return false;
        }
        SkipWholeCycles(nowMs);
        int limit = Frames.Count * 4 + 8;
        for (int steps = 0; steps < limit; steps++)
        {
            if (!StepOnce(nowMs)) break;
        }
        return Displayed != before;
    }

    /// <summary>
    /// The current frame's gap just changed: it applies from now on and cannot reach back, so a gap shortened below the time already spent
    /// ends the frame now (its successors start now) instead of being counted from the frame's old start.
    /// </summary>
    internal void RebaseCurrentGap(long nowMs) => FrameStartMs = Math.Max(FrameStartMs, nowMs - Hold(Frames[Current]));

    /// <summary>A frame arrived while parked: it is shown now and its gap counts from now, not from the old timeline.</summary>
    private bool ResumeParked(long nowMs)
    {
        if (Current + 1 >= Frames.Count) return false;
        _waiting = false;
        MoveTo(Current + 1);
        FrameStartMs = nowMs;
        return true;
    }

    /// <summary>One transition when the current frame's gap has run out; false once the playhead has settled at <paramref name="nowMs"/>.</summary>
    private bool StepOnce(long nowMs)
    {
        long hold = Hold(Frames[Current]);
        if (hold > 0 && nowMs - FrameStartMs < hold) return false;
        if (Current + 1 < Frames.Count)
        {
            FrameStartMs += hold;
            MoveTo(Current + 1);
            return true;
        }
        // Past the last frame.
        if (State == KittyAnimationState.Loading) { _waiting = true; return false; }
        if (LoopsRemaining == 0) { Stop(); return false; }
        if (LoopsRemaining > 0) LoopsRemaining--;
        FrameStartMs += hold;
        MoveTo(0);
        return true;
    }

    /// <summary>
    /// Loading mode parked on the last frame resumes the moment a frame arrives: it is shown at once and its gap counts from
    /// <paramref name="nowMs"/>, however late the next render is. Call it after settling the playhead to the same time.
    /// </summary>
    internal bool OnFrameAppended(long nowMs)
    {
        if (!_waiting || Current + 1 >= Frames.Count) return false;
        int before = Displayed;
        _waiting = false;
        MoveTo(Current + 1);
        FrameStartMs = nowMs;
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
        if (LoopsRemaining >= 0) whole = Math.Min(whole, LoopsRemaining);
        if (whole <= 0) return;                    // nothing to skip (including finite playback with no passes left): stepping decides
        FrameStartMs += whole * cycle;
        if (LoopsRemaining >= 0) LoopsRemaining -= (int)whole;
        // The skipped cycles passed through every visible frame, so what is on screen is the last visible frame at or before the playhead,
        // exactly as stepping through them one by one would have left it (a gapless frame is never displayed).
        Displayed = NearestVisibleAtOrBefore(Current);
    }

    private int NearestVisibleAtOrBefore(int index)
    {
        for (int back = 0; back < Frames.Count; back++)
        {
            int candidate = ((index - back) % Frames.Count + Frames.Count) % Frames.Count;
            if (Hold(Frames[candidate]) > 0) return candidate;
        }
        return index;
    }

    /// <summary>When the next frame change is due, or null when nothing is running.</summary>
    internal long? NextDueMs
    {
        get
        {
            // Nothing is due when the display cannot change: stopped, or fewer than two frames that are ever shown (a lone root, or all gapless).
            if (State == KittyAnimationState.Stopped || CycleMs() == 0 || Frames.Count(frame => Hold(frame) > 0) < 2) return null;
            if (_waiting) return Current + 1 < Frames.Count ? FrameStartMs : null;
            if (State == KittyAnimationState.Loading && Current + 1 >= Frames.Count && Hold(Frames[Current]) == 0) return null;
            return FrameStartMs + Math.Max(1, Hold(Frames[Current]));
        }
    }
}

/// <summary>A pixel rectangle inside an image.</summary>
internal readonly record struct KittyFrameRect(int X, int Y, int Width, int Height);

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
    /// Writes <paramref name="source"/> (<paramref name="bytesPerPixel"/> 3 or 4, top-down rows) over <paramref name="rect"/> of the canvas;
    /// the caller has checked it fits. <paramref name="overwrite"/> replaces canvas pixels, otherwise they are alpha-blended.
    /// </summary>
    internal static void Draw(byte[] canvas, int canvasWidth, byte[] source, int bytesPerPixel, KittyFrameRect rect, bool overwrite)
    {
        for (int row = 0; row < rect.Height; row++)
        {
            for (int col = 0; col < rect.Width; col++)
            {
                int s = (row * rect.Width + col) * bytesPerPixel;
                int d = ((rect.Y + row) * canvasWidth + rect.X + col) * 4;
                byte alpha = bytesPerPixel == 4 ? source[s + 3] : (byte)255;
                BlendPixel(canvas, d, source[s], source[s + 1], source[s + 2], alpha, overwrite);
            }
        }
    }

    /// <summary>
    /// Composes the <paramref name="sourceRect"/> of a whole straight-alpha RGBA frame (<paramref name="frameWidth"/> pixels wide) onto the canvas at
    /// (<paramref name="destX"/>, <paramref name="destY"/>); the caller has checked both rectangles fit and, for one frame, do not overlap.
    /// </summary>
    internal static void DrawFrameRect(byte[] canvas, byte[] sourceFrame, int frameWidth, KittyFrameRect sourceRect, int destX, int destY, bool overwrite)
    {
        for (int row = 0; row < sourceRect.Height; row++)
        {
            for (int col = 0; col < sourceRect.Width; col++)
            {
                int s = ((sourceRect.Y + row) * frameWidth + sourceRect.X + col) * 4;
                int d = ((destY + row) * frameWidth + destX + col) * 4;
                BlendPixel(canvas, d, sourceFrame[s], sourceFrame[s + 1], sourceFrame[s + 2], sourceFrame[s + 3], overwrite);
            }
        }
    }

    private static void BlendPixel(byte[] canvas, int d, byte sr, byte sg, byte sb, byte sa, bool overwrite)
    {
        if (overwrite || sa == 255)
        {
            canvas[d] = sr; canvas[d + 1] = sg; canvas[d + 2] = sb; canvas[d + 3] = sa;
            return;
        }
        if (sa == 0) return;
        int da = canvas[d + 3];
        // out alpha = sa + da*(1-sa); colors are un-premultiplied again by out alpha.
        int sourceWeight = sa * 255, destWeight = da * (255 - sa), total = sourceWeight + destWeight;
        if (total == 0) return;
        canvas[d] = Mix(sr, sourceWeight, canvas[d], destWeight, total);
        canvas[d + 1] = Mix(sg, sourceWeight, canvas[d + 1], destWeight, total);
        canvas[d + 2] = Mix(sb, sourceWeight, canvas[d + 2], destWeight, total);
        canvas[d + 3] = (byte)((total + 127) / 255);
    }

    private static byte Mix(int sourceColor, int sourceWeight, int destColor, int destWeight, int total) =>
        (byte)((sourceColor * sourceWeight + destColor * destWeight + total / 2) / total);
}
