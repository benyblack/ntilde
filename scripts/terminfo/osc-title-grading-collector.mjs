// Append-only access to the exact pinned probe bodies; stock grading is unchanged.
import { oscTitleCaseIds } from './osc-title-contracts.mjs';
export const oscTitleAdapterId = 'ntilde-owned-osc-title-grading-v1';
export const oscTitleSeams = Object.freeze([
    "    {\n      ...probe(\"extensions.osc0-icon-title\", (ctx) => {\n        const before = ctx.getTitle();\n        ctx.feed(\"\\x1B]0;My Title\\x07\");\n        const after = ctx.getTitle();\n        return notTestedResult(\"icon-name state\", { before, after });\n      }, () => {\n        const note = \"No icon-name and window-title readback for OSC 0\";\n        return Promise.resolve({\n          pass: false,\n          note,\n          observation: { outcome: \"inconclusive\", reason: \"insufficient-evidence\", evidence: \"none\", note }\n        });\n      }),\n      termlessObservationEvidence: \"parser-state\"\n    },\n",
    "    {\n      ...probe(\"extensions.osc1-icon\", (ctx) => {\n        const before = ctx.getTitle();\n        ctx.feed(\"\\x1B]1;test-icon\\x07\");\n        const after = ctx.getTitle();\n        return notTestedResult(\"icon-name state\", { before, after });\n      }, () => {\n        const note = \"No icon-name readback for OSC 1\";\n        return Promise.resolve({\n          pass: false,\n          note,\n          observation: { outcome: \"inconclusive\", reason: \"insufficient-evidence\", evidence: \"none\", note }\n        });\n      }),\n      termlessObservationEvidence: \"parser-state\"\n    },\n",
    "    {\n      ...probe(\"extensions.osc2-title\", (ctx) => {\n        ctx.feed(\"\\x1B]2;Test Title\\x07\");\n        const title = ctx.getTitle();\n        return parserStateResult(title === \"Test Title\", \"OSC 2 sets the exact requested window title\", { title });\n      }, () => Promise.resolve(unmeasuredStateEffect(\"OSC 2 window title\"))),\n      termlessObservationEvidence: \"parser-state\"\n    },\n"
]);
export function adaptOscTitleCollector(source) {
    source = String(source);
    for (const seam of oscTitleSeams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned OSC title probe seam changed.');
    }
    if (source.split('export const ntildeTitleStackAdapter = Object.freeze({').length !== 2) throw new Error('Missing predecessor adapter.');
    return source + `
export const ntildeOscTitleAdapter = Object.freeze({ id: ${JSON.stringify(oscTitleAdapterId)}, probeIds: ${JSON.stringify(oscTitleCaseIds)} });
export function ntildeRunOscTitleTermless(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.termless !== "function") throw new Error("Unknown pinned probe " + probeId);
  return { probeId, ...probe.termless(ctx) };
}
export async function ntildeRunOscTitleTerminal(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.term !== "function") throw new Error("Unknown pinned probe " + probeId);
  return { probeId, ...await probe.term(ctx) };
}
`;
}
