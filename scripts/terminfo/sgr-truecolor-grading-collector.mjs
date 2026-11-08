// Explicitly versioned collector extension for the SGR truecolor
// qualification (foreground and background). This is a
// seventh digest-checked derivation layered on top of the SGR-palette grading
// extension: the pinned bundle, the owned adapter and every earlier grading
// extension stay byte-for-byte unchanged, and each graded result carries this
// extension's identity so it can never be presented as stock output.
//
// The extension adds no probe of its own. It exposes the PINNED termless
// truecolorProbe definition for the two contracted cases so live, independently
// observed cells can be graded by the collector's own criteria. Derivation
// refuses unless the retained SGR-palette extension still contains the exact
// pinned probe definitions and the exact pinned termless bodies and notes the
// contracts mirror.
import { sgrTruecolorCaseIds } from './sgr-truecolor-contracts.mjs';

/** Identity of this seventh layered extension. */
export const sgrTruecolorAdapterId = 'ntilde-owned-sgr-truecolor-grading-v1';

/** Exact pinned seams from terminfo.dev@4.0.0 (as retained by the SGR-palette
 * extension). Each must appear exactly once. */
const pinnedTruecolorSeams = Object.freeze([
    'truecolorProbe("sgr.fg.truecolor", "fg"),',
    'truecolorProbe("sgr.bg.truecolor", "bg"),',
    'function truecolorProbe(id, channel) {',
    'function sameRgb(a, b) {',
    'function parserStateResult(pass, expected, state, note) {',
    'outcome: pass === null ? "inconclusive" : pass ? "supported" : "unsupported",',
    // truecolor body, references and notes
    'const ordinary = channel === "fg" ? [31, 34] : [41, 44];',
    'const first = channel === "fg" ? { r: 255, g: 128, b: 0 } : { r: 0, g: 255, b: 128 };',
    '  const second = { r: 17, g: 97, b: 201 };\n  return {',
    '      ctx.feed(`C\\x1B[${ordinary[0]}mA\\x1B[${ordinary[1]}mB\\x1B[${sgr};2;${first.r};${first.g};${first.b}mX\\x1B[${sgr};2;17;97;201mY`);',
    '`${id}: two distinct direct-RGB samples are exposed at their exact requested values`',
    '"RGB target or independent color-control cells were not exposed"',
    '"RGB target readback was absent"',
    'if (!preceding || sameRgb(preceding, first)) {',
    '"The preceding color can mimic an ignored first RGB request"',
    'if (sameRgb(x, first) && sameRgb(y, second))',
    // One multi-line seam: the unsupported return and the calibration note also occur in other pinned probes.
    'if (a && b && !sameRgb(a, b))\n        return parserStateResult(false, expected, state);\n      return parserStateResult(null, expected, state, "Independent color-channel calibration was absent");',
    // the retained previous layer's exports
    'export const ntildeSgrPaletteAdapter = Object.freeze({',
    'export function ntildeRunSgrPaletteTermless(probeId, ctx) {',
]);

/** The pinned seams, exposed so portable tests can build a synthetic layered source. */
export const sgrTruecolorSeams = pinnedTruecolorSeams;

/** Derive the SGR-truecolor grading extension from the SGR-palette grading extension. */
export function adaptSgrTruecolorCollector(sgrPaletteSource) {
    const source = String(sgrPaletteSource);
    for (const seam of pinnedTruecolorSeams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned SGR-truecolor probe seam changed; refusing derivation.');
    }
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local SGR-truecolor grading extension ${JSON.stringify(sgrTruecolorAdapterId)}.
// Derived from the SGR-palette grading extension; probe definitions, policy
// gates and stock grading remain intact.
export const ntildeSgrTruecolorAdapter = Object.freeze({
  id: ${JSON.stringify(sgrTruecolorAdapterId)},
  probeIds: ${JSON.stringify([...sgrTruecolorCaseIds])}
});
/** Execute one pinned termless probe over caller-served cells. */
export function ntildeRunSgrTruecolorTermless(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.termless !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = probe.termless(ctx);
  return { probeId, ...result };
}
`;
}
