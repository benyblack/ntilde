using Ntilde.VT;
using Ntilde.VT.Links;
using Xunit;

namespace Ntilde.VT.Tests.StateTransfer;

/// <summary>
/// The parser's OSC 8 interning registry is deliberately absent from <see cref="AnsiParserState"/> -
/// link identity has a single source, the buffer's link table - so a restored parser has to be
/// re-seeded from that table instead. These pin both halves of that rule.
/// </summary>
/// <remarks>
/// The parity suite caught the missing seeding (osc8-hyperlinks, cut 36) but cannot catch seeding
/// too <em>much</em>: no corpus stream has two separate id-less OSC 8 runs sharing a URI on
/// opposite sides of a cut, so wrongly interning a no-id link would go unnoticed there while
/// silently merging two links that <c>id</c> exists to keep apart.
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

        TerminalStateSnapshot snapshot = source.ExportState(int.MaxValue);
        snapshot.Parser = sourceParser.ExportState();

        var (restored, restoredParser) = NewTerminal();
        System.Collections.Generic.IReadOnlyList<Hyperlink> links = restored.ImportState(snapshot);
        restoredParser.ImportState(snapshot.Parser);
        restoredParser.SeedHyperlinkRegistry(links);

        // The tail re-references the same anchor.
        restoredParser.Process("\x1b[1;3H" + Open("id=alpha", Uri) + "B" + Close());

        Hyperlink? fromSnapshot = LinkAt(restored, 0);
        Hyperlink? fromTail = LinkAt(restored, 2);
        Assert.NotNull(fromSnapshot);
        Assert.NotNull(fromTail);
        Assert.Same(fromSnapshot, fromTail);
    }

    /// <summary>
    /// The mirror constraint: a link written without an <c>id</c> must not be seeded, so a later
    /// id-less OSC 8 to the same URI stays a separate anchor. Interning it would merge two links
    /// that merely share a target - the exact case the spec's <c>id</c> exists to disambiguate, and
    /// the reason <see cref="HyperlinkRegistry.Resolve"/> mints a fresh instance every time.
    /// </summary>
    [Fact]
    public void SeededRegistry_LeavesIdLessLinksDistinct()
    {
        var (source, sourceParser) = NewTerminal();
        sourceParser.Process(Open(string.Empty, Uri) + "A" + Close());

        TerminalStateSnapshot snapshot = source.ExportState(int.MaxValue);
        snapshot.Parser = sourceParser.ExportState();

        var (restored, restoredParser) = NewTerminal();
        System.Collections.Generic.IReadOnlyList<Hyperlink> links = restored.ImportState(snapshot);
        restoredParser.ImportState(snapshot.Parser);
        restoredParser.SeedHyperlinkRegistry(links);

        restoredParser.Process("\x1b[1;3H" + Open(string.Empty, Uri) + "B" + Close());

        Hyperlink? fromSnapshot = LinkAt(restored, 0);
        Hyperlink? fromTail = LinkAt(restored, 2);
        Assert.NotNull(fromSnapshot);
        Assert.NotNull(fromTail);
        Assert.Equal(fromSnapshot!.Uri, fromTail!.Uri);
        Assert.NotSame(fromSnapshot, fromTail);
    }
}
