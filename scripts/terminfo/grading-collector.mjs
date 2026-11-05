// Explicitly versioned collector extension for the ASCII/SGR qualification
// batch. This is a second digest-checked derivation on top of the existing
// owned adapter (scripts/terminfo/collector.mjs): the upstream bundle and the
// owned adapter stay byte-for-byte unchanged, and every graded result carries
// this extension's identity so it can never be presented as stock output.
//
// The extension adds no probe of its own. It exposes the PINNED termless
// probes for the four contracted cases so a live, independently observed
// screen can be graded by the collector's own criteria. Derivation refuses
// unless the pinned bundle still contains the exact four probe definitions.
import { asciiSgrCaseIds } from './ascii-sgr-contracts.mjs';

export const gradingAdapterId = 'ntilde-owned-ascii-sgr-grading-v1';

/** Exact pinned probe-definition seams from terminfo.dev@4.0.0. Each must
 * appear exactly once; derivation binds the contracts to these bytes. */
const pinnedSeams = Object.freeze([
    'probe("text.basic", (ctx) => {\n        ctx.feed("\\x1B[2J\\x1B[H");',
    '"Hello appears after a measured empty control"',
    'probe("text.overwrite", (ctx) => {\n        ctx.feed("AB");',
    '"C overwrites A while B remains"',
    'sgrProbe("sgr.bold", "\\x1B[1m", (cell) => cell.bold === true)',
    'sgrProbe("sgr.italic", "\\x1B[3m", (cell) => cell.italic === true)',
    'var init_src = __esm(() => {',
]);

const ownedAdapterSeam = 'export async function collectNtildeRun(assertion) {';

/** Derive the grading extension from the existing owned-adapter source. */
export function adaptGradingCollector(ownedAdapterSource) {
    let source = String(ownedAdapterSource);
    for (const seam of pinnedSeams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned ASCII/SGR probe seam changed; refusing derivation.');
    }
    if (source.split(ownedAdapterSeam).length !== 2) throw new Error('Expected the existing owned collector adapter.');
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local grading extension ${JSON.stringify(gradingAdapterId)}. Derived from the
// owned adapter; probe definitions, policy gates and stock grading are intact.
export const ntildeGradingAdapter = Object.freeze({
  id: ${JSON.stringify(gradingAdapterId)},
  probeIds: ${JSON.stringify([...asciiSgrCaseIds])}
});
export function ntildeRunTermless(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.termless !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = probe.termless(ctx);
  return { probeId, ...result };
}
`;
}
