// Explicitly versioned collector extension for the SGR pixel-coordinate mouse qualification (input.pixel-mouse, ?1016).
// This is a digest-checked derivation layered on top of the left/right-margin grading extension, a sibling of the routed-input, modifyOtherKeys,
// extended-mouse and other layers: the pinned bundle, the owned adapter and every earlier grading extension stay byte-for-byte unchanged, and
// each graded result carries this extension's identity so it can never be presented as stock output.
//
// The extension adds no probe of its own. It exposes the PINNED termless probe definition so its one read can be served from the child's own
// DECRQM reply: input.pixel-mouse is mouseInputProbe("input.pixel-mouse", "pixelMouse"), which reads ctx.getMode("pixelMouse") and is always
// inconclusive (nothing validated, nothing fed). The derivation refuses unless the retained left/right-margin extension still contains the exact
// pinned registration, helper and absent-event note, each verbatim and once. The stock terminal path (inconclusive without touching the terminal)
// is untouched by this layer.
import { pxmCaseIds } from './pxm-contracts.mjs';

/** Identity of this layered extension. */
export const pxmAdapterId = 'ntilde-owned-pixel-mouse-grading-v1';

/** Exact pinned seams from terminfo.dev@4.0.0 (as retained by the left/right-margin extension), then its retained exports. */
const pinnedPxmSeams = Object.freeze([
    // mouseInputProbe helper
    "function mouseInputProbe(id, modeName) {\n  return probe(id, (ctx) => ({\n    pass: false,\n    response: JSON.stringify({ currentMode: ctx.getMode(modeName) === true }),\n    note: absentEventNote,\n    observation: {\n      outcome: \"inconclusive\",\n      reason: \"insufficient-evidence\",\n      evidence: \"legacy\",\n      note: absentEventNote\n    }\n  }), () => Promise.resolve({\n    pass: false,\n    note: absentEventNote,\n    observation: {\n      outcome: \"inconclusive\",\n      reason: \"insufficient-evidence\",\n      evidence: \"none\",\n      note: absentEventNote\n    }\n  }));\n}\n",
    // input.pixel-mouse registration
    "    mouseInputProbe(\"input.pixel-mouse\", \"pixelMouse\"),",
    // absentEventNote declaration
    "var absentEventNote = \"No generated input event and encoded report were validated\", inputProbes;",
    // the retained previous layer's exports
    'export const ntildeLrMarginsAdapter = Object.freeze({',
    'export function ntildeRunLrMarginsTermless(probeId, ctx) {',
]);

/** The pinned seams, exposed so portable tests can build a synthetic layered source. */
export const pxmSeams = pinnedPxmSeams;

/** Derive the SGR-pixel grading extension from the left/right-margin grading extension. */
export function adaptPxmCollector(lrMarginsSource) {
    const source = String(lrMarginsSource);
    for (const seam of pinnedPxmSeams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned SGR-pixel probe seam changed; refusing derivation.');
    }
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local SGR-pixel grading extension ${JSON.stringify(pxmAdapterId)}.
// Derived from the left/right-margin grading extension; probe definitions, policy
// gates and stock grading remain intact.
export const ntildePxmAdapter = Object.freeze({
  id: ${JSON.stringify(pxmAdapterId)},
  probeIds: ${JSON.stringify([...pxmCaseIds])}
});
/** Execute one pinned termless probe over a caller-served DECRQM reply and mode metadata. */
export function ntildeRunPxmTermless(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.termless !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = probe.termless(ctx);
  return { probeId, ...result };
}
`;
}
