using Ntilde.VT;
using Ntilde.VT.Links;
using Xunit;

namespace Ntilde.VT.Tests.StateTransfer;

/// <summary>
/// The parser's OSC 8 interning registry is deliberately absent from <see cref="AnsiParserState"/> -
/// link identity has a single source, the buffer's link table - so a restored parser has to be
/// re-seeded from that table instead. These pin the rules that govern the re-seeding.
/// </summary>
/// <remarks>
/// Driven through <see cref="TerminalStateTransfer.Restore"/>, the supported attach entry point, so
/// these cover the same path <c>ParityHarness</c> and Phase 1 use rather than a second arrangement
/// of the three underlying calls.
/// </remarks>
public class HyperlinkRegistrySeedingTests
{
    private const string Uri = "https://example.com/a";

    private static string Open(string parameters, string uri) => $"\x1b]8;{parameters};{uri}\x1b\\";

    private static string Close() => "\x1b]8;;\x1b\\";

    private static (TerminalBuffer Buffer, AnsiParser Parser) NewTerminal()
    {
        var buffer = new TerminalBuffer(80, 5);
        return (buffer, new AnsiParser(buffer));
    }

    private static TerminalStateSnapshot SnapshotOf(TerminalBuffer buffer, AnsiParser parser)
    {
        TerminalStateSnapshot snapshot = buffer.ExportState(int.MaxValue);
        snapshot.Parser = parser.ExportState();
        return snapshot;
    }

    private static Hyperlink? LinkAt(TerminalBuffer buffer, int col)
        => buffer.GetHyperlinkIdentityAbsolute(col, buffer.Scrollback.Count);

    /// <summary>
    /// A resumed parser must return the buffer's instance for an id the restored cells already
    /// carry. Minting a second one is not visibly broken - the URI and the id both match - but the
    /// two runs stop underlining together, which is the whole purpose of <c>id</c>.
    /// </summary>
    [Fact]
    public void SeededRegistry_RejoinsAnIdAlreadyOnTheRestoredScreen()
    {
        var (source, sourceParser) = NewTerminal();
        sourceParser.Process(Open("id=alpha", Uri) + "A" + Close());

        var (restored, restoredParser) = NewTerminal();
        TerminalStateTransfer.Restore(restored, restoredParser, SnapshotOf(source, sourceParser));

        // The tail re-references the same anchor.
        restoredParser.Process("\x1b[1;3H" + Open("id=alpha", Uri) + "B" + Close());

        Hyperlink? fromSnapshot = LinkAt(restored, 0);
        Hyperlink? fromTail = LinkAt(restored, 2);
        Assert.NotNull(fromSnapshot);
        Assert.NotNull(fromTail);
        Assert.Same(fromSnapshot, fromTail);
    }

    /// <summary>
    /// Re-attach into a parser that has already seen the same <c>(URI, id)</c> in a previous life.
    /// The instance it is still holding is <em>not</em> in the incoming snapshot's table, so the
    /// seeding must replace it rather than keep it: handing the tail a stale instance no restored
    /// cell carries reproduces exactly the grouping bug the seeding exists to fix. The buffer's
    /// table is authoritative.
    /// </summary>
    [Fact]
    public void SeededRegistry_ReplacesAStaleIdentityFromAnEarlierAttach()
    {
        // A parser with a life of its own: it interns (Uri, alpha) as instance X.
        var (reused, reusedParser) = NewTerminal();
        reusedParser.Process(Open("id=alpha", Uri) + "X" + Close());
        Hyperlink? stale = LinkAt(reused, 0);
        Assert.NotNull(stale);

        // An unrelated session that happens to use the same (URI, id) - a different instance.
        var (source, sourceParser) = NewTerminal();
        sourceParser.Process(Open("id=alpha", Uri) + "A" + Close());

        TerminalStateTransfer.Restore(reused, reusedParser, SnapshotOf(source, sourceParser));
        reusedParser.Process("\x1b[1;3H" + Open("id=alpha", Uri) + "B" + Close());

        Hyperlink? fromSnapshot = LinkAt(reused, 0);
        Hyperlink? fromTail = LinkAt(reused, 2);
        Assert.NotNull(fromSnapshot);
        Assert.NotNull(fromTail);
        Assert.Same(fromSnapshot, fromTail);
        Assert.NotSame(stale, fromTail);
    }

    /// <summary>
    /// A link written without an <c>id</c> stays its own anchor across the restore: a later id-less
    /// OSC 8 to the same URI must not join it.
    /// </summary>
    /// <remarks>
    /// This pins <see cref="HyperlinkRegistry.Resolve"/>'s no-id rule <em>through the restore
    /// path</em>, not the seeding. The guarantee lives in <c>Resolve</c>, which returns a fresh
    /// instance for an id-less OSC 8 before the interning table is ever consulted - so seeding a
    /// no-id link would not actually break this, and the <c>Id</c> check in
    /// <c>SeedInterned</c> is defence in depth rather than the load-bearing guard. The regression
    /// this would catch is someone making <c>Resolve</c> consult the table for id-less links in
    /// order to "fix" a future parity failure, which would silently merge two distinct links that
    /// happen to share a target - the exact case <c>id</c> exists to disambiguate.
    /// </remarks>
    [Fact]
    public void SeededRegistry_LeavesIdLessLinksDistinct()
    {
        var (source, sourceParser) = NewTerminal();
        sourceParser.Process(Open(string.Empty, Uri) + "A" + Close());

        var (restored, restoredParser) = NewTerminal();
        TerminalStateTransfer.Restore(restored, restoredParser, SnapshotOf(source, sourceParser));

        restoredParser.Process("\x1b[1;3H" + Open(string.Empty, Uri) + "B" + Close());

        Hyperlink? fromSnapshot = LinkAt(restored, 0);
        Hyperlink? fromTail = LinkAt(restored, 2);
        Assert.NotNull(fromSnapshot);
        Assert.NotNull(fromTail);
        Assert.Equal(fromSnapshot!.Uri, fromTail!.Uri);
        Assert.NotSame(fromSnapshot, fromTail);
    }
}
