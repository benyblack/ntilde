// Explicitly versioned collector extension for the ASCII reflow qualification (extensions.reflow). This is a
// digest-checked derivation layered on top of the OSC-title grading extension: the pinned bundle, the owned
// adapter and every earlier grading extension stay byte-for-byte unchanged, and each graded result carries this
// extension's identity so it can never be presented as stock output.
//
// The extension adds no probe of its own. It exposes the PINNED extensions.reflow probe definition (both arms, which
// never touch a terminal and always return inconclusive) so it can be replayed over the journaled declared capability;
// the derivation refuses unless the retained OSC-title extension still contains the exact pinned registration and the
// parserStateResult helper body, each verbatim and once.
import { asciiReflowCaseIds } from './ascii-reflow-contracts.mjs';

/** Identity of this layered extension. */
export const asciiReflowAdapterId = 'ntilde-owned-ascii-reflow-grading-v1';

/** Exact pinned seams from terminfo.dev@4.0.0 (as retained by the OSC-title extension). Each must appear exactly once:
 * the extensions.reflow registration (termless and terminal functions, both always inconclusive); parserStateResult;
 * then the retained previous layer's exports. */
const pinnedReflowSeam = Object.freeze('    probe("extensions.reflow", (ctx) => {\n      const note = "No controlled resize and cell-content readback to measure reflow";\n      return {\n        pass: false,\n        response: JSON.stringify({ declaredReflow: ctx.capabilities.reflow === true }),\n        note,\n        observation: { outcome: "inconclusive", reason: "insufficient-evidence", evidence: "legacy", note }\n      };\n    }, () => {\n      const note = "No controlled resize and cell-content readback to measure reflow";\n      return Promise.resolve({\n        pass: false,\n        note,\n        observation: { outcome: "inconclusive", reason: "insufficient-evidence", evidence: "none", note }\n      });\n    }),\n');
const parserStateResultSeam = Object.freeze('function parserStateResult(pass, expected, state, note) {\n  const response = JSON.stringify(state);\n  return {\n    pass: pass === true,\n    response,\n    ...note && { note },\n    observation: {\n      outcome: pass === null ? "inconclusive" : pass ? "supported" : "unsupported",\n      ...pass === null && { reason: "insufficient-evidence" },\n      evidence: "parser-state",\n      ...note && { note }\n    },\n    ...pass !== null && {\n      assertions: [{ kind: pass ? "positive" : "negative", expected, observed: response }]\n    }\n  };\n}');
const predecessorSeams = Object.freeze([
    'export const ntildeOscTitleAdapter = Object.freeze({',
    'export function ntildeRunOscTitleTermless(probeId, ctx) {',
]);

/** The pinned seams, exposed so portable tests can build a synthetic layered source. */
export const asciiReflowSeams = Object.freeze([pinnedReflowSeam, parserStateResultSeam, ...predecessorSeams]);

/** Derive the ASCII reflow grading extension from the OSC-title grading extension. */
export function adaptReflowCollector(oscTitleSource) {
    const source = String(oscTitleSource);
    for (const seam of asciiReflowSeams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned extensions.reflow probe seam changed; refusing derivation.');
    }
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local ASCII reflow grading extension ${JSON.stringify(asciiReflowAdapterId)}.
// Derived from the OSC-title grading extension; probe definitions, policy
// gates and stock grading remain intact.
export const ntildeReflowAdapter = Object.freeze({
  id: ${JSON.stringify(asciiReflowAdapterId)},
  probeIds: ${JSON.stringify([...asciiReflowCaseIds])}
});
/** Execute one pinned termless probe over a caller-served declared capability. */
export function ntildeRunReflowTermless(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.termless !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = probe.termless(ctx);
  return { probeId, ...result };
}
`;
}
