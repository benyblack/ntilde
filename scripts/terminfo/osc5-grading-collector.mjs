// Explicitly versioned collector extension for the rxvt special-color qualification (extensions.osc5-special-color). This is a digest-checked derivation layered on top of the left/right-margin grading
// extension, a sibling of the device-query, kitty-color, OSC 133 and notification layers: the pinned bundle, the owned adapter and every earlier grading extension stay byte-for-byte unchanged, and each
// graded result carries this extension's identity so it can never be presented as stock output.
//
// The extension adds no probe of its own. It exposes the PINNED probe definition so both of its paths can be replayed over the parent-journaled bytes of their own fenced exchange: the termless function
// (feedCapture of OSC 5;0;? BEL and oscReplyResult over the captured text) and the terminal function (queryWithSentinelOutcome of the same request with the same frame pattern). The derivation refuses
// unless the retained left/right-margin extension still contains the exact registration, the probe and reply helpers that decide its verdict, the factory that registers it and its own exports, each
// verbatim and once.
import { osc5CaseIds } from './osc5-contracts.mjs';

/** Identity of this layered extension. */
export const osc5AdapterId = 'ntilde-owned-osc5-special-colors-grading-v1';

/** Exact pinned seams from terminfo.dev@4.0.0 (as retained by the left/right-margin extension). Each must appear exactly once: the OSC 5 registration, the probe helper, queryOnly, oscReplyResult (the
 * one function that decides the pinned verdict), the oscSimpleQueryProbe factory, then the retained previous layer's exports. */
const pinnedOsc5Seams = Object.freeze([
    // extensions.osc5-special-color registration
    [
        "    oscSimpleQueryProbe(\"extensions.osc5-special-color\", \"\\x1B]5;0;?\\x07\", /\\x1b\\]5;0;rgb:[0-9a-f]{1,4}\\/[0-9a-f]{1,4}\\/[0-9a-f]{1,4}(?:\\x07|\\x1b\\\\)/i, /\\x1b\\]5;0;/, \"Complete OSC 5 index-0 color reply\"),",
    ].join('\n') + '\n',
    // the probe() helper
    [
        "function probe(id, termless, term, termObservationEvidence) {",
        "  return { id, termless, term, ...termObservationEvidence && { termObservationEvidence } };",
        "}",
    ].join('\n') + '\n',
    // queryOnly
    [
        "function queryOnly(definition) {",
        "  return { ...definition, termWrites: \"query\" };",
        "}",
    ].join('\n') + '\n',
    // oscReplyResult
    [
        "function oscReplyResult(raw, frame, expected, prefix) {",
        "  if (frame) {",
        "    return {",
        "      pass: true,",
        "      response: raw,",
        "      observation: { outcome: \"supported\", evidence: \"query\" },",
        "      assertions: [{ kind: \"positive\", expected, observed: frame }]",
        "    };",
        "  }",
        "  return {",
        "    pass: false,",
        "    response: raw,",
        "    observation: {",
        "      outcome: \"inconclusive\",",
        "      reason: prefix.test(raw) ? \"invalid-reply\" : \"no-response\",",
        "      evidence: \"query\"",
        "    }",
        "  };",
        "}",
    ].join('\n') + '\n',
    // the oscSimpleQueryProbe factory
    [
        "function oscSimpleQueryProbe(id, query, pattern, prefix, expected) {",
        "  return {",
        "    ...queryOnly(probe(id, (ctx) => {",
        "      const raw = ctx.feedCapture(query);",
        "      return oscReplyResult(raw, pattern.exec(raw)?.[0] ?? null, expected, prefix);",
        "    }, async (ctx) => {",
        "      const reply = await ctx.queryWithSentinelOutcome(query, pattern);",
        "      return oscReplyResult(reply.raw, reply.reason === \"reply\" ? reply.match?.[0] ?? null : null, expected, prefix);",
        "    }, \"query\")),",
        "    termlessObservationEvidence: \"query\"",
        "  };",
        "}",
    ].join('\n') + '\n',
    // the retained previous layer's exports
    'export const ntildeLrMarginsAdapter = Object.freeze({',
    'export function ntildeRunLrMarginsTermless(probeId, ctx) {',
]);

/** The pinned seams, exposed so portable tests can build a synthetic layered source. */
export const osc5Seams = pinnedOsc5Seams;

/** Derive the special-color grading extension from the left/right-margin grading extension. */
export function adaptOsc5Collector(lrMarginsSource) {
    const source = String(lrMarginsSource);
    for (const seam of pinnedOsc5Seams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned special-color probe seam changed; refusing derivation.');
    }
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local rxvt special-color grading extension ${JSON.stringify(osc5AdapterId)}.
// Derived from the left/right-margin grading extension; probe definitions, policy
// gates and stock grading remain intact.
export const ntildeOsc5Adapter = Object.freeze({
  id: ${JSON.stringify(osc5AdapterId)},
  probeIds: ${JSON.stringify(['extensions.osc5-special-color'])}
});
/** Execute one pinned termless probe over a caller-served terminal context. */
export function ntildeRunOsc5Termless(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.termless !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = probe.termless(ctx);
  return { probeId, ...result };
}
/** Execute one pinned terminal-path probe over a caller-served query/sentinel context. */
export async function ntildeRunOsc5Terminal(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.term !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = await probe.term(ctx);
  return { probeId, ...result };
}
`;
}
