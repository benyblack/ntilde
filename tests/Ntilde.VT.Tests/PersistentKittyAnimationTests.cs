using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Ntilde.VT.Tests;

/// <summary>
/// Kitty animation where images are retained for snapshots (persistent image policy 3, #1017): the headless daemon has no image decoder while its clients do,
/// so both must accept, refuse and charge the same frames from the same bytes, and a snapshot must carry the frames and the playhead.
/// </summary>
public sealed class PersistentKittyAnimationTests
{
    private sealed record Pixels(byte[] Rgba, int Width, int Height);

    /// <summary>Hands back inspectable handles and counts every pixel read-back: a persistent session must never need one.</summary>
    private sealed class ClientDecoder : IImageDecoder
    {
        internal int ReadBacks;
        public object? DecodeImageBytes(byte[] imageData, out int pixelWidth, out int pixelHeight)
        {
            pixelWidth = pixelHeight = 0;
            if (!PngPixelReader.TryRead(imageData, 4096, out pixelWidth, out pixelHeight, out byte[] rgba)) return null;
            return new Pixels(rgba, pixelWidth, pixelHeight);
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

        public bool TryReadPixels(object handle, out byte[] rgba, out int pixelWidth, out int pixelHeight)
        {
            ReadBacks++;
            (rgba, pixelWidth, pixelHeight) = ([], 0, 0);
            return false;
        }
    }

    private sealed class Side
    {
        internal readonly TerminalBuffer Buffer;
        internal readonly AnsiParser Parser;
        internal readonly List<string> Replies = [];
        internal long Now { get; set; }
        internal Side(IImageDecoder? decoder, int policy = PersistentImagePolicy.AnimationImagesVersion, ImageSourceBudget? budget = null)
        {
            Buffer = new TerminalBuffer(40, 20) { ImageSources = budget ?? new ImageSourceBudget() };
            Parser = new AnsiParser(Buffer, forceConPtyFiltering: false) { ImageDecoder = decoder, CellWidth = 8, CellHeight = 16 };
            PersistentImagePolicy.Apply(Parser, policy);
            Parser.OnResponse = Replies.Add;
            Buffer.KittyAnimationClock = () => Now;
        }

        internal void Send(string text) => Parser.Process(text);
        internal KittyImageAsset Asset(uint id = 1) => Buffer.FindKittyImage(id, 0)!;
        internal KittyAnimation Animation(uint id = 1) => Asset(id).Animation!;
    }

    private static string Kitty(string controls, string payload = "") => $"\x1b_G{controls};{payload}\x1b\\";
    private static string B64(params byte[] bytes) => Convert.ToBase64String(bytes);
    private static string Solid(byte r, byte g, byte b, byte a, int pixels) => B64(Enumerable.Range(0, pixels).SelectMany(_ => new[] { r, g, b, a }).ToArray());
    private static string Png(int width, int height, int[][] pixels) => Convert.ToBase64String(PngPixelReaderTests.Encode(width, height, 6, 8, false, pixels));

    /// <summary>A root, a raw frame, a PNG frame composed over the root, an edit of the root, a frame built on that edit, then playback.</summary>
    private static string Traffic() =>
        Kitty("a=T,f=32,s=2,v=1,i=1,p=1,c=2,r=1,C=1", B64(255, 0, 0, 255, 0, 0, 255, 255))                      // root: red | blue
        + Kitty("a=f,f=32,s=2,v=1,i=1,z=100", Solid(0, 255, 0, 255, 2))                                           // frame 2: green
        + Kitty("a=f,f=100,i=1,x=1,c=1,z=120", Png(1, 1, [[255, 255, 255, 128]]))                                 // frame 3: half-white PNG over the root's right pixel
        + Kitty("a=f,f=32,s=2,v=1,i=1,r=1", Solid(9, 8, 7, 255, 2))                                               // the root is edited
        + Kitty("a=f,f=32,s=1,v=1,i=1,x=1,c=1,z=130", Solid(1, 2, 3, 255, 1))                                     // frame 4: built on the edited root
        + Kitty("a=a,i=1,s=3,v=1") + Kitty("a=a,i=1,c=2");

