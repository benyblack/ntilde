using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Ntilde.VT.Tests;

/// <summary>
/// Kitty animation (#988): <c>a=f</c> frame data, <c>a=a</c> control and the playhead. Deterministic
/// local tests on an injected clock and a decoder whose handles expose their RGBA; they are not
/// runtime qualification and do not establish painted pixels or elapsed wall-clock playback.
/// </summary>
public sealed class KittyAnimationTests
{
    private sealed record Pixels(byte[] Rgba, int Width, int Height);

    private sealed class PixelDecoder : IImageDecoder
    {
        /// <summary>A test container: a PNG signature and IHDR (what the header probe reads), then one RGBA color the whole image is filled with.</summary>
        public object? DecodeImageBytes(byte[] imageData, out int pixelWidth, out int pixelHeight)
        {
            pixelWidth = pixelHeight = 0;
            if (imageData.Length != 37 || !ImageHeaderProbe.TryGetSize(imageData, out _, out int width, out int height)) return null;
            (pixelWidth, pixelHeight) = (width, height);
            var rgba = new byte[width * height * 4];
            for (int i = 0; i < width * height; i++) Array.Copy(imageData, 33, rgba, i * 4, 4);
            return new Pixels(rgba, width, height);
        }

        public bool TryReadPixels(object handle, out byte[] rgba, out int pixelWidth, out int pixelHeight)
        {
            (rgba, pixelWidth, pixelHeight) = handle is Pixels pixels ? ((byte[])pixels.Rgba.Clone(), pixels.Width, pixels.Height) : ([], 0, 0);
            return handle is Pixels;
        }

        public object? DecodeSixel(string sixelData, out int pixelWidth, out int pixelHeight) { pixelWidth = pixelHeight = 0; return null; }
        public object? DecodeRawImage(byte[] data, int bytesPerPixel, int width, int height, out int pixelWidth, out int pixelHeight)
        {
            (pixelWidth, pixelHeight) = (width, height);
            var rgba = new byte[width * height * 4];
            for (int i = 0; i < width * height; i++)
            {
                Array.Copy(data, i * bytesPerPixel, rgba, i * 4, 3);
                rgba[i * 4 + 3] = bytesPerPixel == 4 ? data[i * bytesPerPixel + 3] : (byte)255;
            }
            return new Pixels(rgba, width, height);
        }
    }

    private static string B64(params byte[] bytes) => Convert.ToBase64String(bytes);
    private static string Command(string controls, string payload = "") => $"\x1b_G{controls};{payload}\x1b\\";

    private sealed class Harness
    {
        internal readonly TerminalBuffer Buffer = new(80, 24);
        internal readonly AnsiParser Parser;
        internal readonly List<string> Replies = new();
        internal long Now;

        internal Harness(int width = 2, int height = 1)
        {
            Parser = new AnsiParser(Buffer, forceConPtyFiltering: false) { ImageDecoder = new PixelDecoder(), CellWidth = 8, CellHeight = 16 };
            Parser.OnResponse = Replies.Add;
            Buffer.KittyAnimationClock = () => Now;
            // Root frame: opaque red | opaque blue, shown with a=T.
            Send($"a=T,f=32,s={width},v={height},i=1,C=1", B64(Enumerable.Range(0, width * height).SelectMany(i => i % 2 == 0 ? new byte[] { 255, 0, 0, 255 } : new byte[] { 0, 0, 255, 255 }).ToArray()));
            Replies.Clear();
        }

        internal void Send(string controls, string payload = "") => Parser.Process(Command(controls, payload));
        internal string Reply() { string reply = Assert.Single(Replies); Replies.Clear(); return reply; }
        internal object Shown => Assert.Single(Buffer.Images).ImageHandle;
        internal Pixels FramePixels(int index) => (Pixels)Buffer.FindKittyImage(1, 0)!.Animation!.Frames[index].Handle;
        internal bool Advance(long now) { Now = now; return Buffer.AdvanceKittyAnimations(now, out _); }
    }

