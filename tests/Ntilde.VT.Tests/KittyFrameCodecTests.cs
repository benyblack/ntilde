using System;
using System.Linq;
using Xunit;

namespace Ntilde.VT.Tests;

/// <summary>The deterministic packed frame form persistent sessions retain (#1017).</summary>
public sealed class KittyFrameCodecTests
{
    private static byte[] Pixels(int count, Func<int, byte[]> pixel) => Enumerable.Range(0, count).SelectMany(pixel).ToArray();

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(130)]
    [InlineData(1000)]
    public void A_solid_frame_round_trips_in_a_few_bytes(int count)
    {
        byte[] rgba = Pixels(count, _ => [9, 8, 7, 255]);
        byte[] packed = KittyFrameCodec.Encode(rgba);
        Assert.True(KittyFrameCodec.TryDecode(packed, count, out byte[] decoded));
        Assert.Equal(rgba, decoded);
        Assert.True(packed.Length <= 5 * ((count + 128) / 129), "a solid frame costs five bytes per 129 pixels");
    }

    [Theory]
    [InlineData(1, 100)]
    [InlineData(2, 300)]
    [InlineData(3, 257)]
    public void Noise_and_mixed_runs_round_trip_and_cost_at_most_one_byte_per_128_pixels_extra(int seed, int count)
    {
        var random = new Random(seed);
        byte[] rgba = Pixels(count, i => i % 7 < 3 ? [1, 2, 3, 4] : [(byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)]);
        byte[] packed = KittyFrameCodec.Encode(rgba);
        Assert.True(KittyFrameCodec.TryDecode(packed, count, out byte[] decoded));
        Assert.Equal(rgba, decoded);
        Assert.True(packed.Length <= rgba.Length + count / 128 + 8);
        byte[] noise = Pixels(count, i => [(byte)(i * 31), (byte)(i * 7 + 1), (byte)(i * 13 + 2), (byte)(i * 3 + 5)]);
        Assert.True(KittyFrameCodec.Encode(noise).Length <= noise.Length + (count + 127) / 128);
    }

    [Fact]
    public void Encoding_is_deterministic_and_independent_of_any_runtime_compressor()
    {
        byte[] rgba = Pixels(500, i => i % 50 < 20 ? [255, 0, 0, 255] : [(byte)i, (byte)(i >> 1), 0, 255]);
        Assert.Equal(KittyFrameCodec.Encode(rgba), KittyFrameCodec.Encode((byte[])rgba.Clone()));
        // Pinned bytes: a run of three red pixels then one literal blue pixel.
        Assert.Equal(new byte[] { 129, 255, 0, 0, 255, 0, 0, 0, 255, 255 }, KittyFrameCodec.Encode([255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 0, 0, 255, 255]));
    }

    [Fact]
    public void A_packing_of_the_wrong_length_or_shape_is_refused()
    {
        byte[] packed = KittyFrameCodec.Encode(Pixels(10, _ => [1, 2, 3, 4]));
        Assert.True(KittyFrameCodec.TryDecode(packed, 10, out _));
        Assert.False(KittyFrameCodec.TryDecode(packed, 9, out _));                               // would write past the end
        Assert.False(KittyFrameCodec.TryDecode(packed, 11, out _));                              // too few pixels
        Assert.False(KittyFrameCodec.TryDecode(packed.AsSpan(0, packed.Length - 1), 10, out _));                        // truncated pixel
        Assert.False(KittyFrameCodec.TryDecode([5, 1, 2, 3, 4], 6, out _));                      // literal of six pixels with one present
        Assert.False(KittyFrameCodec.TryDecode([], 1, out _));
        Assert.False(KittyFrameCodec.TryDecode(packed, 0, out _));
        Assert.False(KittyFrameCodec.TryDecode(packed, -1, out _));
        Assert.Throws<ArgumentException>(() => KittyFrameCodec.Encode([1, 2, 3]));
    }

    [Fact]
    public void A_repeat_cannot_expand_past_the_declared_size()
    {
        // 255 repeats 129 pixels; asking for 100 must be refused rather than clipped.
        Assert.False(KittyFrameCodec.TryDecode([255, 1, 2, 3, 4], 100, out _));
        Assert.True(KittyFrameCodec.TryDecode([255, 1, 2, 3, 4], 129, out byte[] full));
        Assert.Equal(129 * 4, full.Length);
    }
}
