// Explicitly versioned collector extension for the SGR reset/standard-color
// qualification batch. This is a third digest-checked derivation layered on
// top of the ASCII/SGR grading extension: the pinned bundle, the owned
// adapter and the first grading extension stay byte-for-byte unchanged, and
// every graded result carries this extension's identity so it can never be
// presented as stock output.
//
// The extension adds no probe of its own. It exposes the PINNED termless
// probes for the five contracted cases so live, independently observed cells
// can be graded by the collector's own criteria. Derivation refuses unless
// the pinned bundle still contains the exact five probe definitions.
import { sgrColorsCaseIds } from './sgr-colors-contracts.mjs';

export const sgrColorsAdapterId = 'ntilde-owned-sgr-colors-grading-v1';

/** Exact pinned probe-definition seams from terminfo.dev@4.0.0. Each must
 * appear exactly once; derivation binds the contracts to these bytes. */
const pinnedColorSeams = Object.freeze([
    'namedColorProbe("sgr.fg.standard", "fg", 31, 34)',
    'namedColorProbe("sgr.bg.standard", "bg", 41, 44)',
    'resetProbe("sgr.selective-reset.bold", "\\x1B[1;2;3m", "\\x1B[22m", ["bold", "dim"], ["italic"])',
    'resetProbe("sgr.selective-reset.italic", "\\x1B[1;3m", "\\x1B[23m", ["italic"], ["bold"])',
    'resetProbe("sgr.reset", "\\x1B[1;3;4m", "\\x1B[0m", ["bold", "italic", "underline"])',
    'function measuredReset(ctx, id, setup, reset, set, preserve) {',
    'function namedColorProbe(id, channel, firstCode, secondCode) {',
    'the two named colors select distinct exposed ',
    'measured setup attributes clear on Y and unrelated attributes remain set',
]);

const asciiSgrSeam = 'export const ntildeGradingAdapter = Object.freeze({';

/** Derive the SGR-colors grading extension from the first grading extension. */
export function adaptSgrColorsCollector(asciiSgrSource) {
    let source = String(asciiSgrSource);
    for (const seam of pinnedColorSeams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned SGR-colors probe seam changed; refusing derivation.');
    }
    if (source.split(asciiSgrSeam).length !== 2) throw new Error('Expected the first ASCII/SGR grading extension.');
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local SGR-colors grading extension ${JSON.stringify(sgrColorsAdapterId)}.
// Derived from the ASCII/SGR grading extension; probe definitions, policy
// gates and stock grading remain intact.
export const ntildeSgrColorsAdapter = Object.freeze({
  id: ${JSON.stringify(sgrColorsAdapterId)},
  probeIds: ${JSON.stringify([...sgrColorsCaseIds])}
});
export function ntildeRunSgrColorsTermless(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.termless !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = probe.termless(ctx);
  return { probeId, ...result };
}
`;
}
