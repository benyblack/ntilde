// Explicitly versioned collector extension for the OSC 22 App cursor-property qualification (extensions.osc22-pointer). A digest-checked derivation layered on the left/right-margin grading
// extension, a sibling of the OSC 5, notification, device-query and OSC 133 layers: the pinned bundle, the owned adapter and every earlier grading extension stay byte-for-byte unchanged, and each
// graded result carries this extension's identity so it can never be presented as stock output.
//
// The extension adds no probe of its own. It exposes the PINNED probe definition so its one path can be replayed over the parent-journaled cursor position: extensions.osc22-pointer has no termless
// function and its terminal path (two writes, a cursor query, unmeasuredCellResult) can only return inconclusive. The derivation refuses unless the retained left/right-margin extension still contains the
// exact registration and the unmeasuredCellResult helper, each verbatim and once.
import { osc22CaseIds } from './osc22-contracts.mjs';

/** Identity of this layered extension. */
export const osc22AdapterId = 'ntilde-owned-osc22-grading-v1';
/** The grading layer this one is derived from. */
export const osc22DerivedFromAdapterId = 'ntilde-owned-left-right-margins-grading-v1';

/** The exact pinned bytes of one retained bundle seam: the extensions.osc22-pointer registration. */
export const seamRegistration = "    probe(\"extensions.osc22-pointer\", null, async (ctx) => {\n      ctx.write(\"\\x1B[1;1H\\x1B[2K\");\n      ctx.write(\"\\x1B]22;pointer\\x07\");\n      const pos = await ctx.queryCursorPosition();\n      return unmeasuredCellResult(pos, \"OSC 22 pointer shape\");\n    }),\n";
/** The exact pinned bytes of one retained bundle seam: the helper that makes the verdict inconclusive. */
export const seamUnmeasured = "function unmeasuredCellResult(position, feature) {\n  return {\n    pass: false,\n    ...position && { response: `${position.row};${position.col}` },\n    note: position ? `Cursor response does not measure ${feature}` : `No cursor response after ${feature}`,\n    observation: {\n      outcome: \"inconclusive\",\n      reason: position ? \"insufficient-evidence\" : \"no-response\",\n      evidence: \"query\"\n    }\n  };\n}\n";
/** The retained previous layer's two export lines, pinned verbatim. */
export const seamLrMarginsAdapterLine = 'export const ntildeLrMarginsAdapter = Object.freeze({';
export const seamLrMarginsTermlessLine = 'export function ntildeRunLrMarginsTermless(probeId, ctx) {';

/** The pinned seams, exposed so portable tests can build a synthetic layered source. */
export const osc22Seams = Object.freeze([seamRegistration, seamUnmeasured, seamLrMarginsAdapterLine, seamLrMarginsTermlessLine]);

/** Derive the OSC 22 grading extension from the left/right-margin grading extension. */
export function adaptOsc22Collector(lrMarginsSource) {
    const source = String(lrMarginsSource);
    for (const seam of osc22Seams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned OSC 22 probe seam changed; refusing derivation.');
    }
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local OSC 22 grading extension ${JSON.stringify(osc22AdapterId)}.
// Derived from the left/right-margin grading extension; probe definitions, policy
// gates and stock grading remain intact.
export const ntildeOsc22Adapter = Object.freeze({
  id: ${JSON.stringify(osc22AdapterId)},
  probeIds: ${JSON.stringify([...osc22CaseIds])}
});
/** Whether the pinned probe has a termless function at all (it has none). */
export function ntildeOsc22HasTermless(probeId) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe) throw new Error("Unknown pinned probe " + probeId);
  return typeof probe.termless === "function";
}
/** Execute the pinned terminal-path probe over a caller-served context. */
export async function ntildeRunOsc22Terminal(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.term !== "function") throw new Error("Unknown pinned terminal probe " + probeId);
  return { probeId, ...await probe.term(ctx) };
}
`;
}
