// Explicitly versioned collector extension for the rxvt special-color qualification (extensions.osc5-special-color). A digest-checked derivation layered on the left/right-margin grading extension, a
// sibling of the device-query, kitty-color and OSC 133 layers: the pinned bundle, the owned adapter and every earlier grading extension stay byte-for-byte unchanged, and each graded result carries this
// extension's identity so it can never be presented as stock output.
//
// The extension adds no probe of its own. It exposes the PINNED probe definition so both of its paths can be replayed over the parent-journaled bytes of their own fenced exchange: the termless function
// (feedCapture of OSC 5;0;? BEL and oscReplyResult over the captured text) and the terminal function (queryWithSentinelOutcome of the same request with the same frame pattern). The derivation refuses
// unless the retained left/right-margin extension still contains each pinned seam verbatim and exactly once.

/** Identity of this layered extension. */
export const osc5AdapterId = 'ntilde-owned-osc5-special-colors-grading-v1';

/** The exact pinned bytes of one retained bundle seam. */
export const seamRegistration = "    oscSimpleQueryProbe(\"extensions.osc5-special-color\", \"\\x1B]5;0;?\\x07\", /\\x1b\\]5;0;rgb:[0-9a-f]{1,4}\\/[0-9a-f]{1,4}\\/[0-9a-f]{1,4}(?:\\x07|\\x1b\\\\)/i, /\\x1b\\]5;0;/, \"Complete OSC 5 index-0 color reply\"),\n";
/** The exact pinned bytes of one retained bundle seam. */
export const seamProbe = "function probe(id, termless, term, termObservationEvidence) {\n  return { id, termless, term, ...termObservationEvidence && { termObservationEvidence } };\n}\n";
/** The exact pinned bytes of one retained bundle seam. */
export const seamQueryOnly = "function queryOnly(definition) {\n  return { ...definition, termWrites: \"query\" };\n}\n";
/** The exact pinned bytes of one retained bundle seam. */
export const seamReplyResult = "function oscReplyResult(raw, frame, expected, prefix) {\n  if (frame) {\n    return {\n      pass: true,\n      response: raw,\n      observation: { outcome: \"supported\", evidence: \"query\" },\n      assertions: [{ kind: \"positive\", expected, observed: frame }]\n    };\n  }\n  return {\n    pass: false,\n    response: raw,\n    observation: {\n      outcome: \"inconclusive\",\n      reason: prefix.test(raw) ? \"invalid-reply\" : \"no-response\",\n      evidence: \"query\"\n    }\n  };\n}\n";
/** The exact pinned bytes of one retained bundle seam. */
export const seamFactory = "function oscSimpleQueryProbe(id, query, pattern, prefix, expected) {\n  return {\n    ...queryOnly(probe(id, (ctx) => {\n      const raw = ctx.feedCapture(query);\n      return oscReplyResult(raw, pattern.exec(raw)?.[0] ?? null, expected, prefix);\n    }, async (ctx) => {\n      const reply = await ctx.queryWithSentinelOutcome(query, pattern);\n      return oscReplyResult(reply.raw, reply.reason === \"reply\" ? reply.match?.[0] ?? null : null, expected, prefix);\n    }, \"query\")),\n    termlessObservationEvidence: \"query\"\n  };\n}\n";
/** The retained previous layer's two export lines, pinned verbatim. */
export const seamLrMarginsAdapterLine = "export const ntildeLrMarginsAdapter = Object.freeze({";
export const seamLrMarginsTermlessLine = "export function ntildeRunLrMarginsTermless(probeId, ctx) {";

/** The pinned seams, exposed so portable tests can build a synthetic layered source. */
export const osc5Seams = Object.freeze([seamRegistration, seamProbe, seamQueryOnly, seamReplyResult, seamFactory, seamLrMarginsAdapterLine, seamLrMarginsTermlessLine]);

/** Derive the special-color grading extension from the left/right-margin grading extension. */
export function adaptOsc5Collector(lrMarginsSource) {
    const source = String(lrMarginsSource);
    for (const seam of osc5Seams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned special-color probe seam changed; refusing derivation.');
    }
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local rxvt special-color grading extension "ntilde-owned-osc5-special-colors-grading-v1".
// Derived from the left/right-margin grading extension; probe definitions, policy
// gates and stock grading remain intact.
export const ntildeOsc5Adapter = Object.freeze({
  id: "ntilde-owned-osc5-special-colors-grading-v1",
  probeIds: ["extensions.osc5-special-color"]
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
