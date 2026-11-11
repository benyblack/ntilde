// Explicitly versioned collector extension for the OSC 133 shell-integration qualification (extensions.osc133-a, -b, -c and -d). This is a
// digest-checked derivation layered on top of the left/right-margin grading extension, a sibling of the routed-input and runtime-color reset layers: the pinned bundle, the owned adapter and every
// earlier grading extension stay byte-for-byte unchanged, and each graded result carries this extension's identity so it can never be presented
// as stock output.
//
// The extension adds no probe of its own. It exposes the PINNED probe definitions so both of their paths can be replayed over the parent-observed
// journal: the termless function (feeds ESC]133;<marker> BEL X and reads cell (0,0)) and the terminal function (homes the cursor, erases the row,
// writes the marker and X, then asks for the cursor position). Both always return inconclusive by design (promptConsumptionResult: "shell prompt
// integration was not observed"), and the derivation refuses unless the retained left/right-margin extension still contains the four exact pinned
// registrations and the promptConsumptionResult helper, each verbatim and once.
import { osc133CaseIds } from './osc133-contracts.mjs';

/** Identity of this layered extension. */
export const osc133AdapterId = 'ntilde-owned-osc133-grading-v1';

/** Exact pinned seams from terminfo.dev@4.0.0 (as retained by the left/right-margin extension). Each must appear exactly once: the four osc133
 * registrations (termless and terminal functions), the promptConsumptionResult helper, then the retained previous layer's exports. */
const pinnedOsc133Seams = Object.freeze([
    // extensions.osc133-a registration
    "    {\n      ...probe(\"extensions.osc133-a\", (ctx) => {\n        ctx.feed(\"\\x1B]133;A\\x07X\");\n        const cell = ctx.getCell(0, 0);\n        return promptConsumptionResult(cell.char === \"X\");\n      }, async (ctx) => {\n        ctx.write(\"\\x1B[1;1H\\x1B[2K\");\n        ctx.write(\"\\x1B]133;A\\x07X\");\n        const pos = await ctx.queryCursorPosition();\n        return promptConsumptionResult(pos === null ? null : pos.col === 2);\n      }),\n      termlessObservationEvidence: \"consumed\"\n    },\n",
    // extensions.osc133-b registration
    "    {\n      ...probe(\"extensions.osc133-b\", (ctx) => {\n        ctx.feed(\"\\x1B]133;B\\x07X\");\n        const cell = ctx.getCell(0, 0);\n        return promptConsumptionResult(cell.char === \"X\");\n      }, async (ctx) => {\n        ctx.write(\"\\x1B[1;1H\\x1B[2K\");\n        ctx.write(\"\\x1B]133;B\\x07X\");\n        const pos = await ctx.queryCursorPosition();\n        return promptConsumptionResult(pos === null ? null : pos.col === 2);\n      }),\n      termlessObservationEvidence: \"consumed\"\n    },\n",
    // extensions.osc133-c registration
    "    {\n      ...probe(\"extensions.osc133-c\", (ctx) => {\n        ctx.feed(\"\\x1B]133;C\\x07X\");\n        const cell = ctx.getCell(0, 0);\n        return promptConsumptionResult(cell.char === \"X\");\n      }, async (ctx) => {\n        ctx.write(\"\\x1B[1;1H\\x1B[2K\");\n        ctx.write(\"\\x1B]133;C\\x07X\");\n        const pos = await ctx.queryCursorPosition();\n        return promptConsumptionResult(pos === null ? null : pos.col === 2);\n      }),\n      termlessObservationEvidence: \"consumed\"\n    },\n",
    // extensions.osc133-d registration
    "    {\n      ...probe(\"extensions.osc133-d\", (ctx) => {\n        ctx.feed(\"\\x1B]133;D;0\\x07X\");\n        const cell = ctx.getCell(0, 0);\n        return promptConsumptionResult(cell.char === \"X\");\n      }, async (ctx) => {\n        ctx.write(\"\\x1B[1;1H\\x1B[2K\");\n        ctx.write(\"\\x1B]133;D;0\\x07X\");\n        const pos = await ctx.queryCursorPosition();\n        return promptConsumptionResult(pos === null ? null : pos.col === 2);\n      }),\n      termlessObservationEvidence: \"consumed\"\n    },\n",
    // promptConsumptionResult helper
    "function promptConsumptionResult(consumed) {\n  const note = consumed === null ? \"No cursor reply after semantic prompt marker\" : consumed ? \"Marker was consumed; shell prompt integration was not observed\" : \"Marker consumption was not confirmed; shell prompt integration was not observed\";\n  return {\n    pass: false,\n    note,\n    observation: {\n      outcome: \"inconclusive\",\n      evidence: consumed === null ? \"query\" : \"consumed\",\n      reason: consumed === null ? \"no-response\" : \"insufficient-evidence\",\n      note\n    }\n  };\n}\n",
    // the retained previous layer's exports
    'export const ntildeLrMarginsAdapter = Object.freeze({',
    'export function ntildeRunLrMarginsTermless(probeId, ctx) {',
]);

/** The pinned seams, exposed so portable tests can build a synthetic layered source. */
export const osc133Seams = pinnedOsc133Seams;

/** Derive the OSC 133 grading extension from the left/right-margin grading extension. */
export function adaptOsc133Collector(lrMarginsSource) {
    const source = String(lrMarginsSource);
    for (const seam of pinnedOsc133Seams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned OSC 133 probe seam changed; refusing derivation.');
    }
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local OSC 133 grading extension ${JSON.stringify(osc133AdapterId)}.
// Derived from the left/right-margin grading extension; probe definitions, policy
// gates and stock grading remain intact.
export const ntildeOsc133Adapter = Object.freeze({
  id: ${JSON.stringify(osc133AdapterId)},
  probeIds: ${JSON.stringify([...osc133CaseIds])}
});
/** Execute one pinned termless probe over a caller-served terminal context. */
export function ntildeRunOsc133Termless(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.termless !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = probe.termless(ctx);
  return { probeId, ...result };
}
/** Execute one pinned terminal-path probe over a caller-served write/cursor-position context. */
export async function ntildeRunOsc133Terminal(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.term !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = await probe.term(ctx);
  return { probeId, ...result };
}
`;
}
