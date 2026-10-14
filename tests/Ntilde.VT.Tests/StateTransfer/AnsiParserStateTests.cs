using System;
using Ntilde.VT;
using Xunit;

namespace Ntilde.VT.Tests.StateTransfer;

/// <summary>
/// Pins that a parser's cross-call state is fully transferable. The parity test in
/// <c>SnapshotTailParityTests</c> is the real proof; these are the targeted cases that say
/// <em>which</em> field broke when it fails.
/// </summary>
public class AnsiParserStateTests
{
    private static (AnsiParser Parser, TerminalBuffer Buffer) NewPair(int cols = 80, int rows = 24)
    {
        var buffer = new TerminalBuffer(cols, rows);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { ImageDecoder = null };
        return (parser, buffer);
    }

    /// <summary>
    /// Transfers <paramref name="source"/>'s state onto a fresh parser over a fresh buffer of the
    /// same size, then returns the pair.
    /// </summary>
    private static (AnsiParser Parser, TerminalBuffer Buffer) Transfer(AnsiParser source, TerminalBuffer sourceBuffer)
    {
        (AnsiParser parser, TerminalBuffer buffer) = NewPair(sourceBuffer.Cols, sourceBuffer.Rows);
        parser.ImportState(source.ExportState());
        return (parser, buffer);
    }

    [Fact]
    public void CsiSplitAcrossChunks_CompletesAfterTransfer()
    {
        (AnsiParser a, TerminalBuffer bufA) = NewPair();
        a.Process("\u001b[1;3");

        (AnsiParser c, TerminalBuffer bufC) = Transfer(a, bufA);
        c.Process("1H");
        a.Process("1H");

        Assert.Equal(a.ExportState().ToDebugString(), c.ExportState().ToDebugString());
        Assert.Equal(bufA.CursorRow, bufC.CursorRow);
        Assert.Equal(bufA.CursorCol, bufC.CursorCol);
    }

    [Fact]
    public void OscSplitAcrossChunks_CompletesAfterTransfer()
    {
        (AnsiParser a, TerminalBuffer bufA) = NewPair();
        string? titleA = null;
        a.OnTitleChanged = t => titleA = t;
        a.Process("\u001b]0;my ti");

        (AnsiParser c, _) = Transfer(a, bufA);
        string? titleC = null;
        c.OnTitleChanged = t => titleC = t;

        a.Process("tle\u0007");
        c.Process("tle\u0007");

        Assert.Equal("my title", titleA);
        Assert.Equal(titleA, titleC);
    }

    [Fact]
    public void ChunkedKittyPayload_ResumesAfterTransfer()
    {
        (AnsiParser a, TerminalBuffer bufA) = NewPair();
        a.Process("\u001b_Ga=T,f=24,s=1,v=1,m=1;AAAA\u001b\\");

        AnsiParserState state = a.ExportState();

        Assert.NotEmpty(state.KittyPayload);
        Assert.NotEmpty(state.KittyPendingParams);

        (AnsiParser c, _) = Transfer(a, bufA);
        Assert.Equal(state.ToDebugString(), c.ExportState().ToDebugString());
    }

    [Fact]
    public void CharsetDesignationAndShift_SurviveTransfer()
    {
        (AnsiParser a, TerminalBuffer bufA) = NewPair();
        // Designate G1 as DEC special graphics, then SO (shift-out) to invoke it into GL. Per
        // ECMA-48, SO/LS1 invokes G1 (not G0) - this is the standard `enacs`/`smacs` idiom
        // (e.g. screen's terminfo: designate G1 via ESC ) 0, toggle with SO/SI). Designating
        // G0 instead would leave SO shifting to an untouched (still-ASCII) G1, and 'q' would
        // never be mapped - which is not what this test means to exercise.
        a.Process("\u001b)0\u000e");

        (AnsiParser c, TerminalBuffer bufC) = Transfer(a, bufA);
        a.Process("q");
        c.Process("q");

        bufA.Lock.EnterReadLock();
        bufC.Lock.EnterReadLock();
        try
        {
            Assert.Equal(bufA.ViewportRows[0].Cells[0].Character, bufC.ViewportRows[0].Cells[0].Character);
            Assert.NotEqual('q', bufC.ViewportRows[0].Cells[0].Character);
        }
        finally
        {
            bufC.Lock.ExitReadLock();
            bufA.Lock.ExitReadLock();
        }
    }

