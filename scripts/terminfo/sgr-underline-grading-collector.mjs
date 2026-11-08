// Explicitly versioned collector extension for the SGR underline-style
// qualification (double, curly, dotted, dashed). This is an eighth digest-checked
// derivation layered on top of the SGR-truecolor grading extension: the pinned
// bundle, the owned adapter and every earlier grading extension stay
// byte-for-byte unchanged, and each graded result carries this extension's
// identity so it can never be presented as stock output.
//
// The extension adds no probe of its own. It exposes the PINNED termless sgrProbe
// definitions for the four contracted cases so live, independently observed cells
// can be graded by the collector's own criteria. Derivation refuses unless the
// retained SGR-truecolor extension still contains the exact pinned probe
// definitions (spelling and exact-style check) and the exact pinned sgrProbe
// termless body and notes the contracts mirror.
import { sgrUnderlineCaseIds } from './sgr-underline-contracts.mjs';

/** Identity of this eighth layered extension. */
export const sgrUnderlineAdapterId = 'ntilde-owned-sgr-underline-grading-v1';

/** Exact pinned seams from terminfo.dev@4.0.0 (as retained by the SGR-truecolor
 * extension). Each must appear exactly once. */
const pinnedUnderlineSeams = Object.freeze([
    String.raw`sgrProbe("sgr.underline.double", "\x1B[21m", (cell) => cell.underline === "double")`,
    String.raw`sgrProbe("sgr.underline.curly", "\x1B[4:3m", (cell) => cell.underline === "curly")`,
    String.raw`sgrProbe("sgr.underline.dotted", "\x1B[4:4m", (cell) => cell.underline === "dotted")`,
    String.raw`sgrProbe("sgr.underline.dashed", "\x1B[4:5m", (cell) => cell.underline === "dashed")`,
    'function sgrProbe(id, sequence, check, noObservableWhen) {',
    '      ctx.feed(sequence + "X");\n      const cell = ctx.getCell(0, 0);',
    'const measured = cell.char === "X" && check(cell) === true;',
    '`${id}: attribute applied to the rendered X cell`',
    '"No rendered X cell to evaluate"',
    'Cell readback cannot distinguish unsupported styling from unreported attributes or collapsed styles; a negative needs rendering review',
    // the retained previous layer's exports
    'export const ntildeSgrTruecolorAdapter = Object.freeze({',
    'export function ntildeRunSgrTruecolorTermless(probeId, ctx) {',
]);

/** The pinned seams, exposed so portable tests can build a synthetic layered source. */
export const sgrUnderlineSeams = pinnedUnderlineSeams;

/** Derive the SGR-underline grading extension from the SGR-truecolor grading extension. */
export function adaptSgrUnderlineCollector(sgrTruecolorSource) {
    const source = String(sgrTruecolorSource);
    for (const seam of pinnedUnderlineSeams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned SGR-underline probe seam changed; refusing derivation.');
    }
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local SGR-underline grading extension ${JSON.stringify(sgrUnderlineAdapterId)}.
// Derived from the SGR-truecolor grading extension; probe definitions, policy
// gates and stock grading remain intact.
export const ntildeSgrUnderlineAdapter = Object.freeze({
  id: ${JSON.stringify(sgrUnderlineAdapterId)},
  probeIds: ${JSON.stringify([...sgrUnderlineCaseIds])}
});
/** Execute one pinned termless probe over caller-served cells. */
export function ntildeRunSgrUnderlineTermless(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.termless !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = probe.termless(ctx);
  return { probeId, ...result };
}
`;
}