    /// <summary>The test container <see cref="PixelDecoder"/> reads: a 33-byte PNG header for the size, then the fill color.</summary>
    private static string Png(int width, int height, byte r, byte g, byte b, byte a)
    {
        var bytes = new byte[37];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(bytes, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), (uint)width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), (uint)height);
        new[] { r, g, b, a }.CopyTo(bytes, 33);
        return B64(bytes);
    }

    private static string Solid(byte r, byte g, byte b, byte a, int pixels = 2) =>
        B64(Enumerable.Range(0, pixels).SelectMany(_ => new[] { r, g, b, a }).ToArray());

    [Fact]
    public void Frame_data_adds_a_frame_with_its_gap_and_acknowledges_it()
    {
        var h = new Harness();
        h.Send("a=f,i=1,f=32,s=2,v=1,z=120", Solid(0, 255, 0, 255));
        Assert.Equal("\x1b_Gi=1;OK\x1b\\", h.Reply());
        var animation = h.Buffer.FindKittyImage(1, 0)!.Animation!;
        Assert.Equal(2, animation.Frames.Count);
        Assert.Equal(120, animation.Frames[1].GapMs);
        Assert.Equal(new byte[] { 0, 255, 0, 255, 0, 255, 0, 255 }, h.FramePixels(1).Rgba);
        Assert.Equal(KittyAnimationState.Stopped, animation.State);
    }

    [Fact]
    public void A_frame_without_a_gap_uses_the_forty_millisecond_default()
    {
        var h = new Harness();
        h.Send("a=f,i=1,f=32,s=2,v=1", Solid(1, 2, 3, 255));
        Assert.Equal(40, h.Buffer.FindKittyImage(1, 0)!.Animation!.Frames[1].GapMs);
        h.Send("a=f,i=1,f=32,s=2,v=1,z=0", Solid(1, 2, 3, 255));
        Assert.Equal(40, h.Buffer.FindKittyImage(1, 0)!.Animation!.Frames[2].GapMs);
    }

    [Fact]
    public void A_rectangle_lands_where_asked_over_the_background_color()
    {
        var h = new Harness();
        // Background Y=0xff0000ff (opaque red); a green pixel at x=1.
        h.Send("a=f,i=1,f=32,s=1,v=1,x=1,Y=4278190335", Solid(0, 255, 0, 255, 1));
        Assert.Equal("\x1b_Gi=1;OK\x1b\\", h.Reply());
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 255, 0, 255 }, h.FramePixels(1).Rgba);
    }

    [Fact]
    public void Blend_composes_over_a_base_frame_and_overwrite_replaces_it()
    {
        var h = new Harness();
        h.Send("a=f,i=1,f=32,s=2,v=1", Solid(255, 0, 0, 255));        // frame 2: opaque red
        h.Replies.Clear();
        // Frame 3 on base frame 2: half-transparent white blended over opaque red.
        h.Send("a=f,i=1,f=32,s=1,v=1,c=2", Solid(255, 255, 255, 128, 1));
        Assert.Equal("\x1b_Gi=1;OK\x1b\\", h.Reply());
        byte[] blended = h.FramePixels(2).Rgba;
        Assert.Equal(255, blended[0]);
        Assert.InRange(blended[1], 126, 129);
        Assert.InRange(blended[2], 126, 129);
        Assert.Equal(255, blended[3]);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, blended[4..8]);   // untouched pixel keeps the base
        // The same data with X=1 replaces the pixel, alpha included.
        h.Send("a=f,i=1,f=32,s=1,v=1,c=2,X=1", Solid(255, 255, 255, 128, 1));
        Assert.Equal(new byte[] { 255, 255, 255, 128 }, h.FramePixels(3).Rgba[0..4]);
    }

    [Fact]
    public void Editing_a_frame_replaces_it_in_place_keeps_its_gap_and_retires_the_old_pixels()
    {
        var h = new Harness();
        h.Send("a=f,i=1,f=32,s=2,v=1,z=77", Solid(255, 0, 0, 255));
        h.Replies.Clear();
        object old = h.FramePixels(1);
        h.Send("a=f,i=1,f=32,s=1,v=1,r=2,X=1", Solid(0, 255, 0, 255, 1));
        Assert.Equal("\x1b_Gi=1;OK\x1b\\", h.Reply());
        var animation = h.Buffer.FindKittyImage(1, 0)!.Animation!;
        Assert.Equal(2, animation.Frames.Count);
        Assert.Equal(77, animation.Frames[1].GapMs);
        Assert.Equal(new byte[] { 0, 255, 0, 255, 255, 0, 0, 255 }, h.FramePixels(1).Rgba);
        var retired = new List<object>(); h.Buffer.DrainRetiredImageHandles(retired, long.MaxValue);
        Assert.Contains(old, retired);
    }

    [Theory]
    [InlineData("a=f,i=1,f=32,s=2,v=1,c=9", "ENOENT")]
    [InlineData("a=f,i=1,f=32,s=2,v=1,r=9", "ENOENT")]
    [InlineData("a=f,i=1,f=32,s=2,v=1,x=1", "EINVAL")]            // rectangle leaves the 2x1 image
    [InlineData("a=f,i=1,f=32,s=2,v=2", "EINVAL")]
    [InlineData("a=f,i=1,f=32,s=2,v=1,X=2", "EINVAL")]
    [InlineData("a=f,i=1,f=32,s=2,v=1,z=abc", "EINVAL")]
    [InlineData("a=f,i=1,f=100", "EINVAL")]                        // not an image container
    [InlineData("a=f,i=1,s=2,v=1", "ENOTSUP")]                      // f absent is not assumed
    [InlineData("a=f,i=2,f=32,s=2,v=1", "ENOENT")]                  // unknown image
    [InlineData("a=a,i=2,s=3", "ENOENT")]
    [InlineData("a=a,i=1,s=4", "EINVAL")]
    [InlineData("a=a,i=1,c=2", "ENOENT")]                           // no second frame yet
    [InlineData("a=a,i=1,r=2,z=10", "ENOENT")]
    public void Invalid_requests_get_a_correlated_negative_reply_and_change_nothing(string controls, string code)
    {
        var h = new Harness();
        h.Send(controls, Solid(1, 2, 3, 255));
        string reply = h.Reply();
        Assert.Contains($";{code}:", reply);
        Assert.Equal(1, TerminalBuffer.KittyFrameCount(h.Buffer.FindKittyImage(1, 0)!));
    }

    [Fact]
    public void The_root_frame_is_a_base_for_a_later_frame()
    {
        var h = new Harness();                                                          // root: red | blue
        h.Send("a=f,i=1,f=32,s=1,v=1,x=1,c=1", Solid(0, 255, 0, 255, 1));              // green over the root's right pixel
        Assert.Equal("\x1b_Gi=1;OK\x1b\\", h.Reply());
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 255, 0, 255 }, h.FramePixels(1).Rgba);
        h.Send("a=f,i=1,f=32,s=1,v=1,c=1,X=1", Solid(255, 255, 255, 128, 1));          // overwrite replaces the pixel, alpha included
        Assert.Equal(new byte[] { 255, 255, 255, 128, 0, 0, 255, 255 }, h.FramePixels(2).Rgba);
    }

    [Fact]
    public void Editing_the_root_frame_replaces_its_pixels_and_keeps_its_gap()
    {
        var h = new Harness();
        object original = h.Shown;
        h.Send("a=a,i=1,r=1,z=300"); h.Replies.Clear();
        h.Send("a=f,i=1,f=32,s=2,v=1,r=1", Solid(9, 8, 7, 255));
        Assert.Equal("\x1b_Gi=1;OK\x1b\\", h.Reply());
        var animation = h.Buffer.FindKittyImage(1, 0)!.Animation!;
        Assert.Single(animation.Frames);
        Assert.Equal(300, animation.Frames[0].GapMs);
        Assert.NotSame(original, h.Shown);                                              // the displayed root now draws the edit
        Assert.Equal(new byte[] { 9, 8, 7, 255, 9, 8, 7, 255 }, h.FramePixels(0).Rgba);
        // The decoded pixels were already charged to the asset; only the retained RGBA is new (2x1 image: 8 bytes each).
        Assert.Equal(8 + 8, h.Buffer.FindKittyImage(1, 0)!.PixelCharge);
        var retired = new List<object>(); h.Buffer.DrainRetiredImageHandles(retired, long.MaxValue);
        Assert.Contains(original, retired);
        h.Send("a=f,i=1,f=32,s=1,v=1,x=1,c=1", Solid(1, 2, 3, 255, 1));                // a later frame is built on the edited root
        Assert.Equal(new byte[] { 9, 8, 7, 255, 1, 2, 3, 255 }, h.FramePixels(1).Rgba);
    }

    [Fact]
    public void A_decoder_that_cannot_read_pixels_leaves_the_root_unusable_as_a_base()
    {
        var h = new Harness();
        h.Parser.ImageDecoder = new RawOnlyDecoder();
        h.Send("a=f,i=1,f=32,s=1,v=1,c=1", Solid(1, 2, 3, 255, 1));
        Assert.Contains(";ENOTSUP:Root frame pixels are unavailable", h.Reply());
        h.Send("a=f,i=1,f=32,s=1,v=1", Solid(1, 2, 3, 255, 1));                          // a frame that needs no base is fine
        Assert.Equal("\x1b_Gi=1;OK\x1b\\", h.Reply());
    }

    [Fact]
    public void A_container_frame_is_decoded_to_pixels_and_composed()
    {
        var h = new Harness();
        h.Send("a=f,i=1,f=100,x=1,z=90", Png(1, 1, 0, 0, 255, 255));                    // a 1x1 blue PNG at x=1, over a transparent canvas
        Assert.Equal("\x1b_Gi=1;OK\x1b\\", h.Reply());
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 0, 255, 255 }, h.FramePixels(1).Rgba);
        Assert.Equal(90, h.Buffer.FindKittyImage(1, 0)!.Animation!.Frames[1].GapMs);
        h.Send("a=f,i=1,f=100,c=1", Png(1, 1, 0, 255, 0, 255));                         // a PNG over the root: green at the origin
        Assert.Equal(new byte[] { 0, 255, 0, 255, 0, 0, 255, 255 }, h.FramePixels(2).Rgba);
    }

    [Theory]
    [InlineData("a=f,i=1,f=100,x=1", 2, 1)]                                             // a 2x1 PNG at x=1 leaves the 2x1 image
    [InlineData("a=f,i=1,f=100", 3, 1)]
    [InlineData("a=f,i=1,f=100,x=2147483648", 1, 1)]                                   // an offset that would wrap negative as an int
    [InlineData("a=f,i=1,f=32,s=1,v=1,y=4294967295", 1, 1)]
    public void A_container_frame_larger_than_its_place_is_refused_before_decoding(string controls, int width, int height)
    {
        var h = new Harness();
        var counting = new CountingDecoder();
        h.Parser.ImageDecoder = counting;
        h.Send(controls, Png(width, height, 1, 2, 3, 255));
        Assert.Contains(";EINVAL:", h.Reply());
        Assert.Equal(0, counting.Decodes);
        Assert.Equal(1, TerminalBuffer.KittyFrameCount(h.Buffer.FindKittyImage(1, 0)!));
    }

    [Fact]
    public void A_container_frame_needs_a_decoder_that_exposes_pixels()
    {
        var h = new Harness();
        h.Parser.ImageDecoder = new RawOnlyDecoder();
        h.Send("a=f,i=1,f=100", Png(1, 1, 1, 2, 3, 255));
        Assert.Contains(";ENOTSUP:", h.Reply());
        h.Parser.ImageDecoder = null;
        h.Send("a=f,i=1,f=100", Png(1, 1, 1, 2, 3, 255));
        Assert.Contains(";ENOTSUP:", h.Reply());
        Assert.Equal(1, TerminalBuffer.KittyFrameCount(h.Buffer.FindKittyImage(1, 0)!));
    }

    [Fact]
    public void A_payload_that_does_not_match_its_dimensions_is_rejected()
    {
        var h = new Harness();
        h.Send("a=f,i=1,f=32,s=2,v=1", Solid(1, 2, 3, 255, 1));
        Assert.Contains(";EINVAL:", h.Reply());
        Assert.Equal(1, TerminalBuffer.KittyFrameCount(h.Buffer.FindKittyImage(1, 0)!));
    }

    [Fact]
    public void Raw_rgb_frames_are_opaque()
    {
        var h = new Harness();
        h.Send("a=f,i=1,f=24,s=2,v=1", B64(1, 2, 3, 4, 5, 6));
        Assert.Equal("\x1b_Gi=1;OK\x1b\\", h.Reply());
        Assert.Equal(new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 }, h.FramePixels(1).Rgba);
    }

    [Fact]
    public void Chunked_frame_data_is_assembled_before_it_is_stored()
    {
        var h = new Harness();
        string all = Solid(9, 8, 7, 255);
        h.Send("a=f,i=1,f=32,s=2,v=1,m=1", all[..4]);
        Assert.Empty(h.Replies);
        h.Send("a=f,m=0", all[4..]);
        Assert.Equal("\x1b_Gi=1;OK\x1b\\", h.Reply());
        Assert.Equal(new byte[] { 9, 8, 7, 255, 9, 8, 7, 255 }, h.FramePixels(1).Rgba);
    }

    [Fact]
    public void Quiet_policy_applies_to_animation_commands()
    {
        var h = new Harness();
        h.Send("a=f,i=1,f=32,s=2,v=1,q=1", Solid(1, 2, 3, 255));
        h.Send("a=a,i=1,s=3,q=1");
        Assert.Empty(h.Replies);
        h.Send("a=a,i=9,s=3,q=1");
        Assert.Contains(";ENOENT:", h.Reply());
        h.Send("a=a,i=9,s=3,q=2");
        Assert.Empty(h.Replies);
    }

    [Fact]
    public void Frame_count_and_pixel_quota_are_bounded_without_partial_effects()
    {
        var h = new Harness();
        for (int i = 2; i <= KittyAnimation.MaxFrames; i++) h.Send("a=f,i=1,f=32,s=2,v=1", Solid(1, 1, 1, 255));
        Assert.All(h.Replies, reply => Assert.Equal("\x1b_Gi=1;OK\x1b\\", reply));
        h.Replies.Clear();
        h.Send("a=f,i=1,f=32,s=2,v=1", Solid(1, 1, 1, 255));
        Assert.Contains(";ENOSPC:", h.Reply());
        Assert.Equal(KittyAnimation.MaxFrames, TerminalBuffer.KittyFrameCount(h.Buffer.FindKittyImage(1, 0)!));
    }

    [Fact]
    public void A_frame_larger_than_the_screen_quota_is_refused_and_evicts_nothing()
    {
        var h = new Harness(width: 1500, height: 1500);   // 9 MB root; each frame charges 18 MB
        h.Send("a=t,f=32,s=1,v=1,i=2", Solid(1, 2, 3, 255, 1));
        h.Replies.Clear();
        string frame = Solid(5, 5, 5, 255, 1500 * 1500);
        for (int i = 0; i < 3; i++) h.Send("a=f,i=1,f=32,s=1500,v=1500", frame);
        Assert.All(h.Replies, reply => Assert.Equal("\x1b_Gi=1;OK\x1b\\", reply));
        h.Replies.Clear();
        h.Send("a=f,i=1,f=32,s=1500,v=1500", frame);                 // 9 + 4*18 MB > 64 MiB
        Assert.Contains(";ENOSPC:", h.Reply());
        Assert.Equal(4, TerminalBuffer.KittyFrameCount(h.Buffer.FindKittyImage(1, 0)!));
        Assert.NotNull(h.Buffer.FindKittyImage(2, 0));            // the other image was not evicted for nothing
    }

    // ---- playback ----------------------------------------------------------------------------

    private static Harness ThreeFrames()
    {
        var h = new Harness();
        h.Send("a=f,i=1,f=32,s=2,v=1,z=100", Solid(0, 255, 0, 255));
        h.Send("a=f,i=1,f=32,s=2,v=1,z=200", Solid(255, 255, 0, 255));
        h.Send("a=a,i=1,r=1,z=50");                                   // root frame shows for 50 ms
        h.Replies.Clear();
        return h;
    }

    private static int Index(Harness h) => h.Buffer.FindKittyImage(1, 0)!.Animation!.Current;

    [Fact]
    public void A_stopped_animation_never_moves_however_much_time_passes()
    {
        var h = ThreeFrames();
        object root = h.Shown;
        Assert.False(h.Advance(60_000));
        Assert.Same(root, h.Shown);
        Assert.Equal(0, Index(h));
    }

    [Fact]
    public void Running_walks_the_frames_on_their_gaps_and_loops_forever()
    {
        var h = ThreeFrames();
        h.Now = 1000;
        h.Send("a=a,i=1,s=3");
        Assert.Equal("\x1b_Gi=1;OK\x1b\\", h.Reply());
        object root = h.Shown;
        Assert.False(h.Advance(1049));                    // root: 50 ms
        Assert.Same(root, h.Shown);
        Assert.True(h.Advance(1050));
        Assert.Equal(1, Index(h)); Assert.Same(h.FramePixels(1), h.Shown);
        Assert.False(h.Advance(1149));
        Assert.True(h.Advance(1150));                     // frame 2: 100 ms
        Assert.Equal(2, Index(h)); Assert.Same(h.FramePixels(2), h.Shown);
        Assert.False(h.Advance(1349));
        Assert.True(h.Advance(1350));                     // frame 3: 200 ms, then back to the root
        Assert.Equal(0, Index(h)); Assert.Same(root, h.Shown);
        Assert.True(h.Advance(1400 + 350 * 1000));        // many cycles later still lands on a real boundary
        Assert.InRange(Index(h), 0, 2);
    }

    [Fact]
    public void A_late_clock_catches_up_through_several_frames_in_order()
    {
        var h = ThreeFrames();
        h.Now = 0; h.Send("a=a,i=1,s=3"); h.Replies.Clear();
        Assert.True(h.Advance(170));                      // 50 + 100 passed, 20 into frame 3
        Assert.Equal(2, Index(h));
    }

    [Fact]
    public void A_finite_loop_count_plays_the_animation_n_minus_one_times_then_stops_on_the_last_frame()
    {
        var h = ThreeFrames();
        h.Now = 0; h.Send("a=a,i=1,s=3,v=2"); h.Replies.Clear();    // v=2 is one pass in total
        h.Advance(349);
        Assert.Equal(2, Index(h));
        h.Advance(350);                                    // end of the only pass
        var animation = h.Buffer.FindKittyImage(1, 0)!.Animation!;
        Assert.Equal(KittyAnimationState.Stopped, animation.State);
        Assert.Equal(2, animation.Displayed);
        Assert.False(h.Advance(99_999));

        var again = ThreeFrames();
        again.Now = 0; again.Send("a=a,i=1,s=3,v=3"); again.Replies.Clear();   // two passes
        again.Advance(350);
        Assert.Equal(0, Index(again));                     // the second pass begins
        Assert.Equal(KittyAnimationState.Running, again.Buffer.FindKittyImage(1, 0)!.Animation!.State);
        again.Advance(700);
        Assert.Equal(KittyAnimationState.Stopped, again.Buffer.FindKittyImage(1, 0)!.Animation!.State);
        Assert.Equal(2, again.Buffer.FindKittyImage(1, 0)!.Animation!.Displayed);
    }

    [Fact]
    public void Loop_value_zero_is_ignored_one_is_infinite_and_n_is_n_minus_one_passes()
    {
        var h = ThreeFrames();
        h.Now = 0; h.Send("a=a,i=1,s=3,v=0"); h.Replies.Clear();
        Assert.Equal(-1, h.Buffer.FindKittyImage(1, 0)!.Animation!.LoopsRemaining);
        h.Send("a=a,i=1,v=3");
        Assert.Equal(1, h.Buffer.FindKittyImage(1, 0)!.Animation!.LoopsRemaining);   // one pass after the current one
        h.Send("a=a,i=1,v=2");
        Assert.Equal(0, h.Buffer.FindKittyImage(1, 0)!.Animation!.LoopsRemaining);
        h.Send("a=a,i=1,v=1");
        Assert.Equal(-1, h.Buffer.FindKittyImage(1, 0)!.Animation!.LoopsRemaining);
    }

    [Fact]
    public void A_clock_that_jumps_far_ahead_lands_exactly_where_elapsed_time_says_without_replaying()
    {
        var h = ThreeFrames();                             // holds 50, 100, 200: a 350 ms cycle
        h.Now = 0; h.Send("a=a,i=1,s=3"); h.Replies.Clear();
        foreach (long elapsed in new long[] { 200_000, 1_000_000_000_000, 349, 350 * 5000 + 49, 350 * 5000 + 50, 350 * 5000 + 149, 350 * 5000 + 150 })
        {
            var run = ThreeFrames();
            run.Now = 0; run.Send("a=a,i=1,s=3"); run.Replies.Clear();
            run.Advance(elapsed);
            long phase = elapsed % 350;
            Assert.Equal(phase < 50 ? 0 : phase < 150 ? 1 : 2, Index(run));
            Assert.Equal(KittyAnimationState.Running, run.Buffer.FindKittyImage(1, 0)!.Animation!.State);
        }
        var finite = ThreeFrames();
        finite.Now = 0; finite.Send("a=a,i=1,s=3,v=3"); finite.Replies.Clear();
        finite.Advance(1_000_000_000_000);                // far past two passes
        var animation = finite.Buffer.FindKittyImage(1, 0)!.Animation!;
        Assert.Equal(KittyAnimationState.Stopped, animation.State);
        Assert.Equal(2, animation.Displayed);
        Assert.Equal(-1, animation.LoopsRemaining);
    }

    [Fact]
    public void A_catch_up_after_a_long_gap_is_one_step_not_thousands_of_replays()
    {
        var h = new Harness();
        h.Send("a=f,i=1,f=32,s=2,v=1,z=40", Solid(0, 255, 0, 255));
        h.Send("a=f,i=1,f=32,s=2,v=1,z=40", Solid(0, 0, 255, 255));
        h.Send("a=a,i=1,r=1,z=40"); h.Replies.Clear();    // three 40 ms frames: a 120 ms cycle
        h.Now = 0; h.Send("a=a,i=1,s=3"); h.Replies.Clear();
        h.Advance(200_000);                                // more than 4096 transitions later
        Assert.Equal(200_000 % 120 < 40 ? 0 : 200_000 % 120 < 80 ? 1 : 2, Index(h));
    }

    [Fact]
    public void Loading_resumes_from_the_moment_a_frame_arrives_not_from_the_stale_timeline()
    {
        var h = ThreeFrames();
        h.Now = 0; h.Send("a=a,i=1,s=2"); h.Replies.Clear();
        Assert.True(h.Advance(10_000));
        Assert.False(h.Advance(100_000));                  // parked on the last frame for a long time
        h.Now = 100_000; h.Send("a=f,i=1,f=32,s=2,v=1,z=100", Solid(0, 0, 255, 255));
        h.Replies.Clear();
        Assert.Equal(3, Index(h));                         // the new frame is shown at once, at its arrival
        Assert.False(h.Advance(100_000));
        h.Now = 100_019; h.Send("a=f,i=1,f=32,s=2,v=1,z=100", Solid(255, 0, 255, 255));
        h.Replies.Clear();
        Assert.False(h.Advance(100_019));                  // the first new frame has been up for 19 ms of its 100
        Assert.Equal(3, Index(h));
        Assert.True(h.Advance(100_100));
        Assert.Equal(4, Index(h));
    }

    [Fact]
    public void A_trailing_gapless_frame_is_never_displayed_even_when_loading_mode_parks_on_it()
    {
        var h = new Harness();
        h.Send("a=f,i=1,f=32,s=2,v=1,z=100", Solid(0, 255, 0, 255));
        h.Send("a=f,i=1,f=32,s=2,v=1,z=-1", Solid(0, 0, 255, 255));
        h.Send("a=a,i=1,r=1,z=50"); h.Replies.Clear();
        h.Now = 0; h.Send("a=a,i=1,s=2"); h.Replies.Clear();
        h.Advance(10_000);
        var animation = h.Buffer.FindKittyImage(1, 0)!.Animation!;
        Assert.Equal(2, animation.Current);                // the playhead rests on the helper frame
        Assert.Equal(1, animation.Displayed);              // but the visible frame is the last one with a gap
        Assert.Same(h.FramePixels(1), h.Shown);
        Assert.Null(NextDue(h));
    }

    [Fact]
    public void A_gap_change_applies_from_now_and_cannot_rewrite_time_that_already_elapsed()
    {
        var h = new Harness();
        h.Send("a=f,i=1,f=32,s=2,v=1,z=100", Solid(0, 255, 0, 255));
        h.Send("a=a,i=1,r=1,z=100"); h.Replies.Clear();        // two 100 ms frames
        h.Now = 0; h.Send("a=a,i=1,s=3"); h.Replies.Clear();
        h.Now = 150;                                          // no render happened since t=0
        h.Send("a=a,i=1,r=1,z=200"); h.Replies.Clear();       // the root gap grows to 200 ms
        Assert.Equal(1, Index(h));                            // the root's first 100 ms had already passed: frame 2 is up
        Assert.Same(h.FramePixels(1), h.Shown);
        h.Advance(199);
        Assert.Equal(1, Index(h));
        h.Advance(200);                                       // frame 2 ran 100..200, then the root again
        Assert.Equal(0, Index(h));
    }

    [Fact]
    public void A_frame_added_while_parked_starts_its_clock_at_its_arrival_even_if_rendering_is_late()
    {
        var h = ThreeFrames();
        h.Now = 0; h.Send("a=a,i=1,s=2"); h.Replies.Clear();
        h.Advance(10_000);                                   // parked on the last frame
        h.Now = 20_000; h.Send("a=f,i=1,f=32,s=2,v=1,z=100", Solid(0, 0, 255, 255)); h.Replies.Clear();
        Assert.Equal(3, Index(h));                            // shown at arrival, with no render in between
        h.Now = 20_050; h.Send("a=f,i=1,f=32,s=2,v=1,z=100", Solid(255, 0, 255, 255)); h.Replies.Clear();
        Assert.Equal(3, Index(h));                            // the first new frame has had 50 of its 100 ms
        Assert.True(h.Advance(20_150));                        // a late render: its 100 ms ended at 20_100
        Assert.Equal(4, Index(h));
    }

    [Fact]
    public void A_refused_frame_still_leaves_the_display_where_elapsed_time_puts_it()
    {
        var h = new Harness();
        for (int i = 2; i <= KittyAnimation.MaxFrames; i++) h.Send("a=f,i=1,f=32,s=2,v=1,z=100", Solid(1, 1, 1, 255));
        h.Replies.Clear();
        h.Now = 0; h.Send("a=a,i=1,s=3"); h.Replies.Clear();
        h.Now = 1000;                                          // no render since the start
        h.Send("a=f,i=1,f=32,s=2,v=1,z=100", Solid(2, 2, 2, 255));
        Assert.Contains(";ENOSPC:", h.Reply());                // the 65th frame is refused...
        Assert.Equal(10, Index(h));                            // ...but the playhead was settled first: root 40 ms, then nine 100 ms frames
        Assert.Same(h.FramePixels(10), h.Shown);
    }

    private sealed class RejectedHandleDecoder(object handle) : IImageDecoder
    {
        public object? DecodeImageBytes(byte[] imageData, out int pixelWidth, out int pixelHeight) { pixelWidth = pixelHeight = 0; return null; }
        public object? DecodeSixel(string sixelData, out int pixelWidth, out int pixelHeight) { pixelWidth = pixelHeight = 0; return null; }
        public object? DecodeRawImage(byte[] data, int bytesPerPixel, int width, int height, out int pixelWidth, out int pixelHeight)
        {
            (pixelWidth, pixelHeight) = (width, height);
            return handle;
        }
    }

    [Fact]
    public void A_frame_whose_decoder_handle_cannot_be_admitted_is_kept_undecoded_like_a_whole_image()
    {
        var h = new Harness();
        object spent = new();
        h.Buffer.ReleaseRejectedImageHandle(spent);           // a handle the buffer has already handed off: it cannot be admitted again
        h.Parser.ImageDecoder = new RejectedHandleDecoder(spent);
        h.Send("a=f,i=1,f=32,s=2,v=1,z=100", Solid(0, 255, 0, 255));
        Assert.Equal("\x1b_Gi=1;OK\x1b\\", h.Reply());
        var animation = h.Buffer.FindKittyImage(1, 0)!.Animation!;
        Assert.Equal(2, animation.Frames.Count);
        Assert.IsType<UndecodedImage>(animation.Frames[1].Handle);
        Assert.NotSame(spent, animation.Frames[1].Handle);
        Assert.Equal(100, animation.Frames[1].GapMs);
    }

    [Fact]
    public void Making_room_for_a_frame_stores_it_first_and_evicts_only_unplaced_older_assets()
    {
        var h = new Harness(width: 1000, height: 1000);       // 4 MB root
        for (int id = 2; id <= 15; id++) h.Send($"a=t,f=32,s=1000,v=1000,i={id}", Solid(1, 2, 3, 255, 1000 * 1000));
        h.Replies.Clear();
        Assert.NotNull(h.Buffer.FindKittyImage(2, 0));         // 14 further 4 MB images fit beside the root: 60 MB of 64
        h.Send("a=f,i=1,f=32,s=1000,v=1000,z=100", Solid(5, 5, 5, 255, 1000 * 1000));   // +8 MB: must evict at least two
        Assert.Equal("\x1b_Gi=1;OK\x1b\\", h.Reply());
        Assert.Equal(2, TerminalBuffer.KittyFrameCount(h.Buffer.FindKittyImage(1, 0)!));
        Assert.Null(h.Buffer.FindKittyImage(2, 0));            // the oldest unplaced asset went first
        Assert.NotNull(h.Buffer.FindKittyImage(15, 0));        // the newest survived
        Assert.Single(h.Buffer.Images);                        // the animated image's placement is untouched
    }

    [Fact]
    public void Animation_is_refused_and_not_advertised_where_images_are_retained_for_snapshots()
    {
        var h = new Harness();
        h.Parser.RetainImageSources = true;                   // a persistent-session parser: snapshots keep the root frame only
        h.Send("a=f,i=1,f=32,s=2,v=1", Solid(1, 2, 3, 255));
        Assert.Contains(";ENOTSUP:", h.Reply());
        h.Send("a=a,i=1,s=3");
        Assert.Contains(";ENOTSUP:", h.Reply());
        Assert.Equal(1, TerminalBuffer.KittyFrameCount(h.Buffer.FindKittyImage(1, 0)!));
        foreach (string extension in new[] { "f", "a", "c" })
        {
            h.Send($"a=q,i=31,s=1,v=1,f=24,t=d,e={extension}", "AAAA");
            Assert.Equal("\x1b_Gi=31;ERR\x1b\\", h.Reply());
        }
        h.Parser.RetainImageSources = false;
        h.Send("a=q,i=31,s=1,v=1,f=24,t=d,e=a", "AAAA");
        Assert.Equal("\x1b_Gi=31;OK\x1b\\", h.Reply());
    }

    [Fact]
    public void Switching_a_parked_loading_animation_to_running_does_not_count_the_time_it_was_parked()
    {
        var h = ThreeFrames();
        h.Now = 0; h.Send("a=a,i=1,s=2"); h.Replies.Clear();
        h.Advance(10_000);                                   // parked on the last frame since 350 ms
        h.Now = 10_000; h.Send("a=a,i=1,s=3,v=3"); h.Replies.Clear();
        h.Advance(10_000);
        var animation = h.Buffer.FindKittyImage(1, 0)!.Animation!;
        Assert.Equal(KittyAnimationState.Running, animation.State);   // the parked time did not use up the two passes
        Assert.Equal(0, animation.Current);
        Assert.True(animation.LoopsRemaining >= 0);
        h.Advance(10_349);
        Assert.Equal(KittyAnimationState.Running, animation.State);
        Assert.Equal(2, animation.Current);
    }

    [Fact]
    public void A_paused_host_holds_the_playheads_and_requests_no_wake_ups()
    {
        var h = ThreeFrames();
        h.Now = 0; h.Send("a=a,i=1,s=3"); h.Replies.Clear();
        h.Buffer.KittyAnimationPaused = true;                // a replay window that is not playing
        Assert.False(h.Advance(60));
        Assert.Equal(0, Index(h));
        Assert.Null(NextDue(h));
        h.Buffer.KittyAnimationPaused = false;
        Assert.True(h.Advance(60));
        Assert.Equal(1, Index(h));
    }

    [Fact]
    public void The_room_check_reads_the_clock_only_for_an_animating_unpaused_asset()
    {
        var h = new Harness();
        int reads = 0;
        h.Buffer.KittyAnimationClock = () => { reads++; return h.Now; };
        KittyImageAsset asset = h.Buffer.FindKittyImage(1, 0)!;
        h.Buffer.CheckKittyFrameRoom(asset, -1, 8);                // no frame yet, so nothing to settle
        Assert.Equal(0, reads);
        h.Send("a=f,i=1,f=32,s=2,v=1,z=100", Solid(1, 2, 3, 255)); h.Replies.Clear();
        reads = 0;
        h.Buffer.KittyAnimationPaused = true;
        h.Buffer.CheckKittyFrameRoom(asset, -1, 8);                // a held host never follows time
        Assert.Equal(0, reads);
        h.Buffer.KittyAnimationPaused = false;
        h.Buffer.CheckKittyFrameRoom(asset, -1, 8);
        Assert.Equal(1, reads);
    }

    [Fact]
    public void A_paused_host_applies_commands_without_advancing_by_time()
    {
        var h = ThreeFrames();
        h.Now = 0; h.Send("a=a,i=1,s=3"); h.Replies.Clear();
        h.Buffer.KittyAnimationPaused = true;
        h.Now = 60; h.Send("a=a,i=1,r=1,z=200"); h.Replies.Clear();     // would settle to frame 2 first if time were followed
        Assert.Equal(0, Index(h));
        h.Send("a=f,i=1,f=32,s=2,v=1,z=100", Solid(1, 2, 3, 255));        // nor does a new frame move the playhead
        Assert.Equal(0, Index(h));
        h.Send("a=a,i=1,c=3"); h.Replies.Clear();                           // an explicit current frame is not time-based and still applies
        Assert.Equal(2, Index(h));
    }

    [Fact]
    public void Wake_ups_are_requested_only_for_a_placement_in_the_visible_rows()
    {
        var h = ThreeFrames();
        h.Now = 1000; h.Send("a=a,i=1,s=3"); h.Replies.Clear();
        var image = Assert.Single(h.Buffer.Images);
        h.Buffer.AdvanceKittyAnimations(1000, out long? visible, firstVisibleLine: image.CellY);
        Assert.NotNull(visible);                                           // the viewport shows the placement
        h.Buffer.AdvanceKittyAnimations(1000, out long? scrolledAway, firstVisibleLine: image.CellY + image.CellHeight + 100);
        Assert.Null(scrolledAway);                                         // it is below... above the viewport: no redraws for it
        h.Buffer.AdvanceKittyAnimations(1000, out long? anywhere, firstVisibleLine: null);
        Assert.NotNull(anywhere);                                          // no viewport given: any placement counts
    }

    /// <summary>Decodes raw frames and containers to opaque handles but cannot read them back as pixels (the interface default).</summary>
    private sealed class RawOnlyDecoder : IImageDecoder
    {
        public object? DecodeImageBytes(byte[] imageData, out int pixelWidth, out int pixelHeight) { (pixelWidth, pixelHeight) = (1, 1); return new object(); }
        public object? DecodeSixel(string sixelData, out int pixelWidth, out int pixelHeight) { pixelWidth = pixelHeight = 0; return null; }
        public object? DecodeRawImage(byte[] data, int bytesPerPixel, int width, int height, out int pixelWidth, out int pixelHeight)
        {
            (pixelWidth, pixelHeight) = (width, height);
            return new object();
        }
    }

    private sealed class CountingDecoder : IImageDecoder
    {
        internal int Decodes;
        public object? DecodeImageBytes(byte[] imageData, out int pixelWidth, out int pixelHeight) { Decodes++; pixelWidth = pixelHeight = 0; return null; }
        public object? DecodeSixel(string sixelData, out int pixelWidth, out int pixelHeight) { pixelWidth = pixelHeight = 0; return null; }
        public object? DecodeRawImage(byte[] data, int bytesPerPixel, int width, int height, out int pixelWidth, out int pixelHeight)
        {
            Decodes++;
            (pixelWidth, pixelHeight) = (width, height);
            return new object();
        }
    }

    [Fact]
    public void A_frame_the_quotas_will_refuse_is_refused_before_any_bitmap_is_decoded()
    {
        var h = new Harness();
        for (int i = 2; i <= KittyAnimation.MaxFrames; i++) h.Send("a=f,i=1,f=32,s=2,v=1,z=100", Solid(1, 1, 1, 255));
        h.Replies.Clear();
        var counting = new CountingDecoder();
        h.Parser.ImageDecoder = counting;
        for (int attempt = 0; attempt < 3; attempt++) h.Send("a=f,i=1,f=32,s=2,v=1", Solid(2, 2, 2, 255));
        h.Send("a=f,i=1,f=32,s=2,v=1", Solid(2, 2, 2, 255, 1));       // a malformed payload is refused for the quota first: nothing is inflated or composed for it
        Assert.Equal(4, h.Replies.Count(reply => reply.Contains(";ENOSPC:", StringComparison.Ordinal)));
        Assert.Equal(0, counting.Decodes);                      // nothing was decoded, so nothing waits in the retirement queue
        var retired = new List<object>(); h.Buffer.DrainRetiredImageHandles(retired, long.MaxValue);
        Assert.Empty(retired);
    }

    [Fact]
    public void Rendering_never_waits_for_the_write_lock_and_costs_nothing_without_animations()
    {
        var still = new Harness();                            // no frame or control command ever addressed this image
        Assert.False(still.Buffer.HasKittyAnimations);
        var animated = ThreeFrames();
        animated.Now = 0; animated.Send("a=a,i=1,s=3"); animated.Replies.Clear();
        Assert.True(animated.Buffer.HasKittyAnimations);
        using var release = new System.Threading.ManualResetEventSlim();
        var signals = new[] { new System.Threading.ManualResetEventSlim(), new System.Threading.ManualResetEventSlim() };
        var writers = new[] { still.Buffer, animated.Buffer }.Select((buffer, index) => System.Threading.Tasks.Task.Run(() =>
        {
            buffer.Lock.EnterWriteLock();                      // sustained output holding the lock
            try { signals[index].Set(); release.Wait(TestContext.Current.CancellationToken); }
            finally { buffer.Lock.ExitWriteLock(); }
        })).ToArray();
        try
        {
            Assert.True(signals[0].Wait(10_000, TestContext.Current.CancellationToken) && signals[1].Wait(10_000, TestContext.Current.CancellationToken));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Assert.False(still.Buffer.AdvanceKittyAnimations(10_000, out long? stillDue));
            Assert.Null(stillDue);                             // nothing to do, lock never touched
            Assert.False(animated.Buffer.AdvanceKittyAnimations(10_000, out long? busyDue));
            Assert.Equal(10_016, busyDue);                     // the lock was busy: skip this frame and ask for another
            Assert.True(watch.ElapsedMilliseconds < 1000, "advancing must not block behind a writer");
        }
        finally
        {
            release.Set();
            System.Threading.Tasks.Task.WaitAll(writers, 10_000, TestContext.Current.CancellationToken);
            foreach (var signal in signals) signal.Dispose();
        }
        Assert.True(animated.Advance(60));                    // and once the lock is free it advances normally
        Assert.Equal(1, Index(animated));
    }

    [Fact]
    public void The_animation_flag_clears_when_the_last_animated_image_is_gone()
    {
        var h = ThreeFrames();
        Assert.True(h.Buffer.HasKittyAnimations);
        h.Send("a=d,d=I,i=1"); h.Replies.Clear();
        Assert.False(h.Buffer.HasKittyAnimations);
        Assert.False(h.Buffer.AdvanceKittyAnimations(1_000, out long? due)); Assert.Null(due);
    }

    [Fact]
    public void Advancing_is_a_pure_function_of_time_so_extra_offscreen_advances_change_nothing()
    {
        foreach (string controls in new[] { "a=a,i=1,s=3", "a=a,i=1,s=3,v=3", "a=a,i=1,s=2" })
        {
            var direct = ThreeFrames();
            direct.Now = 0; direct.Send(controls); direct.Replies.Clear();
            var stepped = ThreeFrames();
            stepped.Now = 0; stepped.Send(controls); stepped.Replies.Clear();
            foreach (long at in new long[] { 10, 49, 50, 51, 149, 150, 349, 350, 351, 700, 701, 5_000 }) stepped.Advance(at);   // captures and renders in between
            direct.Advance(5_000);
            var a = direct.Buffer.FindKittyImage(1, 0)!.Animation!; var b = stepped.Buffer.FindKittyImage(1, 0)!.Animation!;
            Assert.Equal((a.State, a.Current, a.Displayed, a.LoopsRemaining), (b.State, b.Current, b.Displayed, b.LoopsRemaining));
            Assert.Equal(direct.Buffer.Images.Count, stepped.Buffer.Images.Count);
        }
    }

    [Fact]
    public void Skipping_whole_cycles_leaves_the_same_visible_frame_as_stepping_through_them_even_around_a_gapless_frame()
    {
        Harness Build(string controls)
        {
            var h = new Harness();
            h.Send("a=f,i=1,f=32,s=2,v=1,z=-1", Solid(0, 255, 0, 255));      // a gapless second frame
            h.Send("a=a,i=1,r=1,z=100"); h.Replies.Clear();
            h.Now = 0; h.Send(controls); h.Replies.Clear();                   // positioned on the gapless frame
            return h;
        }
        foreach (string controls in new[] { "a=a,i=1,c=2,s=3,v=3", "a=a,i=1,c=2,s=3,v=2" })      // two passes, then one pass: nothing left to skip
        {
            var one = Build(controls); one.Advance(100);
            var two = Build(controls); two.Advance(0); two.Advance(100);
            var x = one.Buffer.FindKittyImage(1, 0)!.Animation!; var y = two.Buffer.FindKittyImage(1, 0)!.Animation!;
            Assert.Equal((x.State, x.Current, x.Displayed, x.LoopsRemaining), (y.State, y.Current, y.Displayed, y.LoopsRemaining));
        }
        var direct = Build("a=a,i=1,c=2,s=3,v=3"); direct.Advance(100);
        var stepped = Build("a=a,i=1,c=2,s=3,v=3"); stepped.Advance(0); stepped.Advance(100);
        var a = direct.Buffer.FindKittyImage(1, 0)!.Animation!; var b = stepped.Buffer.FindKittyImage(1, 0)!.Animation!;
        Assert.Equal((a.State, a.Current, a.Displayed, a.LoopsRemaining), (b.State, b.Current, b.Displayed, b.LoopsRemaining));
        Assert.Equal(0, a.Displayed);                                          // the gapless frame is never the one on screen
        Assert.Same(direct.FramePixels(0), direct.Shown);
    }

    [Fact]
    public void Shortening_the_active_frames_gap_below_its_elapsed_time_ends_it_now_without_backdating_its_successors()
    {
        var h = new Harness();
        h.Send("a=f,i=1,f=32,s=2,v=1,z=100", Solid(0, 255, 0, 255));
        h.Send("a=f,i=1,f=32,s=2,v=1,z=100", Solid(0, 0, 255, 255));
        h.Send("a=a,i=1,r=1,z=1000"); h.Replies.Clear();            // gaps 1000 / 100 / 100
        h.Now = 0; h.Send("a=a,i=1,s=3,v=2"); h.Replies.Clear();    // one pass
        h.Now = 900; h.Send("a=a,i=1,r=1,z=10"); h.Replies.Clear(); // the root has been up 900 ms; its gap shrinks to 10, with no render in between
        var animation = h.Buffer.FindKittyImage(1, 0)!.Animation!;
        Assert.Equal(KittyAnimationState.Running, animation.State);
        Assert.Equal(0, animation.Current);                          // the change applies from now: the root ends at 900, not retroactively
        h.Advance(900);
        Assert.Equal(1, Index(h));                                    // frame 2 starts now and still has its full 100 ms
        Assert.Equal(KittyAnimationState.Running, animation.State);
        h.Advance(999);
        Assert.Equal(1, Index(h));
        h.Advance(1000);
        Assert.Equal(2, Index(h));
    }

    [Fact]
    public void A_lone_root_frame_set_running_never_requests_wake_ups_or_changes()
    {
        var h = new Harness();                                  // the root only: transmitted and placed, never given another frame
        object root = h.Shown;
        h.Now = 0; h.Send("a=a,i=1,s=3"); h.Replies.Clear();
        Assert.Equal(KittyAnimationState.Running, h.Buffer.FindKittyImage(1, 0)!.Animation!.State);
        Assert.False(h.Advance(5_000));
        Assert.Same(root, h.Shown);
        Assert.Null(NextDue(h));                                // nothing on screen can change, so nothing is redrawn about 25 times a second
    }

    [Fact]
    public void A_placed_animation_that_cannot_move_requests_no_wake_ups()
    {
        var h = new Harness();
        h.Send("a=f,i=1,f=32,s=2,v=1,z=-1", Solid(0, 255, 0, 255));
        h.Send("a=a,i=1,r=1,z=-1"); h.Replies.Clear();
        h.Now = 0; h.Send("a=a,i=1,s=3"); h.Replies.Clear();
        h.Advance(1_000);
        Assert.Null(NextDue(h));
        Assert.Single(h.Buffer.Images);                       // the image is placed, so only the gapless policy suppresses the timer
    }

    [Fact]
    public void An_animation_whose_frames_are_all_gapless_cannot_hang_or_change_the_display()
    {
        var h = new Harness();
        h.Send("a=f,i=1,f=32,s=2,v=1,z=-1", Solid(0, 255, 0, 255));
        h.Send("a=a,i=1,r=1,z=-1"); h.Replies.Clear();
        object root = h.Shown;
        h.Now = 0; h.Send("a=a,i=1,s=3"); h.Replies.Clear();
        Assert.False(h.Advance(5_000));
        Assert.Same(root, h.Shown);
    }

    [Fact]
    public void Loading_mode_waits_at_the_last_frame_and_resumes_when_more_arrives()
    {
        var h = ThreeFrames();
        h.Now = 0; h.Send("a=a,i=1,s=2"); h.Replies.Clear();
        Assert.True(h.Advance(10_000));                    // ran to the last frame
        Assert.Equal(2, Index(h));
        Assert.False(h.Advance(20_000));                   // and waits, never looping
        Assert.Equal(2, Index(h));
        Assert.Null(NextDue(h));
        h.Send("a=f,i=1,f=32,s=2,v=1,z=100", Solid(0, 0, 255, 255));
        Assert.Equal(3, Index(h));                         // resumed by the arrival itself
        Assert.False(h.Advance(20_001));
    }

    private static long? NextDue(Harness h) { h.Buffer.AdvanceKittyAnimations(h.Now, out long? due); return due; }

    [Fact]
    public void Stopping_freezes_the_current_frame_and_resets_the_loop_counter()
    {
        var h = ThreeFrames();
        h.Now = 0; h.Send("a=a,i=1,s=3,v=5"); h.Replies.Clear();
        h.Advance(60);
        Assert.Equal(1, Index(h));
        h.Send("a=a,i=1,s=1"); h.Replies.Clear();
        var animation = h.Buffer.FindKittyImage(1, 0)!.Animation!;
        Assert.Equal(-1, animation.LoopsRemaining);
        Assert.False(h.Advance(50_000));
        Assert.Equal(1, Index(h));
        Assert.Same(h.FramePixels(1), h.Shown);
    }

    [Fact]
    public void A_gapless_frame_is_never_displayed()
    {
        var h = new Harness();
        h.Send("a=f,i=1,f=32,s=2,v=1,z=-1", Solid(0, 255, 0, 255));
        h.Send("a=f,i=1,f=32,s=2,v=1,z=100", Solid(255, 255, 0, 255));
        h.Send("a=a,i=1,r=1,z=50"); h.Replies.Clear();
        h.Now = 0; h.Send("a=a,i=1,s=3"); h.Replies.Clear();
        Assert.True(h.Advance(50));
        Assert.Equal(2, Index(h));                         // skipped frame 2
    }

    [Fact]
    public void Setting_the_current_frame_shows_it_immediately_and_restarts_its_clock()
    {
        var h = ThreeFrames();
        h.Now = 500; h.Send("a=a,i=1,c=3");
        Assert.Equal("\x1b_Gi=1;OK\x1b\\", h.Reply());
        Assert.Same(h.FramePixels(2), h.Shown);
        h.Send("a=a,i=1,c=2"); h.Replies.Clear();
        Assert.Same(h.FramePixels(1), h.Shown);
    }

    [Fact]
    public void Every_placement_of_an_animated_image_follows_the_frame()
    {
        var h = ThreeFrames();
        h.Parser.Process("\x1b[5;1H");
        h.Send("a=p,i=1,C=1"); h.Replies.Clear();
        Assert.Equal(2, h.Buffer.Images.Count);
        h.Now = 0; h.Send("a=a,i=1,s=3"); h.Replies.Clear();
        h.Advance(60);
        Assert.All(h.Buffer.Images, image => Assert.Same(h.FramePixels(1), image.ImageHandle));
    }

    [Fact]
    public void A_placement_made_during_playback_shows_the_current_frame()
    {
        var h = ThreeFrames();
        h.Now = 0; h.Send("a=a,i=1,s=3"); h.Replies.Clear();
        h.Advance(60);
        h.Send("a=p,i=1,C=1,p=7"); h.Replies.Clear();
        Assert.Same(h.FramePixels(1), h.Buffer.Images[^1].ImageHandle);
    }

    [Fact]
    public void Wake_ups_are_requested_only_while_a_visible_placement_is_animating()
    {
        var h = ThreeFrames();
        Assert.Null(NextDue(h));                           // stopped
        h.Now = 1000; h.Send("a=a,i=1,s=3"); h.Replies.Clear();
        Assert.Equal(1050, NextDue(h));
        h.Send("a=d,d=i,i=1"); h.Replies.Clear();         // soft delete: no placement, data kept
        Assert.Null(NextDue(h));
    }

    [Fact]
    public void Replacing_the_image_drops_its_frames_and_state()
    {
        var h = ThreeFrames();
        var frames = Enumerable.Range(0, 3).Select(i => h.Buffer.FindKittyImage(1, 0)!.Animation!.Frames[i].Handle).ToArray();
        h.Send("a=t,f=32,s=2,v=1,i=1", Solid(9, 9, 9, 255));
        Assert.Null(h.Buffer.FindKittyImage(1, 0)!.Animation);
        var retired = new List<object>(); h.Buffer.DrainRetiredImageHandles(retired, long.MaxValue);
        Assert.All(frames, frame => Assert.Contains(frame, retired));
    }

    [Fact]
    public void Hard_delete_retires_every_frame_exactly_once_even_when_a_later_frame_is_displayed()
    {
        var h = ThreeFrames();
        h.Send("a=a,i=1,c=3"); h.Replies.Clear();
        var frames = Enumerable.Range(0, 3).Select(i => h.Buffer.FindKittyImage(1, 0)!.Animation!.Frames[i].Handle).ToArray();
        h.Send("a=d,d=I,i=1");
        Assert.Null(h.Buffer.FindKittyImage(1, 0));
        Assert.Empty(h.Buffer.Images);
        var retired = new List<object>(); h.Buffer.DrainRetiredImageHandles(retired, long.MaxValue);
        Assert.Equal(frames.Length, retired.Count(frame => frames.Contains(frame)));
        Assert.Equal(frames.Length, retired.Distinct().Count(frame => frames.Contains(frame)));
    }

    [Fact]
    public void Soft_delete_keeps_frames_live_and_redisplay_shows_the_current_frame()
    {
        var h = ThreeFrames();
        h.Send("a=a,i=1,c=2"); h.Replies.Clear();
        h.Send("a=d,d=i,i=1");
        Assert.Empty(h.Buffer.Images);
        var retired = new List<object>(); h.Buffer.DrainRetiredImageHandles(retired, long.MaxValue);
        Assert.Empty(retired);
        h.Send("a=p,i=1,C=1");
        Assert.Same(h.FramePixels(1), Assert.Single(h.Buffer.Images).ImageHandle);
    }

    [Fact]
    public void Screen_reset_releases_all_frames()
    {
        var h = ThreeFrames();
        var frames = Enumerable.Range(0, 3).Select(i => h.Buffer.FindKittyImage(1, 0)!.Animation!.Frames[i].Handle).ToArray();
        h.Buffer.Reset();
        Assert.Null(h.Buffer.FindKittyImage(1, 0));
        var retired = new List<object>(); h.Buffer.DrainRetiredImageHandles(retired, long.MaxValue);
        Assert.All(frames, frame => Assert.Contains(frame, retired));
    }

    [Fact]
    public void Composition_is_advertised_with_animation_in_an_ordinary_tab()
    {
        var h = new Harness();
        foreach (string extension in new[] { "f", "a", "c" })
        {
            h.Send($"a=q,i=31,s=1,v=1,f=24,t=d,e={extension}", "AAAA");
            Assert.Equal("\x1b_Gi=31;OK\x1b\\", h.Reply());
        }
    }

    [Fact]
    public void Composition_blends_a_source_frame_onto_the_root_in_place_and_overwrite_replaces()
    {
        var h = new Harness();                                                // root: opaque red | opaque blue
        h.Send("a=f,i=1,f=32,s=2,v=1,z=77", Solid(255, 255, 255, 128));       // frame 2: half-transparent white
        h.Replies.Clear();
        h.Send("a=c,i=1,r=2,c=1");                                            // blend it over the whole root
        Assert.Equal("\x1b_Gi=1;OK\x1b\\", h.Reply());
        var animation = h.Buffer.FindKittyImage(1, 0)!.Animation!;
        Assert.Equal(2, animation.Frames.Count);                              // composition replaces the destination, it adds no frame
        byte[] root = h.FramePixels(0).Rgba;                                  // half-white over opaque red | opaque blue
        Assert.Equal(255, root[0]); Assert.InRange(root[1], 126, 129); Assert.InRange(root[2], 126, 129); Assert.Equal(255, root[3]);
        Assert.InRange(root[4], 126, 129); Assert.InRange(root[5], 126, 129); Assert.Equal(255, root[6]); Assert.Equal(255, root[7]);
        Assert.Equal(new byte[] { 255, 255, 255, 128, 255, 255, 255, 128 }, h.FramePixels(1).Rgba);   // the source frame is untouched
        h.Send("a=c,i=1,r=2,c=1,C=1");                                        // replace: the pixels, alpha included, are the source's
        Assert.Equal("\x1b_Gi=1;OK\x1b\\", h.Reply());
        Assert.Equal(new byte[] { 255, 255, 255, 128, 255, 255, 255, 128 }, h.FramePixels(0).Rgba);
    }

    [Fact]
    public void Composition_lands_a_rectangle_at_its_offsets_and_keeps_the_destination_gap()
    {
        var h = new Harness();
        h.Send("a=f,i=1,f=32,s=2,v=1,z=77", Solid(0, 255, 0, 255));           // frame 2: green | green, 77 ms
        h.Replies.Clear();
        object old = h.FramePixels(1);
        h.Send("a=c,i=1,r=1,c=2,w=1,h=1,X=0,Y=0,x=1,y=0");                    // the root's red pixel onto frame 2's right pixel
        Assert.Equal("\x1b_Gi=1;OK\x1b\\", h.Reply());
        Assert.Equal(new byte[] { 0, 255, 0, 255, 255, 0, 0, 255 }, h.FramePixels(1).Rgba);
        Assert.Equal(77, h.Buffer.FindKittyImage(1, 0)!.Animation!.Frames[1].GapMs);
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 0, 255, 255 }, h.FramePixels(0).Rgba);              // the source frame is untouched
        var retired = new List<object>(); h.Buffer.DrainRetiredImageHandles(retired, long.MaxValue);
        Assert.Contains(old, retired);                                        // the replaced destination's pixels are released
    }

    [Fact]
    public void A_frame_composes_onto_itself_only_when_the_rectangles_do_not_overlap()
    {
        var h = new Harness(4, 1);                                            // root: red | blue | red | blue
        h.Send("a=c,i=1,r=1,c=1,w=1,X=1,x=2");                                // the blue pixel onto the third
        Assert.Equal("\x1b_Gi=1;OK\x1b\\", h.Reply());
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255 }, h.FramePixels(0).Rgba);
        h.Send("a=c,i=1,r=1,c=1,w=2,X=0,x=1");                                // [0,2) onto [1,3) overlaps
        Assert.Contains(";EINVAL:", h.Reply());
        h.Send("a=c,i=1,r=1,c=1,w=2,X=0,x=2");                                // [0,2) onto [2,4) touches but does not overlap
        Assert.Equal("\x1b_Gi=1;OK\x1b\\", h.Reply());
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 0, 255, 255, 255, 0, 0, 255, 0, 0, 255, 255 }, h.FramePixels(0).Rgba);
    }

    [Theory]
    [InlineData("a=c,i=2,r=1,c=2", "ENOENT")]                                 // unknown image
    [InlineData("a=c,i=1,c=2", "ENOENT")]                                     // no source frame named
    [InlineData("a=c,i=1,r=1", "ENOENT")]                                     // no destination frame named
    [InlineData("a=c,i=1,r=0,c=2", "ENOENT")]
    [InlineData("a=c,i=1,r=3,c=2", "ENOENT")]
    [InlineData("a=c,i=1,r=1,c=9", "ENOENT")]
    [InlineData("a=c,i=1,r=5,c=2,w=9", "ENOENT")]                             // a missing frame is reported before a bad rectangle
    [InlineData("a=c,i=1,r=1,c=2,w=3", "EINVAL")]                             // wider than the image
    [InlineData("a=c,i=1,r=1,c=2,h=2", "EINVAL")]
    [InlineData("a=c,i=1,r=1,c=2,w=1,X=2", "EINVAL")]                         // source rectangle leaves the image
    [InlineData("a=c,i=1,r=1,c=2,w=2,x=1", "EINVAL")]                         // destination rectangle leaves the image
    [InlineData("a=c,i=1,r=1,c=2,w=1,h=1,y=1", "EINVAL")]
    [InlineData("a=c,i=1,r=1,c=2,C=2", "EINVAL")]
    [InlineData("a=c,i=1,r=1,c=2,w=abc", "EINVAL")]
    [InlineData("a=c,i=1,r=2,c=2,w=2", "EINVAL")]                             // a frame onto itself, the whole rectangle overlaps
    [InlineData("a=c,i=1,r=2,c=2,w=1,X=0,x=0", "EINVAL")]
    public void Invalid_compositions_get_a_correlated_negative_reply_and_change_nothing(string controls, string code)
    {
        var h = new Harness();
        h.Send("a=f,i=1,f=32,s=2,v=1,z=77", Solid(0, 255, 0, 255));
        h.Replies.Clear();
        byte[] root = h.FramePixels(0).Rgba, second = h.FramePixels(1).Rgba;
        h.Send(controls);
        string reply = h.Reply();
        Assert.Contains(";" + code + ":", reply);
        Assert.StartsWith("\x1b_Gi=" + (controls.Contains("i=2") ? "2" : "1") + ";", reply, StringComparison.Ordinal);
        Assert.Equal(2, h.Buffer.FindKittyImage(1, 0)!.Animation!.Frames.Count);
        Assert.Equal(root, h.FramePixels(0).Rgba); Assert.Equal(second, h.FramePixels(1).Rgba);
    }

    [Fact]
    public void Composition_needs_readable_pixels_and_says_so_when_there_are_none()
    {
        var h = new Harness();
        h.Send("a=f,i=1,f=32,s=2,v=1", Solid(0, 255, 0, 255));
        h.Replies.Clear();
        h.Parser.ImageDecoder = null;                                          // the root's pixels can only be read back through a decoder
        h.Send("a=c,i=1,r=1,c=2");
        Assert.Contains(";ENOTSUP:", h.Reply());
        h.Send("a=c,i=1,r=2,c=2,w=1,X=0,x=1");                                  // frame 2 keeps its own pixels: composing within it needs no decoder
        Assert.Equal("\x1b_Gi=1;OK\x1b\\", h.Reply());
    }

    [Fact]
    public void Number_addressing_selects_the_newest_image_for_frames()
    {
        var h = new Harness();
        h.Send("a=t,f=32,s=2,v=1,I=5", Solid(1, 1, 1, 255));
        Assert.Contains("I=5;OK", h.Reply());
        h.Send("a=f,I=5,f=32,s=2,v=1", Solid(2, 2, 2, 255));
        Assert.Equal("\x1b_Gi=2,I=5;OK\x1b\\", h.Reply());
        Assert.Equal(2, TerminalBuffer.KittyFrameCount(h.Buffer.FindKittyImage(0, 5)!));
        Assert.Equal(1, TerminalBuffer.KittyFrameCount(h.Buffer.FindKittyImage(1, 0)!));
    }
}
