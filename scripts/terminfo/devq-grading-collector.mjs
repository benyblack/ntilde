// Explicitly versioned collector extension for the device-query qualification (device.xtgetxres, device.xtreportcolors, device.xtwinops-20 and device.xtwinops-21). This is a
// digest-checked derivation layered on top of the left/right-margin grading extension, a sibling of the OSC 9, notification, OSC 7/8, title/icon-stack and OSC 133 layers: the
// pinned bundle, the owned adapter and every earlier grading extension stay byte-for-byte unchanged, and each graded result carries this extension's identity so it can never
// be presented as stock output.
//
// The extension adds no probe of its own. It exposes the PINNED probe definitions so both of their paths can be replayed over the parent-journaled bytes of their own fenced
// exchange: the termless function (feeds the setup, if any, then the query through feedCapture and grades the captured reply with the collector's own deviceReplyResult) and
// the terminal function (writes the same bytes with a sentinel DA through queryWithSentinelOutcome). The derivation refuses unless the retained left/right-margin extension
// still contains the four exact registrations, the two label-reply definitions and its own exports, each verbatim and once.
import { devqCaseIds } from './devq-contracts.mjs';

/** Identity of this layered extension. */
export const devqAdapterId = 'ntilde-owned-device-queries-grading-v1';

/** Exact pinned seams from terminfo.dev@4.0.0 (as retained by the left/right-margin extension). Each must appear exactly once: the XTGETXRES and XTREPORTCOLORS registrations,
 * the icon-label and window-title reply definitions, the two XTWINOPS probes, then the retained previous layer's exports. */
const pinnedDevqSeams = Object.freeze([
    // device.xtgetxres registration
    "    deviceQuery({\n      id: \"device.xtgetxres\",\n      query: \"\\x1BP+Q7465726d4e616d65\\x1B\\\\\",\n      valid: /\\x1bP1\\+R7465726d4e616d65=[0-9A-Fa-f]+\\x1b\\\\/i,\n      refusal: /\\x1bP0\\+R7465726d4e616d65\\x1b\\\\/i,\n      malformed: /\\x1bP[01]\\+R/,\n      expected: \"XTGETXRES returns a complete status-1 termName resource value\"\n    })\n",
    // device.xtreportcolors registration
    "    deviceQuery({\n      id: \"device.xtreportcolors\",\n      query: \"\\x1B[#R\",\n      valid: /\\x1b\\[[0-9;]*#Q/,\n      malformed: /\\x1b\\[[0-9;]*#|#Q/,\n      expected: \"CSI # R returns a complete CSI Pm # Q frame\"\n    }),\n",
    // the icon-label and window-title reply definitions
    "  iconLabelReply = {\n    id: \"device.xtwinops-20\",\n    query: \"\\x1B[20t\",\n    valid: /\\x1b\\]Ltest-icon(?:\\x07|\\x1b\\\\)/,\n    malformed: /\\x1b\\]L/,\n    contradicts: /\\x1b\\]L[^\\x07\\x1b]*(?:\\x07|\\x1b\\\\)/,\n    expected: \"OSC 1 + CSI 20 t round trip returns the exact icon label\"\n  };\n  windowTitleReply = {\n    id: \"device.xtwinops-21\",\n    query: \"\\x1B[21t\",\n    valid: /\\x1b\\]ltest-title(?:\\x07|\\x1b\\\\)/,\n    malformed: /\\x1b\\]l/,\n    expected: \"CSI 21 t returns the exact window title set by OSC 2\"\n  };\n",
    // device.xtwinops-20 and device.xtwinops-21 probes
    "    {\n      ...probe(\"device.xtwinops-20\", (ctx) => {\n        ctx.feed(\"\\x1B]1;test-icon\\x07\");\n        const raw = ctx.feedCapture(iconLabelReply.query);\n        return deviceReplyResult(iconLabelReply, raw, answerPattern(iconLabelReply).exec(raw)?.[0] ?? null, \"sentinel\");\n      }, async (ctx) => {\n        ctx.write(\"\\x1B]1;test-icon\\x07\");\n        const reply = await ctx.queryWithSentinelOutcome(iconLabelReply.query, answerPattern(iconLabelReply));\n        return deviceReplyResult(iconLabelReply, reply.raw, reply.reason === \"reply\" ? reply.match?.[0] ?? null : null, reply.reason);\n      }),\n      termlessObservationEvidence: \"query\"\n    },\n    {\n      ...probe(\"device.xtwinops-21\", (ctx) => {\n        ctx.feed(\"\\x1B]2;test-title\\x07\");\n        const raw = ctx.feedCapture(windowTitleReply.query);\n        return deviceReplyResult(windowTitleReply, raw, windowTitleReply.valid.exec(raw)?.[0] ?? null, \"sentinel\");\n      }, async (ctx) => {\n        ctx.write(\"\\x1B]2;test-title\\x07\");\n        const reply = await ctx.queryWithSentinelOutcome(windowTitleReply.query, windowTitleReply.valid);\n        return deviceReplyResult(windowTitleReply, reply.raw, reply.reason === \"reply\" ? reply.match?.[0] ?? null : null, reply.reason);\n      }),\n      termlessObservationEvidence: \"query\"\n    },\n",
    // the retained previous layer's exports
    'export const ntildeLrMarginsAdapter = Object.freeze({',
    'export function ntildeRunLrMarginsTermless(probeId, ctx) {',
]);

/** The pinned seams, exposed so portable tests can build a synthetic layered source. */
export const devqSeams = pinnedDevqSeams;

/** Derive the device-query grading extension from the left/right-margin grading extension. */
export function adaptDevqCollector(lrMarginsSource) {
    const source = String(lrMarginsSource);
    for (const seam of pinnedDevqSeams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned device-query probe seam changed; refusing derivation.');
    }
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local device-query grading extension ${JSON.stringify(devqAdapterId)}.
// Derived from the left/right-margin grading extension; probe definitions, policy
// gates and stock grading remain intact.
export const ntildeDevqAdapter = Object.freeze({
  id: ${JSON.stringify(devqAdapterId)},
  probeIds: ${JSON.stringify([...devqCaseIds])}
});
/** Execute one pinned termless probe over a caller-served terminal context. */
export function ntildeRunDevqTermless(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.termless !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = probe.termless(ctx);
  return { probeId, ...result };
}
/** Execute one pinned terminal-path probe over a caller-served query/sentinel context. */
export async function ntildeRunDevqTerminal(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.term !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = await probe.term(ctx);
  return { probeId, ...result };
}
`;
}
