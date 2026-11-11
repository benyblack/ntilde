// Explicitly versioned collector extension for the routed-input qualification (input.x10-mouse, input.button-event-mouse and
// modes.application-keypad). This is a twenty-eighth digest-checked derivation layered on top of the left/right-margin grading extension:
// the pinned bundle, the owned adapter and every earlier grading extension stay byte-for-byte unchanged, and each graded result carries
// this extension's identity so it can never be presented as stock output.
//
// The extension adds no probe of its own. It exposes the PINNED termless probe definitions so their mode reads can be served from the
// child's DECRQM replies; those probes always return inconclusive (mouseInputProbe: no generated event and report were validated;
// application keypad: notTestedResult), and the derivation refuses unless the retained left/right-margin extension still contains the
// exact pinned registrations (termless and terminal functions), the mouseInputProbe and notTestedResult helpers and the absent-event
// note, each verbatim and once.
import { routedInputCaseIds } from './routed-input-contracts.mjs';

/** Identity of this twenty-eighth layered extension. */
export const routedInputAdapterId = 'ntilde-owned-routed-input-grading-v1';

/** Exact pinned seams from terminfo.dev@4.0.0 (as retained by the left/right-margin extension), then its retained exports. */
const pinnedRoutedInputSeams = Object.freeze([
    // mouseInputProbe helper
    "function mouseInputProbe(id, modeName) {\n  return probe(id, (ctx) => ({\n    pass: false,\n    response: JSON.stringify({ currentMode: ctx.getMode(modeName) === true }),\n    note: absentEventNote,\n    observation: {\n      outcome: \"inconclusive\",\n      reason: \"insufficient-evidence\",\n      evidence: \"legacy\",\n      note: absentEventNote\n    }\n  }), () => Promise.resolve({\n    pass: false,\n    note: absentEventNote,\n    observation: {\n      outcome: \"inconclusive\",\n      reason: \"insufficient-evidence\",\n      evidence: \"none\",\n      note: absentEventNote\n    }\n  }));\n}\n",
    // input.x10-mouse registration
    "    mouseInputProbe(\"input.x10-mouse\", \"mouseTracking\"),",
    // input.button-event-mouse registration
    "    mouseInputProbe(\"input.button-event-mouse\", \"mouseTracking\")",
    // modes.application-keypad registration
    "    probe(\"modes.application-keypad\", (ctx) => {\n      ctx.feed(\"\\x1B=\");\n      const on = ctx.getMode(\"applicationKeypad\") === true;\n      ctx.feed(\"\\x1B>\");\n      const off = ctx.getMode(\"applicationKeypad\") === false;\n      return notTestedResult(\"keypad input\", { on, off });\n    }, () => Promise.resolve({\n      pass: false,\n      observation: {\n        outcome: \"inconclusive\",\n        reason: \"insufficient-evidence\",\n        evidence: \"none\",\n        note: \"Keypad mode left unchanged; this probe does not measure keypad input encoding\"\n      }\n    })),\n",
    // notTestedResult helper
    "function notTestedResult(noObservable, state) {\n  return {\n    pass: false,\n    response: JSON.stringify(state),\n    note: noObservable,\n    notTested: { reason: \"no-semantic-observable\", noObservable }\n  };\n}\n",
    // absentEventNote declaration
    "var absentEventNote = \"No generated input event and encoded report were validated\", inputProbes;",
    // the retained previous layer's exports
    'export const ntildeLrMarginsAdapter = Object.freeze({',
    'export function ntildeRunLrMarginsTermless(probeId, ctx) {',
]);

/** The pinned seams, exposed so portable tests can build a synthetic layered source. */
export const routedInputSeams = pinnedRoutedInputSeams;

/** Derive the routed-input grading extension from the left/right-margin grading extension. */
export function adaptRoutedInputCollector(lrMarginsSource) {
    const source = String(lrMarginsSource);
    for (const seam of pinnedRoutedInputSeams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned routed-input probe seam changed; refusing derivation.');
    }
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local routed-input grading extension ${JSON.stringify(routedInputAdapterId)}.
// Derived from the left/right-margin grading extension; probe definitions, policy
// gates and stock grading remain intact.
export const ntildeRoutedInputAdapter = Object.freeze({
  id: ${JSON.stringify(routedInputAdapterId)},
  probeIds: ${JSON.stringify([...routedInputCaseIds])}
});
/** Execute one pinned termless probe over caller-served mode metadata. */
export function ntildeRunRoutedInputTermless(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.termless !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = probe.termless(ctx);
  return { probeId, ...result };
}
`;
}
