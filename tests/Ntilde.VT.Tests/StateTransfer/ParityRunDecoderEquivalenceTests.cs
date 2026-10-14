using System;
using System.Linq;
using Ntilde.Pty;
using Xunit;

namespace Ntilde.VT.Tests.StateTransfer;

/// <summary>
/// Pins <see cref="ParityRun"/>'s stream-position tracking against the production
/// <see cref="Utf8ChunkDecoder"/> it mirrors.
/// </summary>
/// <remarks>
/// <para>
/// The parity suite's whole claim rests on two numbers: <c>ConsumedBytes</c>, which becomes
/// <c>TerminalStateSnapshot.StreamSeq</c>, and <c>PendingTail</c>, which becomes
/// <c>DecoderTail</c>. Together they say "the next unseen byte is here", and the restored run is
/// fed from exactly that offset. If the harness's own tracking drifted from the decoder the
/// daemon will actually run, the suite would still be internally consistent - and would be
/// proving the snapshot against a copy of itself rather than against the thing it ships with.
/// </para>
/// <para>
/// So this is a differential test, in the same shape as <c>Utf8ChunkDecoderTests</c>: same bytes,
/// same chunk boundaries, compare the pair. The malformed shapes are the ones that matter -
/// <c>C3 C3</c> in particular, where a decoder emits a U+FFFD for the first byte while still
/// carrying the second, so "emitted a char" and "is holding nothing back" come apart. That case
/// is the reason ParityRun tracks bytesRead rather than watching for output, and it is the case
/// where a re-implementation is most likely to be subtly wrong.
/// </para>
/// </remarks>
public class ParityRunDecoderEquivalenceTests
{
    /// <summary>
    /// Malformed and boundary shapes, spelled out because a random sweep hits them only by luck.
    /// </summary>
    public static TheoryData<string, byte[]> MalformedShapes() => new()
    {
        // Two lead bytes in a row: the first is invalid (replaced), the second is still pending.
        { "double-lead-2byte", [0xC3, 0xC3] },
        { "double-lead-2byte-then-continuation", [0xC3, 0xC3, 0xA9] },
        { "triple-lead", [0xE4, 0xE4, 0xE4] },

        // A 4-byte code point truncated at every length, so the tail is 1, 2 and 3 bytes.
        { "4byte-truncated-1", [0xF0] },
        { "4byte-truncated-2", [0xF0, 0x9F] },
        { "4byte-truncated-3", [0xF0, 0x9F, 0x98] },
        { "4byte-complete", [0xF0, 0x9F, 0x98, 0x80] },

        // Continuation bytes with no lead: each is its own replacement, nothing is ever pending.
        { "lone-continuations", [0x80, 0x81, 0xBF] },

        // Bytes that can never appear in UTF-8 at all.
        { "invalid-bytes", [0xFE, 0xFF, 0xC0, 0xC1] },

        // Overlong and surrogate encodings: structurally lead-shaped, semantically illegal.
        { "overlong-nul", [0xC0, 0x80] },
        { "surrogate-half", [0xED, 0xA0, 0x80] },

        // A valid stream with a truncated code point hanging off the end, which is the exact
        // shape a snapshot taken mid-chunk has to describe.
        { "ascii-then-truncated", [0x68, 0x69, 0xE4, 0xBD] },

        // An escape sequence straddling multi-byte text, so the parser is mid-CSI while the
        // decoder is mid-code-point.
        { "esc-then-truncated", [0x1B, 0x5B, 0x33, 0x31, 0x6D, 0xF0, 0x9F] },
    };

    [Theory]
    [MemberData(nameof(MalformedShapes))]
    public void TracksTheSameStreamPositionAsTheProductionDecoder_ForMalformedShapes(
        string name, byte[] input)
    {
        ArgumentNullException.ThrowIfNull(input);

        for (int split = 0; split <= input.Length; split++)
        {
            AssertSamePositionAtEveryStep(name, input, split);
        }
    }

    /// <summary>
    /// The same random streams <c>Utf8ChunkDecoderTests</c> sweeps, at every split offset. Random
    /// bytes are mostly invalid UTF-8, which is what makes them the interesting corpus here: the
    /// replacement path is where a hand-written tail is easiest to get wrong.
    /// </summary>
    [Fact]
    public void TracksTheSameStreamPositionAsTheProductionDecoder_ForRandomByteStreams()
    {
        // Fixed seed, matching Utf8ChunkDecoderTests: a divergence must be reproducible from the
        // test name alone.
        var rng = new Random(0x5EED);

        for (int iteration = 0; iteration < 200; iteration++)
        {
            byte[] input = new byte[rng.Next(1, 48)];
            rng.NextBytes(input);

            for (int split = 0; split <= input.Length; split++)
            {
                AssertSamePositionAtEveryStep($"random#{iteration}", input, split);
            }
        }
    }

    /// <summary>
    /// The real corpus the parity suite runs on, fed in one go. Slower per stream than the random
    /// sweep, so it takes one split point rather than all of them - the point here is coverage of
    /// realistic byte distributions, which the synthetic and recorded streams have and 48 random
    /// bytes do not.
    /// </summary>
    [Fact]
    public void TracksTheSameStreamPositionAsTheProductionDecoder_ForTheParityCorpus()
    {
        foreach ((string name, byte[] bytes) in ParityCorpus.All())
        {
            AssertSamePositionAtEveryStep(name, bytes, bytes.Length / 2);
        }
    }

    /// <summary>
    /// Feeds both the harness's run and a real decoder the same bytes in the same two chunks,
    /// comparing the reported position after each chunk.
    /// </summary>
    private static void AssertSamePositionAtEveryStep(string name, byte[] input, int split)
    {
        var run = new ParityRun(80, 24, forceConPtyFiltering: false);
        var decoder = new Utf8ChunkDecoder();
        char[] destination = new char[Utf8ChunkDecoder.GetMaxCharCount(input.Length)];

        run.Feed(input, 0, split, []);
        decoder.Decode(input.AsSpan(0, split), destination);
        AssertSamePosition($"{name} after [0,{split})", run, decoder);

        run.Feed(input, split, input.Length, []);
        decoder.Decode(input.AsSpan(split), destination);
        AssertSamePosition($"{name} after [{split},{input.Length})", run, decoder);
    }

    private static void AssertSamePosition(string where, ParityRun run, Utf8ChunkDecoder decoder)
    {
        byte[] runTail = run.PendingTail;
        byte[] decoderTail = decoder.PendingTail.ToArray();

        if (run.ConsumedBytes != decoder.ConsumedBytes || !runTail.SequenceEqual(decoderTail))
        {
            Assert.Fail(
                $"[{where}] stream position diverged: harness={run.ConsumedBytes}+" +
                $"[{Hex(runTail)}], decoder={decoder.ConsumedBytes}+[{Hex(decoderTail)}].");
        }
    }

    private static string Hex(byte[] bytes) =>
        string.Join(' ', bytes.Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
}
