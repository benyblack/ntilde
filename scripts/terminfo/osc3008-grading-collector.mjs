// Explicitly versioned collector extension for the OSC 3008 hierarchical-context qualification (extensions.osc3008-context). A digest-checked derivation layered on the left/right-margin grading
// extension, a sibling of the OSC 5, notification, device-query and OSC 133 layers: the pinned bundle, the owned adapter and every earlier grading extension stay byte-for-byte unchanged, and each
// graded result carries this extension's identity so it can never be presented as stock output.
//
// The extension adds no probe of its own. It exposes the PINNED probe definition so its one path can be replayed over the parent-journaled cursor position: extensions.osc3008-context has no termless
// function and its terminal path (two writes, a cursor query, unmeasuredCellResult) can only return inconclusive. The derivation refuses unless the retained left/right-margin extension still contains the
// exact registration and the unmeasuredCellResult helper, each verbatim and once.
import { osc3008CaseIds } from './osc3008-contracts.mjs';

/** Identity of this layered extension. */
export const osc3008AdapterId = 'ntilde-owned-osc3008-grading-v1';
/** The grading layer this one is derived from. */
export const osc3008DerivedFromAdapterId = 'ntilde-owned-left-right-margins-grading-v1';

/** The exact pinned bytes of one retained bundle seam: the extensions.osc3008-context registration. */
export const seamRegistration = "    probe(\"extensions.osc3008-context\", null, async (ctx) => {\n      ctx.write(\"\\x1B[1;1H\\x1B[2K\");\n      ctx.write(\"\\x1B]3008;type=test\\x07\");\n      const pos = await ctx.queryCursorPosition();\n      return unmeasuredCellResult(pos, \"OSC 3008 systemd context\");\n    }),\n";
/** The exact pinned bytes of one retained bundle seam: the helper that makes the verdict inconclusive. */
export const seamUnmeasured = "function unmeasuredCellResult(position, feature) {\n  return {\n    pass: false,\n    ...position && { response: `${position.row};${position.col}` },\n    note: position ? `Cursor response does not measure ${feature}` : `No cursor response after ${feature}`,\n    observation: {\n      outcome: \"inconclusive\",\n      reason: position ? \"insufficient-evidence\" : \"no-response\",\n      evidence: \"query\"\n    }\n  };\n}\n";
/** The retained previous layer's two export lines, pinned verbatim. */
export const seamLrMarginsAdapterLine = 'export const ntildeLrMarginsAdapter = Object.freeze({';
export const seamLrMarginsTermlessLine = 'export function ntildeRunLrMarginsTermless(probeId, ctx) {';

/** The pinned seams, exposed so portable tests can build a synthetic layered source. */
export const osc3008Seams = Object.freeze([seamRegistration, seamUnmeasured, seamLrMarginsAdapterLine, seamLrMarginsTermlessLine]);

/** Derive the OSC 3008 grading extension from the left/right-margin grading extension. */
export function adaptOsc3008Collector(lrMarginsSource) {
    const source = String(lrMarginsSource);
    for (const seam of osc3008Seams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned OSC 3008 probe seam changed; refusing derivation.');
    }
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local OSC 3008 grading extension ${JSON.stringify(osc3008AdapterId)}.
// Derived from the left/right-margin grading extension; probe definitions, policy
// gates and stock grading remain intact.
export const ntildeOsc3008Adapter = Object.freeze({
  id: ${JSON.stringify(osc3008AdapterId)},
  probeIds: ${JSON.stringify([...osc3008CaseIds])}
});
/** Whether the pinned probe has a termless function at all (it has none). */
export function ntildeOsc3008HasTermless(probeId) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe) throw new Error("Unknown pinned probe " + probeId);
  return typeof probe.termless === "function";
}
/** Execute the pinned terminal-path probe over a caller-served context. */
export async function ntildeRunOsc3008Terminal(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.term !== "function") throw new Error("Unknown pinned terminal probe " + probeId);
  return { probeId, ...await probe.term(ctx) };
}
`;
}
