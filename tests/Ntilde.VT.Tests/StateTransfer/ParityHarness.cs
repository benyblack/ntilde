using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Unicode;
using Ntilde.Replay;
using Ntilde.VT;
using Xunit;

namespace Ntilde.VT.Tests.StateTransfer;

/// <summary>
/// One parser+buffer pair driven a byte at a time, tracking what a decoder would be holding back.
/// </summary>
/// <remarks>
/// <para>
/// Byte-at-a-time is the point, not a simplification: it makes every byte offset a legal cut
/// point and exercises mid-sequence chunking on every corpus for free.
/// </para>
/// <para>
/// The tail tracking is a deliberate re-implementation of <c>Utf8ChunkDecoder</c>, not a
/// workaround for a layering rule: this project references Ntilde.Pty and could simply use the
/// real type. It mirrors it instead so the two can be compared against each other -
/// <c>ParityRunDecoderEquivalenceTests</c> asserts that this run's
/// <see cref="ConsumedBytes"/>/<see cref="PendingTail"/> equal a real decoder's at every split
/// offset. A harness built on the type under test proves the snapshot's stream position against
/// itself; a mirror that is independently proved equal does not.
/// </para>
/// <para>
/// It tracks the tail with <see cref="Utf8.ToUtf16"/> rather than by watching whether
/// <c>Encoding.UTF8.GetDecoder().GetChars</c> emitted anything, because "emitted a char" does not
/// mean "is holding nothing back". On <c>C3 C3</c> the framework decoder emits one U+FFFD for the
/// first byte while still carrying the second, so a tail cleared on every non-empty emission
/// would record a stream position that is one byte too far - and the parity run restored from it
/// would legitimately disagree with the continuous run, a manufactured failure with no bug
/// behind it. <c>Utf8.ToUtf16</c> reports <c>bytesRead</c>, which is the thing actually needed,
/// and is exactly what the production <c>Utf8ChunkDecoder</c> is built on.
/// </para>
/// </remarks>
internal sealed class ParityRun
{
    /// <summary>Longest incomplete-but-valid UTF-8 prefix: a 4-byte sequence missing its last byte.</summary>
    private const int MaxTailLength = 3;

    private readonly byte[] _tail = new byte[MaxTailLength + 1];
    private readonly char[] _chars = new char[8];
    private int _tailLength;

    public ParityRun(int cols, int rows, bool forceConPtyFiltering)
    {
        Buffer = new TerminalBuffer(cols, rows);
        Parser = new AnsiParser(Buffer, forceConPtyFiltering) { ImageDecoder = null };
        Parser.OnResponse = r => Responses.Add(r);
    }

    public TerminalBuffer Buffer { get; }

    public AnsiParser Parser { get; }

    public List<string> Responses { get; } = new();

    /// <summary>Bytes turned into chars so far; excludes <see cref="PendingTail"/>.</summary>
    public long ConsumedBytes { get; private set; }

    /// <summary>The incomplete UTF-8 prefix the decoder is holding.</summary>
    public byte[] PendingTail => _tail.AsSpan(0, _tailLength).ToArray();

    /// <summary>
    /// Feeds <paramref name="corpus"/> from <paramref name="from"/> (inclusive) to
    /// <paramref name="to"/> (exclusive), applying every resize in <paramref name="resizes"/>
    /// whose offset falls in that range at the moment the feed reaches it.
    /// </summary>
    /// <remarks>
    /// Deliberately half-open, with no resize applied at the <paramref name="to"/> boundary. A
    /// resize sitting exactly on a cut point would otherwise fire twice in the continuous run -
    /// once as <c>to</c> in <c>Feed(0, cut)</c> and again as <c>i</c> in <c>Feed(cut, len)</c> -
    /// while the restored run fires it once. A resize at <c>corpus.Length</c> therefore never
    /// fires at all, which is harmless: nothing is parsed after the last byte.
    /// </remarks>
    public void Feed(byte[] corpus, int from, int to, (int Offset, int Cols, int Rows)[] resizes)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(resizes);

        for (int i = from; i < to; i++)
        {
            foreach ((int _, int cols, int rows) in resizes.Where(r => r.Offset == i))
            {
                Buffer.Resize(cols, rows);
            }

            _tail[_tailLength++] = corpus[i];

            Utf8.ToUtf16(
                _tail.AsSpan(0, _tailLength),
                _chars,
                out int bytesRead,
                out int charsWritten,
                replaceInvalidSequences: true,
                isFinalBlock: false);

            if (bytesRead > 0)
            {
                ConsumedBytes += bytesRead;
                for (int j = bytesRead; j < _tailLength; j++)
                {
                    _tail[j - bytesRead] = _tail[j];
                }

                _tailLength -= bytesRead;
            }

            if (_tailLength > MaxTailLength)
            {
                // An incomplete UTF-8 prefix is at most 3 bytes, so this cannot happen. If it
                // ever does, the stream offset the snapshot records is wrong and silence here
                // would turn that into an unexplainable parity failure much later.
                throw new InvalidOperationException(
                    $"UTF-8 carry of {_tailLength} bytes exceeds the {MaxTailLength}-byte maximum " +
                    $"at corpus offset {i}; the harness's tail invariant is broken.");
            }

            if (charsWritten > 0)
            {
                Parser.Process(new string(_chars, 0, charsWritten));
            }
        }
    }
}