    [Fact]
    public void A_daemon_without_a_decoder_and_a_client_with_one_decide_alike_and_never_read_a_decoder_back()
    {
        var daemon = new Side(decoder: null);
        var decoder = new ClientDecoder();
        var client = new Side(decoder);
        daemon.Send(Traffic());
        client.Send(Traffic());

        Assert.All(daemon.Replies, reply => Assert.EndsWith(";OK\x1b\\", reply, StringComparison.Ordinal));
        Assert.Equal(daemon.Replies, client.Replies);
        Assert.Equal(7, daemon.Replies.Count);
        Assert.Equal(0, decoder.ReadBacks);                                                                       // pixels came from retained sources, not from a decoder
        Assert.Equal(daemon.Asset().PixelCharge, client.Asset().PixelCharge);
        Assert.Equal(4, daemon.Animation().Frames.Count);                                                          // the root edit replaces frame 1, it adds none
        Assert.Equal(daemon.Animation().Frames.Select(f => (f.GapMs, f.Charge)), client.Animation().Frames.Select(f => (f.GapMs, f.Charge)));
        Assert.Equal(Json(daemon), Json(client));                                                                  // the same frames, packed bytes and playhead
        // The client decoded what it displays; the daemon did not.
        Assert.IsType<Pixels>(client.Asset().Handle);
        Assert.IsType<UndecodedImage>(daemon.Asset().Handle);
    }

    [Fact]
    public void Frames_are_composed_from_retained_sources_with_the_pixels_the_protocol_gives()
    {
        var side = new Side(decoder: null);
        side.Send(Traffic());
        byte[] Frame(int index) => KittyFrameCodec.TryDecode(side.Animation().Frames[index].Source!.Data!, 2, out byte[] rgba) ? rgba : throw new Xunit.Sdk.XunitException("unpackable");
        Assert.Equal(new byte[] { 0, 255, 0, 255, 0, 255, 0, 255 }, Frame(1));
        // Half-transparent white over the opaque blue right pixel; the left pixel keeps the root's red.
        byte[] over = Frame(2);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, over[..4]);
        Assert.InRange(over[4], 127, 129); Assert.InRange(over[5], 127, 129); Assert.Equal(255, over[6]); Assert.Equal(255, over[7]);
        // The edited root, and a frame built on that edit rather than on the original root.
        Assert.Equal(new byte[] { 9, 8, 7, 255, 9, 8, 7, 255 }, Frame(0));
        Assert.Equal(new byte[] { 9, 8, 7, 255, 1, 2, 3, 255 }, Frame(3));
        Assert.Null(side.Animation().Frames[2].Rgba);                                                              // no second copy kept in memory
    }

    [Fact]
    public void A_container_frame_that_is_not_a_PNG_is_unsupported_and_changes_nothing()
    {
        var side = new Side(decoder: null);
        side.Send(Kitty("a=T,f=32,s=2,v=1,i=1,p=1", B64(1, 2, 3, 255, 4, 5, 6, 255)));
        side.Replies.Clear();
        side.Send(Kitty("a=f,f=100,i=1", B64([.. "GIF89a"u8, 1, 0, 1, 0])));
        Assert.Contains(";ENOTSUP:", Assert.Single(side.Replies));
        side.Send(Kitty("a=f,f=100,i=1", B64(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13)));       // a truncated PNG
        Assert.Contains(";EINVAL:", side.Replies[^1]);
        Assert.Null(side.Asset().Animation);
    }

    [Fact]
    public void A_session_below_policy_three_still_refuses_animation_and_does_not_advertise_it()
    {
        var side = new Side(decoder: null, policy: PersistentImagePolicy.MultipartImagesVersion);
        side.Send(Kitty("a=T,f=32,s=1,v=1,i=1,p=1", B64(1, 2, 3, 255)));
        side.Replies.Clear();
        side.Send(Kitty("a=f,f=32,s=1,v=1,i=1", B64(1, 2, 3, 255)));
        Assert.Contains(";ENOTSUP:", side.Replies[0]);
        side.Send(Kitty("a=a,i=1,s=3"));
        Assert.Contains(";ENOTSUP:", side.Replies[1]);
        side.Send(Kitty("a=q,i=31,e=f,s=1,v=1,t=d,f=24", "AAAA"));
        Assert.EndsWith(";ERR\x1b\\", side.Replies[2], StringComparison.Ordinal);
        Assert.False(PersistentImagePolicy.AllowsAnimation(PersistentImagePolicy.MultipartImagesVersion));
        Assert.True(PersistentImagePolicy.AllowsAnimation(PersistentImagePolicy.AnimationImagesVersion));
        Assert.Equal(PersistentImagePolicy.CompositionImagesVersion, PersistentImagePolicy.EnabledVersion);
        var current = new Side(decoder: null);
        current.Send(Kitty("a=q,i=31,e=f,s=1,v=1,t=d,f=24", "AAAA"));
        Assert.EndsWith(";OK\x1b\\", current.Replies[0], StringComparison.Ordinal);
    }

