using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Ntilde.VT.Tests.StateTransfer;

/// <summary>
/// The point of Phase 0: a pane that attaches to a running session mid-stream - takes a
/// snapshot, then parses the rest of the bytes itself - must end up in exactly the state it
/// would have reached by parsing the whole stream from the start. Everything else in this phase
/// exists so this can be asserted.
/// </summary>
/// <remarks>
/// A failure here is a bug in export/import, never a reason to relax the comparison. The
/// comparison IS the specification.
/// </remarks>
public class SnapshotTailParityTests
{
    /// <summary>
    /// Cut points per corpus. Deterministic (fixed seed), and deliberately not uniform: the
    /// interesting cuts land inside an escape sequence or inside a multi-byte code point, which
    /// uniform spacing across a stream of mostly-ASCII would mostly miss.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cut count is scaled inversely with the stream length, because the harness's cost is
    /// roughly <c>3 x length x cuts</c> byte-steps (run A is rebuilt per cut, and every run is fed
    /// a byte at a time). A flat cap of 120 cuts would spend ~25 million byte-steps on
    /// <c>csi-truncating</c> alone - 70 KB of a single repeated construct - per theory, for
    /// coverage that a dozen cuts already bought: the only structurally distinct offsets in it are
    /// the ones near the ESC, near the parameter-truncation threshold, and near the final byte.
    /// </para>
    /// <para>
    /// Small streams keep the dense, structure-aware selection in full. The budget below holds the
    /// whole suite - three theories over 36 streams - to roughly 20 million byte-steps, which is
    /// the difference between a parity suite that runs on every push and one that someone disables
    /// within a month.
    /// </para>
    /// </remarks>
    private static int[] CutPointsFor(byte[] corpus)
    {
        ArgumentNullException.ThrowIfNull(corpus);

        // Size classes. Anchors/random are the discretionary cuts; the structural ones (post-ESC,
        // UTF-8 continuation) are always collected and only thinned by the final cap, so a dense
        // stream never loses its interesting offsets to a stream that is merely long.
        (int maxCuts, int spread, int randomSamples) = corpus.Length switch
        {
            <= 512 => (120, 16, 24),
            <= 4_096 => (96, 16, 16),
            <= 16_384 => (48, 16, 8),
            _ => (24, 16, 4),
        };

        var rng = new Random(0xC0FFEE);
        var cuts = new SortedSet<int>();

        // Anchors: the ends, and a spread across the middle.
        cuts.Add(0);
        cuts.Add(corpus.Length);
        for (int i = 1; i < spread; i++)
        {
            cuts.Add(corpus.Length * i / spread);
        }

        // Every offset that sits one byte after an ESC, so the cut lands inside a sequence.
        for (int i = 0; i < corpus.Length - 1; i++)
        {
            if (corpus[i] == 0x1B)
            {
                cuts.Add(i + 1);
            }
        }

        // Every offset that sits between the bytes of a multi-byte code point.
        for (int i = 1; i < corpus.Length; i++)
        {
            if ((corpus[i] & 0xC0) == 0x80)
            {
                cuts.Add(i);
            }
        }

        // Plus a random sample, so a corpus with no escapes still gets coverage in depth.
        for (int i = 0; i < randomSamples && corpus.Length > 0; i++)
        {
            cuts.Add(rng.Next(corpus.Length + 1));
        }

        // Bounded: a recorded stream has hundreds of ESCs and the harness is O(n) per cut. Take a
        // deterministic, evenly-spread subset when there are too many.
        int[] ordered = cuts.ToArray();
        if (ordered.Length <= maxCuts)
        {
            return ordered;
        }

        return Enumerable.Range(0, maxCuts)
            .Select(i => ordered[(int)((long)i * ordered.Length / maxCuts)])
            .Distinct()
            .ToArray();
    }

    public static TheoryData<string> CorpusNames()
    {
        var data = new TheoryData<string>();
        foreach ((string name, _) in ParityCorpus.All())
        {
            data.Add(name);
        }

        return data;
    }

    private static byte[] CorpusBytes(string name) =>
        ParityCorpus.All().First(c => c.Name == name).Bytes;

    [Theory]
    [MemberData(nameof(CorpusNames))]
    public void SnapshotPlusTail_EqualsContinuous(string name)
    {
        byte[] corpus = CorpusBytes(name);

        ParityHarness.AssertSnapshotTailParity(
            name,
            corpus,
            CutPointsFor(corpus),
            resizes: [],
            forceConPtyFiltering: false);
    }

    /// <summary>
    /// Same proof with resizes interleaved. A resize reflows, which rebuilds the absolute-row
    /// coordinate space - so this is the case most likely to expose a snapshot field that is
    /// nearly right.
    /// </summary>
    [Theory]
    [MemberData(nameof(CorpusNames))]
    public void SnapshotPlusTail_EqualsContinuous_WithInterleavedResizes(string name)
    {
        byte[] corpus = CorpusBytes(name);
        if (corpus.Length < 8)
        {
            return;
        }

        (int Offset, int Cols, int Rows)[] resizes =
        [
            (corpus.Length / 4, 60, 20),
            (corpus.Length / 2, 100, 30),
            (corpus.Length * 3 / 4, 80, 24),
        ];

        ParityHarness.AssertSnapshotTailParity(
            name,
            corpus,
            CutPointsFor(corpus),
            resizes,
            forceConPtyFiltering: false);
    }

    /// <summary>
    /// ConPTY filtering changes how kitty graphics APC and capability probes are handled, and it
    /// is what every Windows session runs with. Both sides are constructed with the same value -
    /// this variant checks that the state transfer is correct under that value too, not that the
    /// two values agree with each other (they legitimately do not).
    /// </summary>
    [Theory]
    [MemberData(nameof(CorpusNames))]
    public void SnapshotPlusTail_EqualsContinuous_UnderConPtyFiltering(string name)
    {
        byte[] corpus = CorpusBytes(name);

        ParityHarness.AssertSnapshotTailParity(
            name,
            corpus,
            CutPointsFor(corpus),
            resizes: [],
            forceConPtyFiltering: true);
    }
}
