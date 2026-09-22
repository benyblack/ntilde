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
    /// </remarks>
    public static class TerminalStateTransfer
    {
        /// <summary>
        /// Restores <paramref name="snapshot"/> into <paramref name="buffer"/> and
        /// <paramref name="parser"/>, and re-seeds the parser's hyperlink registry from the link
        /// table the buffer rebuilt.
        /// </summary>
        /// <param name="buffer">
        /// The buffer to restore into. It must already be the snapshot's size - neither this method
        /// nor <see cref="TerminalBuffer.ImportState"/> resizes, and a mismatch degrades silently
        /// rather than throwing. Construct it at <see cref="TerminalStateSnapshot.Cols"/> x
        /// <see cref="TerminalStateSnapshot.Rows"/>, or resize it first.
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
