using System;
using System.Collections.Generic;
using Ntilde.VT.Links;

namespace Ntilde.VT
{
    /// <summary>
    /// The one supported way to restore a terminal from a <see cref="TerminalStateSnapshot"/>:
    /// buffer, parser and OSC 8 link identity, in the order they depend on each other.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The three steps are individually public and remain so - this is a convenience over them, not
    /// a replacement. It exists because the third step fails <em>silently</em> when forgotten:
    /// <see cref="AnsiParser.SeedHyperlinkRegistry"/> is what makes an <c>OSC 8</c> in the resumed
    /// stream resolve a re-referenced <c>id=</c> to the instance the restored cells already carry.
    /// Skip it and nothing throws; hyperlink grouping just quietly stops working for anchors that
    /// straddle the attach point. A three-step protocol whose last step is optional-looking and
    /// silent is a trap with documentation beside it, so the ordering gets an owner.
    /// </para>
    /// <para>
    /// Order is not incidental. The buffer import is what mints the <see cref="Hyperlink"/>
    /// identities; the parser can only be seeded with instances that already exist.
    /// </para>
    /// <para>
    /// It owns the geometry precondition for the same reason it owns the seeding: a step that is
    /// easy to forget and silent when forgotten needs an owner, and importing into a
    /// wrong-sized buffer is the more damaging of the two - it produces a visibly wrong screen
    /// rather than a subtly wrong one.
    /// </para>
    /// <para>
    /// <see cref="Restore"/> validates the whole envelope - the buffer portion and, when present,
    /// the parser portion - before it resizes or imports anything. "Whole" is the operative word:
    /// it runs <see cref="TerminalBuffer.ValidateBufferState"/> and
    /// <see cref="AnsiParser.ValidateParserState"/>, the same routines the two
    /// <c>ImportState</c> methods run on their own input, rather than a hoisted selection of the
    /// checks inside them. Hoisting a selection is what this used to do, and it left the
    /// remainder - the base64 cell blobs, the synchronized-output timestamp, and every required
    /// nested object - to fail after the resize had already reflowed the destination. An invalid
    /// snapshot is now refused with the destination buffer completely untouched: unresized,
    /// unreflowed, and with its prior content intact. Both <c>ImportState</c> methods keep
    /// validating their own input too - they are public and independently callable, so this
    /// hoisting does not relieve them of it.
    /// </para>
    /// </remarks>
    public static class TerminalStateTransfer
    {
        /// <summary>
        /// Restores <paramref name="snapshot"/> into <paramref name="buffer"/> and
        /// <paramref name="parser"/>, and re-seeds the parser's hyperlink registry from the link
        /// table the buffer rebuilt.
        /// </summary>
        /// <param name="buffer">
        /// The buffer to restore into, at any size. It is resized to
        /// <see cref="TerminalStateSnapshot.Cols"/> x <see cref="TerminalStateSnapshot.Rows"/>
        /// first when it does not already match, because
        /// <see cref="TerminalBuffer.ImportState"/> does not resize and a geometry mismatch there
        /// degrades <em>silently</em>: the cells are cropped and padded to the destination's
        /// shape, and the extended-text and link entries are then applied at coordinates from the
        /// other geometry, which puts a grapheme or an OSC 8 underline on the wrong cell. The
        /// resize is free in the case that matters - a freshly constructed buffer is empty, so
        /// there is nothing to reflow - and the import overwrites every row immediately after.
        /// </param>
        /// <param name="parser">
        /// The parser driving <paramref name="buffer"/>. It must have been constructed with the
        /// same <c>forceConPtyFiltering</c> as the exporter; the two halves of a snapshot are only
        /// meaningful under the same filtering.
        /// </param>
        /// <param name="snapshot">
        /// The state to adopt. <see cref="TerminalStateSnapshot.Parser"/> may be <c>null</c>, in
        /// which case the parser's position is left alone and only the buffer is restored - a
        /// buffer-only payload, not a reason to fail.
        /// </param>
        /// <returns>
        /// The rebuilt <see cref="Hyperlink"/> identity table, in snapshot order, for a caller that
        /// wants to inspect it. Ignoring it is normal: the seeding has already happened.
        /// </returns>
        public static IReadOnlyList<Hyperlink> Restore(
            TerminalBuffer buffer, AnsiParser parser, TerminalStateSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentNullException.ThrowIfNull(parser);
            ArgumentNullException.ThrowIfNull(snapshot);

            // Validate the whole envelope before anything is resized or imported: a caller that
            // catches the exception below gets the destination exactly as it was, not reflowed
            // partway through. These are the very routines TerminalBuffer.ImportState and
            // AnsiParser.ImportState run on their own input - not a hoisted subset of them, which
            // is what this used to be and why a bad cell blob, an unrepresentable synchronized
            // output timestamp or a null mode object still got the destination resized first.
            // Both ImportState methods still call them - deliberate belt-and-braces, since both
            // are public and independently callable, not redundancy to optimise away.
            TerminalBuffer.ValidateBufferState(snapshot);
            if (snapshot.Parser is not null)
            {
                parser.ValidateParserState(snapshot.Parser);
            }

            // Before anything else, and before ImportState's own validation runs: a caller that
            // got this wrong gets a correct screen rather than a subtly wrong one. Resize ignores
            // non-positive dimensions, which is why the geometry is *also* structure that
            // ImportState refuses outright - a 0-column snapshot would otherwise leave the buffer
            // at its old size and import into it.
            if (snapshot.Cols != buffer.Cols || snapshot.Rows != buffer.Rows)
            {
                buffer.Resize(snapshot.Cols, snapshot.Rows);
            }

            IReadOnlyList<Hyperlink> links = buffer.ImportState(snapshot);

            if (snapshot.Parser is not null)
            {
                parser.ImportState(snapshot.Parser);
            }

            parser.SeedHyperlinkRegistry(links);
            return links;
        }
    }
}
