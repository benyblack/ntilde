// Explicitly versioned collector extension for the rxvt OSC 720 qualification (extensions.osc720-scroll-up). A digest-checked derivation layered on the left/right-margin grading
// extension, a sibling of the OSC 5, OSC 9 and device-query layers: the pinned bundle, the owned adapter and every earlier grading extension stay byte-for-byte unchanged, and each graded
// result carries this extension's identity so it can never be presented as stock output.
//
// The extension adds no probe of its own. It exposes the PINNED probe definition so both of its paths can be replayed: the termless function (thirty fed lines, two scrollback reads and the bare
// OSC 720 BEL, over a parent-built scrollback context) and the terminal function (two writes and a cursor-position query, always inconclusive). The derivation refuses unless the retained
// left/right-margin extension still contains the exact pinned function and registration, each verbatim and once.
import { osc720CaseIds } from './osc720-contracts.mjs';

/** Identity of this layered extension. */
export const osc720AdapterId = 'ntilde-owned-osc720-grading-v1';

/** The exact pinned bytes of the termless function. */
export const seamFunction = "function osc720ScrollProbe() {\n  return (ctx) => {\n    for (let i = 0;i < 30; i++)\n      ctx.feed(`scroll-${i}\\r\n`);\n    const before = ctx.getScrollback();\n    if (before.totalLines <= before.screenLines || before.viewportOffset <= 0) {\n      return parserStateResult(null, \"OSC 720 moves the scrollback viewport up\", { before }, \"No scrollback viewport available\");\n    }\n    ctx.feed(\"\\x1B]720\\x07\");\n    const after = ctx.getScrollback();\n    const pass = after.viewportOffset < before.viewportOffset;\n    return parserStateResult(pass, \"OSC 720 moves the scrollback viewport up\", { before, after });\n  };\n}\n";
/** The exact pinned bytes of the registration. */
export const seamRegistration = "    {\n      ...probe(\"extensions.osc720-scroll-up\", osc720ScrollProbe(), async (ctx) => {\n        ctx.write(\"\\x1B[1;1H\\x1B[2K\");\n        ctx.write(\"\\x1B]720\\x07\");\n        const pos = await ctx.queryCursorPosition();\n        return unverifiedEffect(pos ? \"Cursor answered; scrollback viewport was not measured\" : \"No cursor response after OSC 720\");\n      }),\n      termlessObservationEvidence: \"parser-state\"\n    },\n";
/** The retained previous layer's two export lines, pinned verbatim. */
export const seamLrMarginsAdapterLine = "export const ntildeLrMarginsAdapter = Object.freeze({";
export const seamLrMarginsTermlessLine = "export function ntildeRunLrMarginsTermless(probeId, ctx) {";

/** The pinned seams, exposed so portable tests can build a synthetic layered source. */
export const osc720Seams = Object.freeze([seamFunction, seamRegistration, seamLrMarginsAdapterLine, seamLrMarginsTermlessLine]);

/** Derive the OSC 720 grading extension from the left/right-margin grading extension. */
export function adaptOsc720Collector(lrMarginsSource) {
    const source = String(lrMarginsSource);
    for (const seam of osc720Seams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned OSC 720 probe seam changed; refusing derivation.');
    }
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local OSC 720 grading extension ${JSON.stringify(osc720AdapterId)}.
// Derived from the left/right-margin grading extension; probe definitions, policy
// gates and stock grading remain intact.
export const ntildeOsc720Adapter = Object.freeze({
  id: ${JSON.stringify(osc720AdapterId)},
  probeIds: ${JSON.stringify([...osc720CaseIds])}
});
/** Whether the pinned probe has a termless function. */
export function ntildeOsc720HasTermless(probeId) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe) throw new Error("Unknown pinned probe " + probeId);
  return typeof probe.termless === "function";
}
/** Execute the pinned termless probe over a caller-served scrollback context. */
export function ntildeRunOsc720Termless(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.termless !== "function") throw new Error("Unknown pinned termless probe " + probeId);
  return { probeId, ...probe.termless(ctx) };
}
/** Execute the pinned terminal-path probe over a caller-served write/cursor context. */
export async function ntildeRunOsc720Terminal(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.term !== "function") throw new Error("Unknown pinned terminal probe " + probeId);
  return { probeId, ...await probe.term(ctx) };
}
`;
}