/// <summary>
/// Proves that a pane attaching mid-stream - snapshot, then parse the rest of the bytes itself -
/// reaches exactly the state it would have reached by parsing the whole stream from the start.
/// </summary>
internal static class ParityHarness
{
    /// <summary>
    /// Proves "snapshot + tail" equals "continuous" for one corpus stream.
    /// </summary>
    /// <param name="name">Corpus name, quoted in every failure message.</param>
    /// <param name="corpus">The byte stream.</param>
    /// <param name="cutPoints">Byte offsets at which to snapshot and resume.</param>
    /// <param name="resizes">(byte offset, cols, rows) applied to every run at that offset.</param>
    /// <param name="forceConPtyFiltering">
    /// Passed to every <see cref="AnsiParser"/> so the result does not depend on the host OS.
    /// </param>
    public static void AssertSnapshotTailParity(
        string name,
        byte[] corpus,
        int[] cutPoints,
        (int Offset, int Cols, int Rows)[] resizes,
        bool forceConPtyFiltering)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(cutPoints);
        ArgumentNullException.ThrowIfNull(resizes);

        foreach (int cut in cutPoints)
        {
            // A: the whole stream, continuously. Rebuilt per cut point so "responses after the
            // cut" can be isolated without replaying bookkeeping.
            var a = new ParityRun(80, 24, forceConPtyFiltering);
            a.Feed(corpus, 0, cut, resizes);
            int responsesBeforeCut = a.Responses.Count;
            a.Feed(corpus, cut, corpus.Length, resizes);
            List<string> responsesAfterCut = a.Responses.Skip(responsesBeforeCut).ToList();

            // B: the prefix only, then snapshot.
            var b = new ParityRun(80, 24, forceConPtyFiltering);
            b.Feed(corpus, 0, cut, resizes);

            TerminalStateSnapshot snapshot = b.Buffer.ExportState(int.MaxValue);
            snapshot.Parser = b.Parser.ExportState();
            snapshot.DecoderTail = b.PendingTail;
            snapshot.StreamSeq = b.ConsumedBytes;

            if (snapshot.StreamSeq + snapshot.DecoderTail.Length != cut)
            {
                Assert.Fail(
                    $"[{name} @ {cut}] snapshot claims stream position " +
                    $"{snapshot.StreamSeq}+{snapshot.DecoderTail.Length} but the cut was at {cut}.");
            }

            // C: restored from the snapshot, then fed the tail INCLUDING the pending bytes.
            // Constructed at 80x24 like every other run and left for TerminalStateTransfer.Restore
            // to resize to the snapshot's dimensions. It used to be constructed at the snapshot's
            // dimensions by hand, because ImportState does not resize and a mismatch degrades
            // silently instead of throwing - a defence the helper now owns, so
            // SnapshotTailParityTests' resize-interleaved theories are also what exercise it.
            //
            // C re-feeds from StreamSeq, which is at or before the cut, so a resize whose offset
            // lands inside the pending decoder tail is applied twice: once by B on its way to the
            // snapshot, once by C on the re-feed. That is inert ONLY because TerminalBuffer.Resize
            // early-returns when the dimensions are unchanged
            // (TerminalBuffer.ResizeAndReflow.cs: "if (newCols == Cols && newRows == Rows) return;"),
            // so the second application changes nothing and emits no in-band resize report under
            // mode 2048. If that early return ever goes away, this is the line to fix - the symptom
            // will be a phantom parity divergence with no bug behind it.
            // Through TerminalStateTransfer.Restore rather than the three underlying calls, so the
            // entry point Phase 1 will attach with is the one this suite proves. Its third step -
            // seeding the parser's OSC 8 registry from the table the buffer rebuilt - is the half
            // of link identity that AnsiParserState deliberately does not carry: the buffer's table
            // restores identity for cells that already exist, while the registry is what an OSC 8
            // in the TAIL consults. Without it a re-referenced `id=` mints a second Hyperlink and
            // those cells stop grouping, which is what this harness caught on osc8-hyperlinks at
            // cut 36.
            var c = new ParityRun(80, 24, forceConPtyFiltering);
            TerminalStateTransfer.Restore(c.Buffer, c.Parser, snapshot);

            c.Feed(corpus, (int)snapshot.StreamSeq, corpus.Length, resizes);

            AssertEquivalent(name, cut, a, responsesAfterCut, c);
        }
    }

    /// <summary>
    /// The specification of what parity means: every field of the exported state, compared. A
    /// field left out here is a field the snapshot is free to get wrong.
    /// </summary>
    private static void AssertEquivalent(
        string name, int cut, ParityRun a, List<string> responsesAfterCut, ParityRun c)
    {
        string where = $"[{name} @ cut {cut}]";
        TerminalStateAssert.AssertEquivalent(where, a.Buffer, a.Parser, c.Buffer, c.Parser);
        TerminalStateAssert.AssertLinesEqual(
            $"{where} device replies", responsesAfterCut.ToArray(), c.Responses.ToArray());
    }
}