    [Fact]
    public void InBandResizeMode2048_SurvivesTransfer()
    {
        (AnsiParser a, TerminalBuffer bufA) = NewPair();
        a.Process("\u001b[?2048h");
        Assert.True(a.InBandResizeReportsEnabled);

        (AnsiParser c, _) = Transfer(a, bufA);
        Assert.True(c.InBandResizeReportsEnabled);
    }

    [Fact]
    public void RepeatCharacter_RepeatsTheSameCharacterAfterTransfer()
    {
        (AnsiParser a, TerminalBuffer bufA) = NewPair();
        a.Process("x");

        (AnsiParser c, TerminalBuffer bufC) = Transfer(a, bufA);
        a.Process("\u001b[3b");
        c.Process("\u001b[3b");

        bufA.Lock.EnterReadLock();
        bufC.Lock.EnterReadLock();
        try
        {
            // bufA: 'x' typed at col 0, then three more from REP at cols 1-3.
            for (int col = 0; col < 4; col++)
            {
                Assert.Equal('x', bufA.ViewportRows[0].Cells[col].Character);
            }

            // Buffer content/cursor position is deliberately NOT part of AnsiParserState (it is
            // TerminalBuffer's - Task 7's TerminalStateSnapshot concern), so Transfer() lands `c`
            // on a fresh buffer whose cursor is still at col 0. REP therefore writes its repeats
            // starting from col 0, not col 1 as it did on bufA. What this test actually pins is
            // that the repeated character is still 'x' - i.e. _lastGraphicChar survived the
            // transfer - not that the two buffers end up byte-for-byte aligned, which would
            // require buffer-level state this task does not own.
            for (int col = 0; col < 3; col++)
            {
                Assert.Equal('x', bufC.ViewportRows[0].Cells[col].Character);
            }
        }
        finally
        {
            bufC.Lock.ExitReadLock();
            bufA.Lock.ExitReadLock();
        }
    }

    /// <summary>
    /// The text batching buffer is deliberately not exported, on the claim that it is always
    /// empty when <c>Process()</c> returns. That claim is load-bearing - if it were ever false,
    /// a snapshot would silently drop pending printable text - so it is asserted, not assumed.
    /// </summary>
    [Theory]
    [InlineData("plain text")]
    [InlineData("text then \u001b[31mSGR")]
    [InlineData("wide 你好 and emoji \U0001F600")]
    [InlineData("incomplete escape \u001b[1;")]
    [InlineData("\u001b]8;;https://example.com\u0007linked")]
    public void TextBatchingBuffer_IsEmptyWhenProcessReturns(string input)
    {
        (AnsiParser a, _) = NewPair();
        a.Process(input);

        Assert.Equal(0, a.ExportState().PendingTextLength);
    }

    /// <summary>
    /// <c>ToDebugString()</c> is the equality oracle the snapshot-parity tests compare two parsers
    /// with, across hundreds of cut points and every corpus stream. <c>Dictionary</c> enumeration
    /// order is unspecified, so rendering <c>KittyPendingParams</c> in it would let two identical
    /// parsers occasionally disagree - a parity failure indistinguishable from a real divergence,
    /// and the most expensive possible way to discover a missing sort. Hence this test rather than
    /// a comment.
    /// </summary>
    [Fact]
    public void ToDebugString_RendersKittyPendingParamsInSortedOrder()
    {
        var state = new AnsiParserState();

        // Inserted in deliberately non-sorted order; insertion order is what an unsorted
        // rendering would most likely echo back.
        state.KittyPendingParams["z"] = "26";
        state.KittyPendingParams["m"] = "1";
        state.KittyPendingParams["a"] = "T";
        state.KittyPendingParams["f"] = "100";

        string rendered = state.ToDebugString();

        Assert.Contains("kparams=a=T,f=100,m=1,z=26,", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// The sort must not depend on the order the keys went in, which is the whole point: two
    /// parsers that reached the same state by different insertion orders must render identically.
    /// </summary>
    [Fact]
    public void ToDebugString_IsIndependentOfKittyPendingParamInsertionOrder()
    {
        var ascending = new AnsiParserState();
        foreach (string key in new[] { "a", "f", "m", "z" })
        {
            ascending.KittyPendingParams[key] = key.ToUpperInvariant();
        }

        var descending = new AnsiParserState();
        foreach (string key in new[] { "z", "m", "f", "a" })
        {
            descending.KittyPendingParams[key] = key.ToUpperInvariant();
        }

        Assert.Equal(ascending.ToDebugString(), descending.ToDebugString());
    }
}
