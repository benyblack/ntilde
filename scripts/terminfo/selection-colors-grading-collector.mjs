// Explicitly versioned collector extension for the selection-color qualification (extensions.osc17-highlight-bg,
// extensions.osc19-highlight-fg, extensions.osc117-reset-highlight-bg and extensions.osc119-reset-highlight-fg). This is a digest-checked
// derivation layered on top of the OSC title grading extension: the pinned bundle, the owned adapter and every earlier grading extension
// stay byte-for-byte unchanged, and each graded result carries this extension's identity so it can never be presented as stock output.
//
// The extension adds no probe of its own. It exposes the PINNED probe definitions of the contracted cases so live, independently
// journaled replies and screens can be graded by the collector's own criteria. Derivation refuses unless the retained OSC title
// extension still contains the four pinned registrations verbatim and once: the two whole reset-case registration objects, the two
// oscColorQueryProbe set-case registrations (whose query, pattern, prefix and grading come from the pinned oscColorQueryProbe body
// pinned here), and the helper bodies their verdicts run through (queryOnly, oscReplyResult, notTestedResult and unverifiedEffect).
// The pinned terminal path of the set cases re-asks the same query behind a sentinel and of the reset cases writes the pinned bytes
// and queries the cursor; neither is run as a verdict by this suite and neither is substituted. No other case is pinned here.
import { selectionColorsCaseIds } from './selection-colors-contracts.mjs';

/** Identity of this layered extension. */
export const selectionColorsAdapterId = 'ntilde-owned-selection-colors-grading-v1';

/** Exact pinned seams from terminfo.dev@4.0.0 (as retained by the OSC title extension). Each must appear exactly once. */
const pinnedSelectionColorSeams = Object.freeze([
    // the two whole reset-case registration objects (termless notTestedResult fixture; terminal unverifiedEffect fixture)
    "    {\n      ...probe(\"extensions.osc117-reset-highlight-bg\", (ctx) => {\n        ctx.feed(\"\\x1B]117\\x07X\");\n        const cell = ctx.getCell(0, 0);\n        return notTestedResult(\"highlight background color\", { cell });\n      }, async (ctx) => {\n        ctx.write(\"\\x1B[1;1H\\x1B[2K\");\n        ctx.write(\"\\x1B]117\\x07\");\n        const pos = await ctx.queryCursorPosition();\n        return unverifiedEffect(pos ? \"Cursor answered; highlight background was not read back\" : \"No cursor response after OSC 117\");\n      }),\n      termlessObservationEvidence: \"parser-state\"\n    },",
    "    {\n      ...probe(\"extensions.osc119-reset-highlight-fg\", (ctx) => {\n        ctx.feed(\"\\x1B]119\\x07X\");\n        const cell = ctx.getCell(0, 0);\n        return notTestedResult(\"highlight foreground color\", { cell });\n      }, async (ctx) => {\n        ctx.write(\"\\x1B[1;1H\\x1B[2K\");\n        ctx.write(\"\\x1B]119\\x07\");\n        const pos = await ctx.queryCursorPosition();\n        return unverifiedEffect(pos ? \"Cursor answered; highlight foreground was not read back\" : \"No cursor response after OSC 119\");\n      }),\n      termlessObservationEvidence: \"parser-state\"\n    },",
    // the two set-case registrations; their query, pattern, prefix and grading come from the pinned oscColorQueryProbe body below
    "    oscColorQueryProbe(\"extensions.osc17-highlight-bg\", 17),",
    "    oscColorQueryProbe(\"extensions.osc19-highlight-fg\", 19),",
    // oscColorQueryProbe: the complete function, verbatim
    "function oscColorQueryProbe(id, oscCode) {\n  const querySeq = `\\x1B]${oscCode};?\\x07`;\n  const pattern = new RegExp(`\\\\x1b\\\\]${oscCode};rgb:[0-9a-f]{1,4}/[0-9a-f]{1,4}/[0-9a-f]{1,4}(?:\\\\x07|\\\\x1b\\\\\\\\)`, \"i\");\n  const prefix = new RegExp(`\\\\x1b\\\\]${oscCode};`);\n  return {\n    ...queryOnly(probe(id, (ctx) => {\n      const raw = ctx.feedCapture(querySeq);\n      return oscReplyResult(raw, pattern.exec(raw)?.[0] ?? null, `Complete OSC ${oscCode} color reply`, prefix);\n    }, async (ctx) => {\n      const reply = await ctx.queryWithSentinelOutcome(querySeq, pattern);\n      return oscReplyResult(reply.raw, reply.reason === \"reply\" ? reply.match?.[0] ?? null : null, `Complete OSC ${oscCode} color reply`, prefix);\n    }, \"query\")),\n    termlessObservationEvidence: \"query\"\n  };\n}",
    "function queryOnly(definition) {\n  return { ...definition, termWrites: \"query\" };\n}",
    "function notTestedResult(noObservable, state) {\n  return {\n    pass: false,\n    response: JSON.stringify(state),\n    note: noObservable,\n    notTested: { reason: \"no-semantic-observable\", noObservable }\n  };\n}",
    "function unverifiedEffect(note, response) {\n  return {\n    pass: false,\n    ...response !== undefined && { response },\n    note,\n    observation: { outcome: \"inconclusive\", reason: \"insufficient-evidence\", evidence: \"consumed\", note }\n  };\n}",
    // oscReplyResult: retained by the runtime-color reset layer and re-checked here; both set-case verdicts run through it
    'function oscReplyResult(raw, frame, expected, prefix) {',
    // the retained previous layer's exports
    'export const ntildeOscTitleAdapter = Object.freeze({',
    'export function ntildeRunOscTitleTermless(probeId, ctx) {',
]);

/** The pinned seams, exposed so portable tests can build a synthetic layered source. */
export const selectionColorsSeams = pinnedSelectionColorSeams;

/** Derive the selection-color grading extension from the OSC title grading extension. */
export function adaptSelectionColorsCollector(oscTitleSource) {
    const source = String(oscTitleSource);
    for (const seam of pinnedSelectionColorSeams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned selection-color probe seam changed; refusing derivation.');
    }
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local selection-color grading extension ${JSON.stringify(selectionColorsAdapterId)}.
// Derived from the OSC title grading extension; probe definitions, policy
// gates and stock grading remain intact.
export const ntildeSelectionColorsAdapter = Object.freeze({
  id: ${JSON.stringify(selectionColorsAdapterId)},
  probeIds: ${JSON.stringify([...selectionColorsCaseIds])}
});
/** Execute one pinned termless probe over caller-served replies and screens. */
export function ntildeRunSelectionColorsTermless(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.termless !== "function") throw new Error("Unknown pinned probe " + probeId);
  return { probeId, ...probe.termless(ctx) };
}
`;
}
