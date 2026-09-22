using System;
using System.Text;
using Ntilde.Pty;
using Xunit;

namespace Ntilde.Platform.Tests.Pty;

/// <summary>
/// <see cref="Utf8ChunkDecoder"/> replaces <c>Encoding.UTF8.GetDecoder()</c> on both live
/// output paths (RustPtySession.ReadLoop, NativeSshSession.EmitOutput). "Replaces" has to mean
/// byte-for-byte identical output for every split of every input, including malformed ones —
/// a decoder that differs by one U+FFFD changes what the parser sees, which changes the screen.
/// So the headline test is differential against the type it replaces, not against hand-written
/// expectations.
/// </summary>
public class Utf8ChunkDecoderTests
{
    /// <summary>Decodes <paramref name="chunks"/> through the new decoder, concatenating output.</summary>
    private static string DecodeAll(byte[][] chunks)
    {
        var decoder = new Utf8ChunkDecoder();
        var sb = new StringBuilder();
        foreach (byte[] chunk in chunks)
        {
            char[] dest = new char[Utf8ChunkDecoder.GetMaxCharCount(chunk.Length)];
            int n = decoder.Decode(chunk, dest);
            sb.Append(dest, 0, n);
        }

        return sb.ToString();
    }

    /// <summary>Decodes the same chunks through the stateful decoder this type replaces.</summary>
    private static string DecodeAllReference(byte[][] chunks)
    {
        Decoder reference = Encoding.UTF8.GetDecoder();
        var sb = new StringBuilder();
        foreach (byte[] chunk in chunks)
        {
            char[] dest = new char[Encoding.UTF8.GetMaxCharCount(chunk.Length)];
            int n = reference.GetChars(chunk, 0, chunk.Length, dest, 0, flush: false);
            sb.Append(dest, 0, n);
        }

        return sb.ToString();
    }

    private static byte[][] SplitAt(byte[] input, int offset) =>
        [input[..offset], input[offset..]];

    public static TheoryData<string> Corpus() => new()
    {
        "hello world",
        "café naïve",          // 2-byte sequences
        "你好世界",       // 3-byte sequences
        "\U0001F600\U0001F1EC\U0001F1E7",  // 4-byte sequences (emoji, regional indicators)
        "áé́",            // combining marks
        "\U0001F468‍\U0001F469‍\U0001F467", // ZWJ family cluster
        "\u001b[31mred\u001b[0m",          // escape sequences stay intact
    };

    [Theory]
    [MemberData(nameof(Corpus))]
    public void MatchesEncodingUtf8Decoder_AtEverySplitOffset(string text)
    {
        byte[] input = Encoding.UTF8.GetBytes(text);

        for (int offset = 0; offset <= input.Length; offset++)
        {
            byte[][] chunks = SplitAt(input, offset);
            Assert.Equal(DecodeAllReference(chunks), DecodeAll(chunks));
        }
    }

    [Fact]
    public void MatchesEncodingUtf8Decoder_ForRandomByteStreams_AtEverySplitOffset()
    {
        // Fixed seed: a differential failure must be reproducible from the test name alone.
        var rng = new Random(0x5EED);

        for (int iteration = 0; iteration < 200; iteration++)
        {
            byte[] input = new byte[rng.Next(1, 48)];
            rng.NextBytes(input);

            for (int offset = 0; offset <= input.Length; offset++)
            {
                byte[][] chunks = SplitAt(input, offset);
                Assert.Equal(DecodeAllReference(chunks), DecodeAll(chunks));
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void FourByteCodePoint_SplitAnywhere_DecodesAsOneCodePoint(int firstChunkLength)
    {
        byte[] input = Encoding.UTF8.GetBytes("\U0001F600");
        Assert.Equal(4, input.Length);

        byte[][] chunks = SplitAt(input, firstChunkLength);
        Assert.Equal("\U0001F600", DecodeAll(chunks));
    }

    [Fact]
    public void InvalidBytes_ProduceTheSameReplacementRunAsEncodingUtf8()
    {
        byte[] input = [0xC3, 0x28, 0xA0, 0xA1, 0xE2, 0x28, 0xA1, 0xF0, 0x28, 0x8C, 0x28];

        Assert.Equal(DecodeAllReference([input]), DecodeAll([input]));
    }

    [Fact]
    public void LoneContinuationBytes_ProduceTheSameReplacementRunAsEncodingUtf8()
    {
        byte[] input = [0x80, 0x80, 0xBF, (byte)'a', 0x80];

        Assert.Equal(DecodeAllReference([input]), DecodeAll([input]));
    }

    [Fact]
    public void PendingTail_HoldsTheIncompletePrefixAndClearsWhenCompleted()
    {
        byte[] input = Encoding.UTF8.GetBytes("\U0001F600");
        var decoder = new Utf8ChunkDecoder();
        char[] dest = new char[Utf8ChunkDecoder.GetMaxCharCount(4)];

        Assert.Equal(0, decoder.Decode(input.AsSpan(0, 3), dest));
        Assert.Equal(new byte[] { input[0], input[1], input[2] }, decoder.PendingTail.ToArray());
        Assert.Equal(0L, decoder.ConsumedBytes);

        Assert.Equal(2, decoder.Decode(input.AsSpan(3), dest));
        Assert.Empty(decoder.PendingTail.ToArray());
        Assert.Equal(4L, decoder.ConsumedBytes);
    }

    [Fact]
    public void Reset_DropsThePendingTailAndCountsItAsConsumed()
    {
        byte[] input = Encoding.UTF8.GetBytes("\U0001F600");
        var decoder = new Utf8ChunkDecoder();
        char[] dest = new char[Utf8ChunkDecoder.GetMaxCharCount(4)];

        decoder.Decode(input.AsSpan(0, 3), dest);
        decoder.Reset();

        Assert.Empty(decoder.PendingTail.ToArray());
        Assert.Equal(3L, decoder.ConsumedBytes);

        // The stream resumes cleanly: the orphaned continuation byte is replaced, not joined.
        int n = decoder.Decode(input.AsSpan(3), dest);
        Assert.Equal("�", new string(dest, 0, n));
    }

    [Fact]
    public void ConsumedBytes_ExcludesThePendingTail()
    {
        byte[] input = Encoding.UTF8.GetBytes("ab你");  // 'a','b' then a 3-byte sequence
        var decoder = new Utf8ChunkDecoder();
        char[] dest = new char[Utf8ChunkDecoder.GetMaxCharCount(input.Length)];

        decoder.Decode(input.AsSpan(0, 3), dest); // 'a', 'b', first byte of the 3-byte sequence
        Assert.Equal(2L, decoder.ConsumedBytes);
        Assert.Single(decoder.PendingTail.ToArray());
    }
}
