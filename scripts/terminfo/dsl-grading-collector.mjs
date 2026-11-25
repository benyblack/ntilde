// Explicitly versioned collector extension for the DECSCLM qualification (modes.decsclm). A digest-checked derivation layered on the left/right-margin grading extension, a sibling of the pointer-color,
// device-query, OSC 133 and feature-reporting layers: the pinned bundle, the owned adapter and every earlier grading extension stay byte-for-byte unchanged, and each graded result carries this
// extension's identity so it can never be presented as stock output.
//
// The extension adds no probe of its own. It exposes the PINNED probe definition so both paths can be replayed over the parent-journaled bytes of their own fenced exchange: the termless function
// (feed CSI ? 4 h, feedCapture CSI ? 4 $ p, feed CSI ? 4 l, then always parserStateResult(null, ...)) and the terminal function (decrpmResult over queryMode(4)). The derivation refuses unless the retained
// left/right-margin extension still contains each pinned seam verbatim and exactly once.

/** Identity of this layered extension. */
export const dslAdapterId = 'ntilde-owned-decsclm-grading-v1';

/** The exact pinned bytes of the registration. */
export const seamRegistration = "    decrpmModeProbe(\"modes.decsclm\", 4, (ctx) => {\n      ctx.feed(\"\\x1B[?4h\");\n      const response = ctx.feedCapture(\"\\x1B[?4$p\");\n      ctx.feed(\"\\x1B[?4l\");\n      return parserStateResult(null, \"DECSCLM changes scroll timing\", { response }, \"DECRPM state does not measure scroll timing\");\n    }),\n";
/** The exact pinned bytes of the helper that wraps the termless function and the queryMode terminal path. */
export const seamDecrpmModeProbe = "function decrpmModeProbe(id, modeNum, termlessFn) {\n  return {\n    id,\n    termWrites: \"query\",\n    termObservationEvidence: \"query\",\n    termless: termlessFn,\n    async term(ctx) {\n      return decrpmResult(await ctx.queryMode(modeNum), modeNum);\n    }\n  };\n}\n";
/** The exact pinned bytes of the helper that decides the terminal verdict. */
export const seamDecrpmResult = "function decrpmResult(state, modeNum) {\n  if (state === null) {\n    return {\n      pass: false,\n      note: \"No DECRPM response\",\n      observation: {\n        outcome: \"inconclusive\",\n        reason: \"no-response\",\n        evidence: \"query\",\n        note: \"No DECRPM response\"\n      }\n    };\n  }\n  if (state === \"unknown\") {\n    return {\n      pass: false,\n      note: `DECRPM mode ${modeNum}: not recognized; mode behavior unmeasured`,\n      response: state,\n      observation: {\n        outcome: \"inconclusive\",\n        reason: \"insufficient-evidence\",\n        evidence: \"query\",\n        note: `DECRPM mode ${modeNum}: not recognized; mode behavior unmeasured`\n      }\n    };\n  }\n  return {\n    pass: true,\n    note: `DECRPM mode ${modeNum}: ${state}`,\n    response: state,\n    observation: { outcome: \"supported\", evidence: \"query\" },\n    assertions: [\n      {\n        kind: \"positive\",\n        expected: `DECRPM mode ${modeNum} recognized (set or reset)`,\n        observed: state\n      }\n    ]\n  };\n}\n";
/** The exact pinned bytes of the helper that decides the termless verdict. */
export const seamParserStateResult = "function parserStateResult(pass, expected, state, note) {\n  const response = JSON.stringify(state);\n  return {\n    pass: pass === true,\n    response,\n    ...note && { note },\n    observation: {\n      outcome: pass === null ? \"inconclusive\" : pass ? \"supported\" : \"unsupported\",\n      ...pass === null && { reason: \"insufficient-evidence\" },\n      evidence: \"parser-state\",\n      ...note && { note }\n    },\n    ...pass !== null && {\n      assertions: [{ kind: pass ? \"positive\" : \"negative\", expected, observed: response }]\n    }\n  };\n}\n";
/** The retained previous layer's two export lines, pinned verbatim. */
export const seamLrMarginsAdapterLine = "export const ntildeLrMarginsAdapter = Object.freeze({";
export const seamLrMarginsTermlessLine = "export function ntildeRunLrMarginsTermless(probeId, ctx) {";

/** The pinned seams, exposed so portable tests can build a synthetic layered source. */
export const dslSeams = Object.freeze([seamRegistration, seamDecrpmModeProbe, seamDecrpmResult, seamParserStateResult, seamLrMarginsAdapterLine, seamLrMarginsTermlessLine]);

/** Derive the DECSCLM grading extension from the left/right-margin grading extension. */
export function adaptDslCollector(lrMarginsSource) {
    const source = String(lrMarginsSource);
    for (const seam of dslSeams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned decsclm probe seam changed; refusing derivation.');
    }
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local DECSCLM grading extension "ntilde-owned-decsclm-grading-v1".
// Derived from the left/right-margin grading extension; probe definitions, policy
// gates and stock grading remain intact.
export const ntildeDslAdapter = Object.freeze({
  id: "ntilde-owned-decsclm-grading-v1",
  probeIds: ["modes.decsclm"]
});
/** Execute one pinned termless probe over a caller-served terminal context. */
export function ntildeRunDslTermless(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.termless !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = probe.termless(ctx);
  return { probeId, ...result };
}
/** Execute one pinned terminal-path probe over a caller-served mode-query context. */
export async function ntildeRunDslTerminal(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.term !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = await probe.term(ctx);
  return { probeId, ...result };
}
`;
}