    /// <summary>The persistent policy that carries <c>a=c</c> (#1027); the traffic below needs it, the older tests keep policy three on purpose.</summary>
    private const int Composing = PersistentImagePolicy.CompositionImagesVersion;

    /// <summary>The animation traffic plus compositions: a PNG frame's pixels onto the edited root, the root onto a later frame, a frame onto itself, then a frame built on the result.</summary>
    private static string ComposeTraffic() =>
        Traffic()
        + Kitty("a=c,i=1,r=3,c=1,w=1,X=1,x=0")                                    // frame 3 (half-white PNG over the root's right pixel) onto the edited root's left pixel
        + Kitty("a=c,i=1,r=1,c=2,w=1,X=0,x=1,C=1")                                // the composed root's left pixel replaces frame 2's right pixel
        + Kitty("a=c,i=1,r=4,c=4,w=1,X=0,x=1")                                    // a frame onto itself, the rectangles side by side
        + Kitty("a=f,f=32,s=1,v=1,i=1,x=0,c=2,z=60", Solid(3, 3, 3, 255, 1));     // built on the composed frame 2

    [Fact]
    public void A_policy_three_session_still_refuses_composition_and_a_policy_four_one_composes_and_advertises_it()
    {
        var three = new Side(decoder: null, policy: PersistentImagePolicy.AnimationImagesVersion);
        three.Send(Traffic());
        three.Replies.Clear();
        three.Send(Kitty("a=c,i=1,r=2,c=1,w=1"));
        Assert.Contains(";ENOTSUP:", Assert.Single(three.Replies));
        three.Send(Kitty("a=q,i=31,e=c,s=1,v=1,t=d,f=24", "AAAA"));
        Assert.EndsWith(";ERR\x1b\\", three.Replies[^1], StringComparison.Ordinal);                                  // animation is advertised, composition is not
        three.Send(Kitty("a=q,i=31,e=f,s=1,v=1,t=d,f=24", "AAAA"));
        Assert.EndsWith(";OK\x1b\\", three.Replies[^1], StringComparison.Ordinal);
        Assert.False(PersistentImagePolicy.AllowsComposition(PersistentImagePolicy.AnimationImagesVersion));
        Assert.True(PersistentImagePolicy.AllowsComposition(Composing));
        var four = new Side(decoder: null, policy: Composing);
        four.Send(Kitty("a=q,i=31,e=c,s=1,v=1,t=d,f=24", "AAAA"));
        Assert.EndsWith(";OK\x1b\\", four.Replies[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_daemon_without_a_decoder_and_a_client_with_one_compose_alike_from_retained_sources()
    {
        var daemon = new Side(decoder: null, policy: Composing);
        var decoder = new ClientDecoder();
        var client = new Side(decoder, policy: Composing);
        daemon.Send(ComposeTraffic());
        client.Send(ComposeTraffic());

        Assert.All(daemon.Replies, reply => Assert.EndsWith(";OK\x1b\\", reply, StringComparison.Ordinal));
        Assert.Equal(daemon.Replies, client.Replies);
        Assert.Equal(11, daemon.Replies.Count);
        Assert.Equal(0, decoder.ReadBacks);
        Assert.Equal(5, daemon.Animation().Frames.Count);                                                          // compositions replace frames, only the last build added one
        Assert.Equal(daemon.Animation().Frames.Select(f => (f.GapMs, f.Charge)), client.Animation().Frames.Select(f => (f.GapMs, f.Charge)));
        Assert.Equal(daemon.Asset().PixelCharge, client.Asset().PixelCharge);
        Assert.Equal(Json(daemon), Json(client));
        byte[] Frame(Side side, int index) => KittyFrameCodec.TryDecode(side.Animation().Frames[index].Source!.Data!, 2, out byte[] rgba) ? rgba : throw new Xunit.Sdk.XunitException("unpackable");
        // Frame 3's right pixel (half-white over the original blue root, ~(128,128,255), now opaque) replaces the edited root's left pixel, which that pixel then carries onto frame 2's right pixel.
        byte[] root = Frame(daemon, 0), second = Frame(daemon, 1);
        Assert.InRange(root[0], 127, 129); Assert.InRange(root[1], 127, 129); Assert.Equal(255, root[2]); Assert.Equal(255, root[3]);
        Assert.Equal(new byte[] { 9, 8, 7, 255 }, root[4..8]);                                                       // the root's right pixel keeps its edit
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, second[..4]);
        Assert.Equal(root[..4], second[4..8]);
        Assert.Equal(Frame(client, 1), second);
    }

    [Fact]
    public void An_evicted_source_makes_a_composition_unavailable_alike_on_every_side()
    {
        var daemon = new Side(decoder: null, policy: Composing, budget: new ImageSourceBudget(maxSources: 3));
        var client = new Side(new ClientDecoder(), policy: Composing, budget: new ImageSourceBudget(maxSources: 3));
        daemon.Send(Traffic());
        client.Send(Traffic());
        TerminalKittyAnimationState animation = Assert.Single(daemon.Buffer.ExportState(int.MaxValue).KittyImages!).Animation!;
        Assert.Contains(animation.Frames, frame => frame.Data.Length == 0);
        daemon.Replies.Clear(); client.Replies.Clear();
        string composes = string.Concat(Enumerable.Range(1, animation.Frames.Count).Select(n => Kitty($"a=c,i=1,r={n},c={n % animation.Frames.Count + 1},w=1")));
        daemon.Send(composes);
        client.Send(composes);
        Assert.Equal(daemon.Replies, client.Replies);
        Assert.Contains(daemon.Replies, reply => reply.Contains(";ENOTSUP:", StringComparison.Ordinal));               // an evicted source or destination is unavailable on both
        Assert.Equal(Json(daemon), Json(client));
    }

    [Fact]
    public void A_snapshot_carries_composed_frames_and_a_restore_goes_on_composing_like_the_original()
    {
        var source = new Side(decoder: null, policy: Composing);
        source.Send(ComposeTraffic());
        Side restored = Restore(Export(source), new ClientDecoder(), now: 0);
        PersistentImagePolicy.Apply(restored.Parser, Composing);
        Assert.Equal(Json(source), Json(restored));
        source.Replies.Clear(); restored.Replies.Clear();
        string more = Kitty("a=c,i=1,r=5,c=3,w=1,X=0,x=1,C=1") + Kitty("a=c,i=1,r=2,c=1,x=1,w=1");
        source.Send(more);
        restored.Send(more);
        Assert.Equal(source.Replies, restored.Replies);
        Assert.All(restored.Replies, reply => Assert.EndsWith(";OK\x1b\\", reply, StringComparison.Ordinal));
        Assert.Equal(Json(source), Json(restored));
    }

    private static TerminalStateSnapshot Export(Side side)
    {
        TerminalStateSnapshot snapshot = side.Buffer.ExportState(int.MaxValue);
        snapshot.Parser = side.Parser.ExportState();
        return TerminalStateSerializer.FromBytes(TerminalStateSerializer.ToBytes(snapshot));
    }

    private static Side Restore(TerminalStateSnapshot snapshot, IImageDecoder? decoder, long now, ImageSourceBudget? budget = null)
    {
        var restored = new Side(decoder, budget: budget) { Now = now };
        TerminalStateTransfer.Restore(restored.Buffer, restored.Parser, TerminalStateSerializer.FromBytes(TerminalStateSerializer.ToBytes(snapshot)));
        return restored;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_snapshot_carries_the_frames_and_the_playhead_and_a_restore_resumes_them(bool withDecoder)
    {
        var source = new Side(new ClientDecoder());
        source.Send(Traffic());
        source.Now = 250;                                                                                              // the current frame has been up 250 ms
        TerminalStateSnapshot snapshot = Export(source);
        TerminalKittyAnimationState animation = Assert.Single(snapshot.KittyImages!).Animation!;
        Assert.Equal((int)KittyAnimationState.Running, animation.State);
        Assert.Equal(4, animation.Frames.Count);
        Assert.Equal(250, animation.ElapsedMs);
        Assert.True(animation.Frames[0].Edited);
        Assert.All(animation.Frames, frame => Assert.NotEmpty(frame.Data));

        Side restored = Restore(snapshot, withDecoder ? new ClientDecoder() : null, now: 1000);
        Assert.Equal(source.Asset().PixelCharge, restored.Asset().PixelCharge);
        Assert.Equal(source.Animation().Frames.Select(f => (f.GapMs, f.Charge)), restored.Animation().Frames.Select(f => (f.GapMs, f.Charge)));
        Assert.Equal((source.Animation().State, source.Animation().Current, source.Animation().Displayed), (restored.Animation().State, restored.Animation().Current, restored.Animation().Displayed));
        Assert.Equal(Json(source), Json(restored));                                                                    // nothing lost, and the clock resumed at 250 ms in
        Assert.True(restored.Buffer.HasKittyAnimations);
        TerminalImage placed = Assert.Single(restored.Buffer.Images);
        Assert.Same(restored.Animation().Frames[restored.Animation().Displayed].Handle, placed.ImageHandle);
        foreach (KittyFrame frame in restored.Animation().Frames)
        {
            if (!withDecoder) { Assert.IsType<UndecodedImage>(frame.Handle); continue; }
            var pixels = Assert.IsType<Pixels>(frame.Handle);
            int index = restored.Animation().Frames.IndexOf(frame);
            Assert.True(KittyFrameCodec.TryDecode(source.Animation().Frames[index].Source!.Data!, 2, out byte[] expected));
            Assert.Equal(expected, pixels.Rgba);
        }
    }

    [Fact]
    public void The_largest_elapsed_time_a_snapshot_may_carry_restores_and_advances_without_wrapping()
    {
        var source = new Side(decoder: null);
        source.Send(Traffic());
        TerminalStateSnapshot snapshot = Export(source);
        snapshot.KittyImages![0].Animation!.ElapsedMs = 1L << 40;
        Side restored = Restore(snapshot, new ClientDecoder(), now: 1_000_000);
        restored.Now = 1_000_001;
        restored.Buffer.AdvanceKittyAnimations(restored.Now, out long? due);                                             // whole cycles are skipped, nothing wraps
        Assert.True(due is null || due.Value > restored.Now);
        // The exporting side clamps what it carries, so a daemon that has run for decades still produces a snapshot a restore accepts.
        source.Now = long.MaxValue / 2;
        Assert.Equal(1L << 40, Assert.Single(source.Buffer.ExportState(int.MaxValue).KittyImages!).Animation!.ElapsedMs);
    }

    [Fact]
    public void A_restored_session_keeps_deciding_like_the_one_it_came_from()
    {
        var source = new Side(decoder: null);
        source.Send(Traffic());
        Side restored = Restore(Export(source), new ClientDecoder(), now: 0);
        source.Replies.Clear();
        // New frames built on a PNG frame, on the edited root and on a frame the source composed: all from the sources a snapshot carried.
        string more = Kitty("a=f,f=32,s=1,v=1,i=1,c=3,x=0,z=50", Solid(5, 5, 5, 255, 1)) + Kitty("a=f,f=32,s=1,v=1,i=1,c=1,x=1", Solid(6, 6, 6, 255, 1))
            + Kitty("a=f,f=32,s=2,v=1,i=1,r=2", Solid(7, 7, 7, 255, 2)) + Kitty("a=a,i=1,c=5");
        source.Send(more);
        restored.Send(more);
        Assert.Equal(source.Replies, restored.Replies);
        Assert.All(restored.Replies, reply => Assert.EndsWith(";OK\x1b\\", reply, StringComparison.Ordinal));
        Assert.Equal(Json(source), Json(restored));
        Assert.Equal(source.Asset().PixelCharge, restored.Asset().PixelCharge);
    }

    [Fact]
    public void The_source_budget_evicts_frame_pixels_identically_on_every_side_and_an_evicted_base_is_refused_alike()
    {
        var daemon = new Side(decoder: null, budget: new ImageSourceBudget(maxSources: 3));
        var client = new Side(new ClientDecoder(), budget: new ImageSourceBudget(maxSources: 3));
        daemon.Send(Traffic());
        client.Send(Traffic());
        Assert.Equal(daemon.Replies, client.Replies);
        Assert.Equal(Json(daemon), Json(client));
        TerminalKittyAnimationState animation = Assert.Single(daemon.Buffer.ExportState(int.MaxValue).KittyImages!).Animation!;
        Assert.Contains(animation.Frames, frame => frame.Data.Length == 0);                                            // something was evicted...
        Assert.Contains(animation.Frames, frame => frame.Data.Length != 0);                                            // ...and something was kept
        Assert.Equal(daemon.Asset().PixelCharge, client.Asset().PixelCharge);                                          // eviction frees bytes, not the charge
        daemon.Replies.Clear(); client.Replies.Clear();
        string builds = string.Concat(Enumerable.Range(1, animation.Frames.Count).Select(n => Kitty($"a=f,f=32,s=1,v=1,i=1,c={n}", Solid(1, 1, 1, 255, 1))));
        daemon.Send(builds);
        client.Send(builds);
        Assert.Equal(daemon.Replies, client.Replies);
        Assert.Contains(daemon.Replies, reply => reply.Contains(";ENOTSUP:", StringComparison.Ordinal));               // an evicted base is unavailable on both
        // A restore reproduces the evicted state too: the same frames are gone, the same ones remain.
        Side restored = Restore(Export(client), new ClientDecoder(), now: 0, new ImageSourceBudget(maxSources: 3));
        Assert.Equal(Json(client), Json(restored));
    }

    /// <summary>Kitty commands for one transfer of <paramref name="data"/>, in 4 KiB base64 chunks the way a real client sends a large frame.</summary>
    private static string Chunked(string controls, byte[] data)
    {
        string base64 = Convert.ToBase64String(data);
        var commands = new System.Text.StringBuilder();
        for (int at = 0; at < base64.Length; at += 4096)
        {
            string chunk = base64.Substring(at, Math.Min(4096, base64.Length - at)), more = at + 4096 < base64.Length ? "1" : "0";
            commands.Append(Kitty((at == 0 ? controls + "," : "") + "m=" + more, chunk));
        }
        return commands.ToString();
    }

    private static byte[] Noise(int pixels, uint seed)
    {
        var bytes = new byte[pixels * 4];
        for (int i = 0; i < bytes.Length; i++) { seed = seed * 1664525 + 1013904223; bytes[i] = (byte)(seed >> 24); }
        return bytes;
    }

    /// <summary>
    /// The measured premise of the #1026 decision: for frames that do not pack (noise costs 4.008 bytes per pixel) the 32 MiB image-source budget and the 64 MiB per-screen
    /// pixel quota (8 bytes per frame pixel) allow the same number of frames, so the budget never refuses or evicts before the quota refuses, unless other retained images share it.
    /// At 640x480 both allow 26 frames beside the root: the 27th is refused by the quota and every one of the 26 still has its pixels.
    /// </summary>
    [Fact]
    public void Noise_frames_fill_the_pixel_quota_exactly_when_they_fill_the_source_budget_so_nothing_is_evicted_first()
    {
        const int width = 640, height = 480, pixels = width * height;
        var side = new Side(decoder: null, policy: Composing);
        side.Send(Chunked($"a=t,f=32,s={width},v={height},i=1", Noise(pixels, 1)));
        Assert.EndsWith(";OK\x1b\\", Assert.Single(side.Replies), StringComparison.Ordinal);
        int accepted = 0;
        for (uint seed = 2; seed < 40; seed++)
        {
            side.Replies.Clear();
            side.Send(Chunked($"a=f,f=32,s={width},v={height},i=1", Noise(pixels, seed)));
            if (!side.Replies[0].EndsWith(";OK\x1b\\", StringComparison.Ordinal)) break;
            accepted++;
        }
        Assert.Equal(26, accepted);
        Assert.Contains(";ENOSPC:Animation frame storage exceeded", side.Replies[0], StringComparison.Ordinal);
        TerminalKittyAnimationState animation = Assert.Single(side.Buffer.ExportState(int.MaxValue).KittyImages!).Animation!;
        Assert.Equal(1 + 26, animation.Frames.Count);
        Assert.NotNull(side.Asset().Source?.Data);                                                                     // the root's own bytes are still retained too
        Assert.Empty(animation.Frames[0].Data);                                                                        // an unedited root carries no packing of its own: its pixels are the image's retained bytes
        Assert.All(animation.Frames.Skip(1), frame => Assert.NotEmpty(frame.Data));                                    // nothing was evicted: every later frame still has its pixels
        Assert.True(side.Buffer.ImageSources.RetainedBytes <= ImageSourceBudget.DefaultMaxBytes);
        Assert.InRange(side.Buffer.ImageSources.RetainedBytes, ImageSourceBudget.DefaultMaxBytes - 1_000_000, ImageSourceBudget.DefaultMaxBytes);   // and the budget is all but full
    }

    /// <summary>
    /// The boundary the premise test does not reach: the budget also holds the root's own bytes and each frame's control bytes, so at 512x512 the quota allows 31 frames beside the root
    /// (66,060,288 of 67,108,864 bytes) while the retained sources of the root and 31 noise frames (33,617,920 bytes) pass the 32 MiB budget by 63,488: every frame is accepted, and the
    /// budget evicts the oldest source, which is the root's, with no other image involved. The budget is therefore slightly the tighter limit, by about a frame.
    /// </summary>
    [Fact]
    public void At_512_by_512_the_budget_binds_one_frame_before_the_quota_and_evicts_the_root_source()
    {
        const int width = 512, height = 512, pixels = width * height;
        var side = new Side(decoder: null, policy: Composing);
        side.Send(Chunked($"a=t,f=32,s={width},v={height},i=1", Noise(pixels, 1)));
        Assert.NotNull(side.Asset().Source?.Data);
        int accepted = 0;
        for (uint seed = 2; seed < 40; seed++)
        {
            side.Replies.Clear();
            side.Send(Chunked($"a=f,f=32,s={width},v={height},i=1", Noise(pixels, seed)));
            if (!side.Replies[0].EndsWith(";OK\x1b\\", StringComparison.Ordinal)) break;
            accepted++;
        }
        Assert.Equal(31, accepted);                                                                                    // the quota still accepts all 31
        Assert.Null(side.Asset().Source!.Data);                                                                        // but the budget evicted the root's bytes
        Assert.All(side.Animation().Frames.Skip(1), frame => Assert.NotNull(frame.Source?.Data));                      // and kept every frame
        Assert.True(side.Buffer.ImageSources.RetainedBytes <= ImageSourceBudget.DefaultMaxBytes);
    }

    /// <summary>The source count is a limit of its own: small flat frames cost almost no bytes or quota but each is one of the budget's 512 sources.</summary>
    [Fact]
    public void Many_small_animations_lose_their_oldest_sources_to_the_source_count_long_before_any_byte_or_quota_limit()
    {
        var side = new Side(decoder: null, policy: Composing);
        for (uint image = 1; image <= 9; image++)
        {
            side.Send(Kitty($"a=t,f=32,s=16,v=16,i={image}", Solid(1, 2, 3, 255, 256)));
            for (int frame = 0; frame < 63; frame++) side.Send(Kitty($"a=f,f=32,s=16,v=16,i={image},z=40", Solid((byte)frame, 2, 3, 255, 256)));
        }
        Assert.All(side.Replies, reply => Assert.EndsWith(";OK\x1b\\", reply, StringComparison.Ordinal));          // every command was accepted: quota and bytes are nowhere near their limits
        Assert.Equal(ImageSourceBudget.DefaultMaxSources, side.Buffer.ImageSources.Count);                              // 9 images x 64 sources = 576 asked for, 512 kept
        Assert.True(side.Buffer.ImageSources.RetainedBytes < 1024 * 1024);
        Assert.Empty(Assert.IsType<TerminalKittyAnimationState>(side.Buffer.ExportState(int.MaxValue).KittyImages!.First(image => image.Id == 1).Animation).Frames[1].Data);   // the first image's earliest frame lost its pixels
        Assert.NotEmpty(side.Buffer.ExportState(int.MaxValue).KittyImages!.First(image => image.Id == 9).Animation!.Frames[1].Data);
    }

    [Fact]
    public void Deleting_the_image_releases_every_frame_source_and_a_replacement_releases_the_replaced_frame()
    {
        var side = new Side(new ClientDecoder());
        side.Send(Traffic());
        Assert.Equal(1 + 4, side.Buffer.ImageSources.Count);                                                           // the root image bytes and four frame sources
        side.Send(Kitty("a=f,f=32,s=2,v=1,i=1,r=2", Solid(4, 4, 4, 255, 2)));                                          // replaces frame 2
        Assert.Equal(1 + 4, side.Buffer.ImageSources.Count);
        side.Send(Kitty("a=d,d=I,i=1"));
        Assert.Equal(0, side.Buffer.ImageSources.Count);
        Assert.Equal(0, side.Buffer.ImageSources.RetainedBytes);
    }

    private static object[] Case(string description, Action<TerminalKittyAnimationState> corrupt) => [description, corrupt];

    public static IEnumerable<object[]> Corruptions() =>
    [
        Case("a frame that is not a packing of the image", a => a.Frames[1].Data = [1, 2, 3]),
        Case("a frame of the wrong size", a => a.Frames[2].Data = KittyFrameCodec.Encode(new byte[4 * 3])),
        Case("no frames", a => a.Frames = []),
        Case("too many frames", a => a.Frames = Enumerable.Range(0, KittyAnimation.MaxFrames + 1).Select(_ => new TerminalKittyFrameState()).ToList()),
        Case("a playhead past the frames", a => a.Current = 99),
        Case("a displayed frame past the frames", a => a.Displayed = -1),
        Case("an unknown state", a => a.State = 9),
        Case("negative elapsed time", a => a.ElapsedMs = -1),
        Case("an absurd elapsed time", a => a.ElapsedMs = long.MaxValue),
        Case("elapsed time one past the bound", a => a.ElapsedMs = (1L << 40) + 1),
        Case("loops below infinite", a => a.LoopsRemaining = -2),
        Case("pixels on an unedited root", a => a.Frames[0].Edited = false),
        Case("an edited later frame", a => a.Frames[2].Edited = true),
    ];

    [Theory]
    [MemberData(nameof(Corruptions))]
    public void A_crafted_animation_snapshot_is_refused_before_it_restores_anything(string description, Action<TerminalKittyAnimationState> corrupt)
    {
        var source = new Side(decoder: null);
        source.Send(Traffic());
        TerminalStateSnapshot snapshot = Export(source);
        corrupt(snapshot.KittyImages![0].Animation!);
        var target = new Side(decoder: null);
        Assert.ThrowsAny<Exception>(() => TerminalStateTransfer.Restore(target.Buffer, target.Parser, snapshot));
        Assert.True(target.Buffer.Images.Count == 0 && !target.Buffer.HasKittyAnimations, description);
    }

    [Fact]
    public void Frames_that_would_pass_the_per_screen_pixel_quota_are_refused_in_a_snapshot()
    {
        var source = new Side(decoder: null);
        source.Send(Kitty("a=T,f=32,s=2,v=1,i=1,p=1", B64(1, 2, 3, 255, 4, 5, 6, 255)) + Kitty("a=f,f=32,s=2,v=1,i=1", Solid(1, 1, 1, 255, 2)));
        TerminalStateSnapshot snapshot = Export(source);
        TerminalKittyImageState image = snapshot.KittyImages![0];
        (image.Width, image.Height) = (2000, 2000);                                                                    // 16 MB root plus 31 frames of 32 MB each is far over 64 MiB
        image.Animation!.Frames = Enumerable.Range(0, 32).Select(_ => new TerminalKittyFrameState()).ToList();
        var target = new Side(decoder: null);
        Assert.ThrowsAny<Exception>(() => TerminalStateTransfer.Restore(target.Buffer, target.Parser, snapshot));
    }

    [Fact]
    public void A_still_images_snapshot_is_unchanged_by_animation_support()
    {
        var side = new Side(decoder: null);
        side.Send(Kitty("a=T,f=32,s=2,v=1,i=1,p=1", B64(1, 2, 3, 255, 4, 5, 6, 255)));
        string json = Json(side);
        Assert.DoesNotContain("animation", json, StringComparison.Ordinal);
        Assert.Null(Assert.Single(side.Buffer.ExportState(int.MaxValue).KittyImages!).Animation);
    }

    private static string Json(Side side)
    {
        TerminalStateSnapshot snapshot = side.Buffer.ExportState(int.MaxValue);
        return JsonSerializer.Serialize(new { snapshot.KittyImages, snapshot.KittyPlacements });
    }
}
