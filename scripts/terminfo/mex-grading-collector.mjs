// Explicitly versioned collector extension for the extended-mouse-encoding qualification (modes.utf8-mouse-1005 and input.urxvt-mouse).
// This is a digest-checked derivation layered on top of the left/right-margin grading extension, a sibling of the routed-input,
// modifyOtherKeys and other layers: the pinned bundle, the owned adapter and every earlier grading extension stay byte-for-byte
// unchanged, and each graded result carries this extension's identity so it can never be presented as stock output.
//
// The extension adds no probe of its own. It exposes the PINNED termless probe definitions so their reads can be served from the
// child's own DECRQM replies: input.urxvt-mouse is mouseInputProbe (always inconclusive, nothing validated) and modes.utf8-mouse-1005
// feeds ?1005h, captures the DECRQM reply, feeds ?1005l and returns notTestedResult (no semantic observable). The derivation refuses
// unless the retained left/right-margin extension still contains the exact pinned registrations, helpers and absent-event note, each
// verbatim and once. The stock terminal path of modes.utf8-mouse-1005 (decrpmResult over queryMode) is untouched by this layer.
import { mexCaseIds } from './mex-contracts.mjs';

/** Identity of this layered extension. */
export const mexAdapterId = 'ntilde-owned-mouse-extended-grading-v1';

/** Exact pinned seams from terminfo.dev@4.0.0 (as retained by the left/right-margin extension), then its retained exports. */
const pinnedMexSeams = Object.freeze([
    // mouseInputProbe helper
    "function mouseInputProbe(id, modeName) {\n  return probe(id, (ctx) => ({\n    pass: false,\n    response: JSON.stringify({ currentMode: ctx.getMode(modeName) === true }),\n    note: absentEventNote,\n    observation: {\n      outcome: \"inconclusive\",\n      reason: \"insufficient-evidence\",\n      evidence: \"legacy\",\n      note: absentEventNote\n    }\n  }), () => Promise.resolve({\n    pass: false,\n    note: absentEventNote,\n    observation: {\n      outcome: \"inconclusive\",\n      reason: \"insufficient-evidence\",\n      evidence: \"none\",\n      note: absentEventNote\n    }\n  }));\n}\n",
    // input.urxvt-mouse registration
    "    mouseInputProbe(\"input.urxvt-mouse\", \"mouseTracking\"),",
    // modes.utf8-mouse-1005 registration
    "    decrpmModeProbe(\"modes.utf8-mouse-1005\", 1005, (ctx) => {\n      ctx.feed(\"\\x1B[?1005h\");\n      const response = ctx.feedCapture(\"\\x1B[?1005$p\");\n      ctx.feed(\"\\x1B[?1005l\");\n      if (response.includes(\"$y\")) {\n        return notTestedResult(\"encoded UTF-8 mouse events\", { response });\n      }\n      ctx.feed(\"X\");\n      const ok = ctx.getCell(0, 0).char === \"X\";\n      return notTestedResult(\"encoded UTF-8 mouse events\", { responsive: ok });\n    }),\n",
    // decrpmModeProbe helper
    "function decrpmModeProbe(id, modeNum, termlessFn) {\n  return {\n    id,\n    termWrites: \"query\",\n    termObservationEvidence: \"query\",\n    termless: termlessFn,\n    async term(ctx) {\n      return decrpmResult(await ctx.queryMode(modeNum), modeNum);\n    }\n  };\n}\n",
    // notTestedResult helper
    "function notTestedResult(noObservable, state) {\n  return {\n    pass: false,\n    response: JSON.stringify(state),\n    note: noObservable,\n    notTested: { reason: \"no-semantic-observable\", noObservable }\n  };\n}\n",
    // absentEventNote declaration
    "var absentEventNote = \"No generated input event and encoded report were validated\", inputProbes;",
    // the retained previous layer's exports
    'export const ntildeLrMarginsAdapter = Object.freeze({',
    'export function ntildeRunLrMarginsTermless(probeId, ctx) {',
]);

/** The pinned seams, exposed so portable tests can build a synthetic layered source. */
export const mexSeams = pinnedMexSeams;

/** Derive the extended-mouse grading extension from the left/right-margin grading extension. */
export function adaptMexCollector(lrMarginsSource) {
    const source = String(lrMarginsSource);
    for (const seam of pinnedMexSeams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned extended-mouse probe seam changed; refusing derivation.');
    }
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local extended-mouse grading extension ${JSON.stringify(mexAdapterId)}.
// Derived from the left/right-margin grading extension; probe definitions, policy
// gates and stock grading remain intact.
export const ntildeMexAdapter = Object.freeze({
  id: ${JSON.stringify(mexAdapterId)},
  probeIds: ${JSON.stringify([...mexCaseIds])}
});
/** Execute one pinned termless probe over caller-served DECRQM replies and mode metadata. */
export function ntildeRunMexTermless(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.termless !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = probe.termless(ctx);
  return { probeId, ...result };
}
`;
}
