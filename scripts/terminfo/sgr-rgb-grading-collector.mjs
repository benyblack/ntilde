// Explicitly versioned collector extension for the SGR standard-color RGB
// qualification. This is a fourth digest-checked derivation layered on top of
// the SGR-colors grading extension: the pinned bundle, the owned adapter and
// every earlier grading extension stay byte-for-byte unchanged, and each graded
// result carries this extension's identity so it can never be presented as
// stock output.
//
// The extension adds no probe of its own. It exposes the PINNED termless
// namedColorProbe for the two standard-color cases so live, independently
// observed cells — projected with the App-resolved per-cell RGB — can be graded
// by the collector's own criteria. Derivation refuses unless the retained
// SGR-colors extension still contains the exact pinned probe definitions.
import { sgrRgbCaseIds } from './sgr-rgb-contracts.mjs';

export const sgrRgbAdapterId = 'ntilde-owned-sgr-rgb-grading-v1';

/** Exact pinned probe-definition seams from terminfo.dev@4.0.0 (as retained by
 * the SGR-colors extension). Each must appear exactly once. */
const pinnedRgbSeams = Object.freeze([
    'namedColorProbe("sgr.fg.standard", "fg", 31, 34)',
    'namedColorProbe("sgr.bg.standard", "bg", 41, 44)',
    'function namedColorProbe(id, channel, firstCode, secondCode) {',
    'the two named colors select distinct exposed ',
    'export const ntildeSgrColorsAdapter = Object.freeze({',
    'export function ntildeRunSgrColorsTermless(probeId, ctx) {',
]);

/** Derive the SGR-RGB grading extension from the SGR-colors grading extension. */
export function adaptSgrRgbCollector(sgrColorsSource) {
    let source = String(sgrColorsSource);
    for (const seam of pinnedRgbSeams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned SGR-RGB probe seam changed; refusing derivation.');
    }
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local SGR-RGB grading extension ${JSON.stringify(sgrRgbAdapterId)}.
// Derived from the SGR-colors grading extension; probe definitions, policy
// gates and stock grading remain intact.
export const ntildeSgrRgbAdapter = Object.freeze({
  id: ${JSON.stringify(sgrRgbAdapterId)},
  probeIds: ${JSON.stringify([...sgrRgbCaseIds])}
});
export function ntildeRunSgrRgbTermless(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.termless !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = probe.termless(ctx);
  return { probeId, ...result };
}
`;
}
