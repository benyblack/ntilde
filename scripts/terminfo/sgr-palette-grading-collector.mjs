// Explicitly versioned collector extension for the SGR palette-color
// qualification (bright, 256-color and default foreground/background). This is a
// sixth digest-checked derivation layered on top of the SGR-attributes grading
// extension: the pinned bundle, the owned adapter and every earlier grading
// extension stay byte-for-byte unchanged, and each graded result carries this
// extension's identity so it can never be presented as stock output.
//
// The extension adds no probe of its own. It exposes the PINNED termless
// namedColorProbe, defaultColorProbe and indexedColorProbe definitions for the six
// contracted cases so live, independently observed cells can be graded by the
// collector's own criteria. Derivation refuses unless the retained
// SGR-attributes extension still contains the exact pinned probe definitions and
// the exact pinned termless bodies and notes the contracts mirror.
import { sgrPaletteCaseIds } from './sgr-palette-contracts.mjs';

/** Identity of this sixth layered extension. */
export const sgrPaletteAdapterId = 'ntilde-owned-sgr-palette-grading-v1';

/** Exact pinned seams from terminfo.dev@4.0.0 (as retained by the SGR-attributes
 * extension). Each must appear exactly once. */
const pinnedPaletteSeams = Object.freeze([
    'namedColorProbe("sgr.fg.bright", "fg", 91, 94),',
    'namedColorProbe("sgr.bg.bright", "bg", 101, 104),',
    'defaultColorProbe("sgr.fg.default", "fg"),',
    'defaultColorProbe("sgr.bg.default", "bg"),',
    'indexedColorProbe("sgr.fg.256", "fg"),',
    'indexedColorProbe("sgr.bg.256", "bg"),',
    'function namedColorProbe(id, channel, firstCode, secondCode) {',
    'function defaultColorProbe(id, channel) {',
    'function indexedColorProbe(id, channel) {',
    'function sameCellColor(a, b) {',
    'function sameRgb(a, b) {',
    'function parserStateResult(pass, expected, state, note) {',
    'outcome: pass === null ? "inconclusive" : pass ? "supported" : "unsupported",',
    // named (bright) body and notes
    '      ctx.feed(`C${first}X${second}Y`);',
    '`${id}: the two named colors select distinct exposed ${channel} RGB values`',
    'if (neutral !== undefined && a && b && !sameCellColor(neutral, a) && !sameRgb(a, b)) {',
    '"Neutral control or named-color cells were not exposed"',
    '"Baseline, theme colors, or color readback were indistinguishable"',
    // default body and notes
    'const setup = channel === "fg" ? 31 : 42;',
    'const reset = channel === "fg" ? 39 : 49;',
    '      ctx.feed(`C\\x1B[${setup}mX\\x1B[${reset}mR`);',
    '`${id}: reset R restores the measured C ${channel} after a distinct colored X`',
    'if (!changed || sameCellColor(baseColor, changed) || resetColor === undefined) {',
    'return parserStateResult(sameCellColor(baseColor, resetColor), expected, state);',
    '"Baseline, colored, or reset cell was not exposed"',
    '"Distinct colored setup or reset color readback was absent"',
    // indexed body and notes
    'const first = { r: 95, g: 135, b: 175 };',
    'const second = { r: 215, g: 135, b: 95 };',
    '      ctx.feed(`C\\x1B[${sgr};2;95;135;175mA\\x1B[${sgr};2;215;135;95mB\\x1B[${sgr};5;67mX\\x1B[${sgr};5;173mY`);',
    '`${id}: indices 67 and 173 match two independently observed canonical RGB cube references`',
    'if (a && b && x && y && sameRgb(a, first) && sameRgb(b, second) && sameRgb(x, a) && sameRgb(y, b)) {',
    '"Indexed or direct-RGB control cells were not exposed"',
    '"RGB reference was not calibrated, or the backend may use a custom indexed palette"',
    // the retained previous layer's exports
    'export const ntildeSgrAttrsAdapter = Object.freeze({',
    'export function ntildeRunSgrAttrsTermless(probeId, ctx) {',
]);

/** The pinned seams, exposed so portable tests can build a synthetic layered source. */
export const sgrPaletteSeams = pinnedPaletteSeams;

/** Derive the SGR-palette grading extension from the SGR-attributes grading extension. */
export function adaptSgrPaletteCollector(sgrAttrsSource) {
    const source = String(sgrAttrsSource);
    for (const seam of pinnedPaletteSeams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned SGR-palette probe seam changed; refusing derivation.');
    }
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local SGR-palette grading extension ${JSON.stringify(sgrPaletteAdapterId)}.
// Derived from the SGR-attributes grading extension; probe definitions, policy
// gates and stock grading remain intact.
export const ntildeSgrPaletteAdapter = Object.freeze({
  id: ${JSON.stringify(sgrPaletteAdapterId)},
  probeIds: ${JSON.stringify([...sgrPaletteCaseIds])}
});
/** Execute one pinned termless probe over caller-served cells. */
export function ntildeRunSgrPaletteTermless(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.termless !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = probe.termless(ctx);
  return { probeId, ...result };
}
`;
}
