// Explicitly versioned collector extension for the SGR attribute qualification
// (underline, inverse, faint, hidden, strikethrough). This is a fifth
// digest-checked derivation layered on top of the SGR-RGB grading extension:
// the pinned bundle, the owned adapter and every earlier grading extension stay
// byte-for-byte unchanged, and each graded result carries this extension's
// identity so it can never be presented as stock output.
//
// The extension adds no probe of its own. It exposes the PINNED termless
// sgrProbe definitions for the five contracted cases so live, independently
// observed cells can be graded by the collector's own criteria. Derivation
// refuses unless the retained SGR-RGB extension still contains the exact pinned
// probe definitions and the exact pinned sgrProbe termless body.
import { sgrAttrsCaseIds } from './sgr-attrs-contracts.mjs';

/** Identity of this fifth layered extension. */
export const sgrAttrsAdapterId = 'ntilde-owned-sgr-attributes-grading-v1';

/** Exact pinned seams from terminfo.dev@4.0.0 (as retained by the SGR-RGB
 * extension). Each must appear exactly once. */
const pinnedAttributeSeams = Object.freeze([
    String.raw`sgrProbe("sgr.faint", "\x1B[2m", (cell) => cell.dim === true)`,
    String.raw`sgrProbe("sgr.underline.single", "\x1B[4m", (cell) => !!cell.underline)`,
    String.raw`sgrProbe("sgr.inverse", "\x1B[7m", (cell) => cell.inverse === true)`,
    String.raw`sgrProbe("sgr.hidden", "\x1B[8m", (cell) => cell.hidden === true)`,
    String.raw`sgrProbe("sgr.strikethrough", "\x1B[9m", (cell) => cell.strikethrough === true)`,
    'function sgrProbe(id, sequence, check, noObservableWhen) {',
    '      ctx.feed(sequence + "X");\n      const cell = ctx.getCell(0, 0);',
    'const measured = cell.char === "X" && check(cell) === true;',
    '`${id}: attribute applied to the rendered X cell`',
    '"No rendered X cell to evaluate"',
    'Cell readback cannot distinguish unsupported styling from unreported attributes or collapsed styles; a negative needs rendering review',
    'export const ntildeSgrRgbAdapter = Object.freeze({',
    'export function ntildeRunSgrRgbTermless(probeId, ctx) {',
]);

/** Derive the SGR-attributes grading extension from the SGR-RGB grading extension. */
export function adaptSgrAttrsCollector(sgrRgbSource) {
    const source = String(sgrRgbSource);
    for (const seam of pinnedAttributeSeams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned SGR-attributes probe seam changed; refusing derivation.');
    }
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local SGR-attributes grading extension ${JSON.stringify(sgrAttrsAdapterId)}.
// Derived from the SGR-RGB grading extension; probe definitions, policy gates
// and stock grading remain intact.
export const ntildeSgrAttrsAdapter = Object.freeze({
  id: ${JSON.stringify(sgrAttrsAdapterId)},
  probeIds: ${JSON.stringify([...sgrAttrsCaseIds])}
});
/** Execute one pinned termless probe over caller-served cells. */
export function ntildeRunSgrAttrsTermless(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.termless !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = probe.termless(ctx);
  return { probeId, ...result };
}
`;
}
