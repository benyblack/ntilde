// Explicitly versioned collector extension for the XTWINOPS title/icon stack qualification (device.xtwinops-22 and device.xtwinops-23). This is a
// digest-checked derivation layered on top of the left/right-margin grading extension, a sibling of the OSC 133, routed-input and runtime-color reset layers:
// the pinned bundle, the owned adapter and every earlier grading extension stay byte-for-byte unchanged, and each graded result carries this extension's
// identity so it can never be presented as stock output.
//
// The extension adds no probe of its own. It exposes the PINNED probe definitions so both of their paths can be replayed over the parent-observed journal:
// the termless function (feeds a title, pushes with CSI 22 ; 0 t, feeds another title, pops with CSI 23 ; 0 t and reads getTitle() after each stage) and the
// terminal function (writes the push or the push/pop pair, then asks for the cursor position, which can only ever be inconclusive). The derivation refuses
// unless the retained left/right-margin extension still contains both exact pinned registrations, each verbatim and once.
import { titleStackCaseIds } from './title-stack-contracts.mjs';

/** Identity of this layered extension. */
export const titleStackAdapterId = 'ntilde-owned-title-stack-grading-v1';

/** Exact pinned seams from terminfo.dev@4.0.0 (as retained by the left/right-margin extension). Each must appear exactly once: the two xtwinops
 * registrations (termless and terminal functions), then the retained previous layer's exports. */
const pinnedTitleStackSeams = Object.freeze([
    // device.xtwinops-22 registration
    "    {\n      ...probe(\"device.xtwinops-22\", (ctx) => {\n        ctx.feed(\"\\x1B]2;pushed-title\\x07\");\n        const original = ctx.getTitle();\n        ctx.feed(\"\\x1B[22;0t\");\n        ctx.feed(\"\\x1B]2;new-title\\x07\");\n        const changed = ctx.getTitle();\n        ctx.feed(\"\\x1B[23;0t\");\n        const restored = ctx.getTitle();\n        return parserStateResult(original === \"pushed-title\" && changed === \"new-title\" && restored === \"pushed-title\" ? true : null, \"XTWINOPS 22 preserves the old title on its stack after a different title is set\", { original, changed, restored }, original !== \"pushed-title\" || changed !== \"new-title\" ? \"Title setup was not observable\" : restored !== \"pushed-title\" ? \"Title push/pop did not restore the old title; the failing operation is unknown\" : undefined);\n      }, async (ctx) => {\n        ctx.write(\"\\x1B]2;pushed-title\\x07\");\n        ctx.write(\"\\x1B[22;0t\");\n        ctx.write(\"\\x1B]2;new-title\\x07\");\n        const pos = await ctx.queryCursorPosition();\n        return {\n          pass: false,\n          note: pos ? \"Cursor answered after title push; title stack was not measured\" : \"No DSR response after title push\",\n          observation: { outcome: \"inconclusive\", reason: \"insufficient-evidence\", evidence: \"consumed\" }\n        };\n      }),\n      termlessObservationEvidence: \"parser-state\"\n    },\n",
    // device.xtwinops-23 registration
    "    {\n      ...probe(\"device.xtwinops-23\", (ctx) => {\n        ctx.feed(\"\\x1B]2;pushed-title\\x07\");\n        const original = ctx.getTitle();\n        ctx.feed(\"\\x1B[22;0t\");\n        ctx.feed(\"\\x1B]2;new-title\\x07\");\n        const changed = ctx.getTitle();\n        ctx.feed(\"\\x1B[23;0t\");\n        const restored = ctx.getTitle();\n        return parserStateResult(original === \"pushed-title\" && changed === \"new-title\" && restored === \"pushed-title\" ? true : null, \"XTWINOPS 23 restores the title saved before a different title was set\", { original, changed, restored }, original !== \"pushed-title\" || changed !== \"new-title\" ? \"Title setup was not observable\" : restored !== \"pushed-title\" ? \"Title push/pop did not restore the old title; the failing operation is unknown\" : undefined);\n      }, async (ctx) => {\n        ctx.write(\"\\x1B[22;0t\");\n        ctx.write(\"\\x1B[23;0t\");\n        const pos = await ctx.queryCursorPosition();\n        return {\n          pass: false,\n          note: pos ? \"Cursor answered after title pop; restored title was not measured\" : \"No DSR response after title pop\",\n          observation: { outcome: \"inconclusive\", reason: \"insufficient-evidence\", evidence: \"consumed\" }\n        };\n      }),\n      termlessObservationEvidence: \"parser-state\"\n    },\n",
    // the retained previous layer's exports
    'export const ntildeLrMarginsAdapter = Object.freeze({',
    'export function ntildeRunLrMarginsTermless(probeId, ctx) {',
]);

/** The pinned seams, exposed so portable tests can build a synthetic layered source. */
export const titleStackSeams = pinnedTitleStackSeams;

/** Derive the title-stack grading extension from the left/right-margin grading extension. */
export function adaptTitleStackCollector(lrMarginsSource) {
    const source = String(lrMarginsSource);
    for (const seam of pinnedTitleStackSeams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned title-stack probe seam changed; refusing derivation.');
    }
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local title-stack grading extension ${JSON.stringify(titleStackAdapterId)}.
// Derived from the left/right-margin grading extension; probe definitions, policy
// gates and stock grading remain intact.
export const ntildeTitleStackAdapter = Object.freeze({
  id: ${JSON.stringify(titleStackAdapterId)},
  probeIds: ${JSON.stringify([...titleStackCaseIds])}
});
/** Execute one pinned termless probe over a caller-served terminal context. */
export function ntildeRunTitleStackTermless(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.termless !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = probe.termless(ctx);
  return { probeId, ...result };
}
/** Execute one pinned terminal-path probe over a caller-served write/cursor-position context. */
export async function ntildeRunTitleStackTerminal(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.term !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = await probe.term(ctx);
  return { probeId, ...result };
}
`;
}
