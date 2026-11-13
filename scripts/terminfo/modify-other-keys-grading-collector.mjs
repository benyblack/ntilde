// Explicitly versioned collector extension for the modifyOtherKeys qualification (input.modify-other-keys, levels 1 and 2).
// This is a digest-checked derivation layered on top of the left/right-margin grading extension, a sibling of the OSC 133,
// routed-input, runtime-color reset, OSC 7/8 and title/icon stack layers: the pinned bundle, the owned adapter and every
// earlier grading extension stay byte-for-byte unchanged, and each graded result carries this extension's identity so it
// can never be presented as stock output.
//
// The extension adds no probe of its own. It exposes the PINNED termless probe definition so its capability read can be
// served from the child's own XTGETTCAP declaration capture; that probe always returns inconclusive (absentEventNote),
// and the derivation refuses unless the retained left/right-margin extension still contains the exact pinned
// input.modify-other-keys registration, verbatim and once.
import { modifyOtherKeysCaseIds } from './modify-other-keys-contracts.mjs';

/** Identity of this layered extension. */
export const modifyOtherKeysAdapterId = 'ntilde-owned-modify-other-keys-grading-v1';

/** Exact pinned seams from terminfo.dev@4.0.0 (as retained by the left/right-margin extension), then its retained exports. */
const pinnedModifyOtherKeysSeams = Object.freeze([
    // input.modify-other-keys registration
    "    probe(\"input.modify-other-keys\", (ctx) => ({\n      pass: false,\n      response: JSON.stringify({ declaredModifyOtherKeys: ctx.capabilities.extensions.has(\"modifyOtherKeys\") }),\n      note: absentEventNote,\n      observation: {\n        outcome: \"inconclusive\",\n        reason: \"insufficient-evidence\",\n        evidence: \"legacy\",\n        note: absentEventNote\n      }\n    }), () => Promise.resolve({\n      pass: false,\n      note: absentEventNote,\n      observation: {\n        outcome: \"inconclusive\",\n        reason: \"insufficient-evidence\",\n        evidence: \"none\",\n        note: absentEventNote\n      }\n    })),\n",
    // the retained previous layer's exports
    "export const ntildeLrMarginsAdapter = Object.freeze({",
    "export function ntildeRunLrMarginsTermless(probeId, ctx) {"
]);

/** The pinned seams, exposed so portable tests can build a synthetic layered source. */
export const modifyOtherKeysSeams = pinnedModifyOtherKeysSeams;

/** Derive the modifyOtherKeys grading extension from the left/right-margin grading extension. */
export function adaptModifyOtherKeysCollector(lrMarginsSource) {
    const source = String(lrMarginsSource);
    for (const seam of pinnedModifyOtherKeysSeams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned modifyOtherKeys probe seam changed; refusing derivation.');
    }
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local modifyOtherKeys grading extension ${JSON.stringify(modifyOtherKeysAdapterId)}.
// Derived from the left/right-margin grading extension; probe definitions, policy
// gates and stock grading remain intact.
export const ntildeModifyOtherKeysAdapter = Object.freeze({
  id: ${JSON.stringify(modifyOtherKeysAdapterId)},
  probeIds: ${JSON.stringify([...modifyOtherKeysCaseIds])}
});
/** Execute the pinned termless probe over caller-served declaration metadata. */
export function ntildeRunModifyOtherKeysTermless(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.termless !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = probe.termless(ctx);
  return { probeId, ...result };
}
`;
}
