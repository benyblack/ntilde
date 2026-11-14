// Explicitly versioned collector extension for the OSC 777 / OSC 99 notification qualification (extensions.osc777-notify and extensions.osc99-kitty-notify). This is a
// digest-checked derivation layered on top of the left/right-margin grading extension, a sibling of the OSC 9, OSC 7/8, title/icon-stack and OSC 133 layers:
// the pinned bundle, the owned adapter and every earlier grading extension stay byte-for-byte unchanged, and each graded result carries this extension's identity
// so it can never be presented as stock output.
//
// The extension adds no probe of its own. It exposes the PINNED probe definitions so the pinned OSC 777 terminal path can be replayed over a parent-observed cursor
// position: extensions.osc777-notify has no termless function and its terminal path (two writes, a cursor query, unmeasuredCellResult) can only return inconclusive.
// extensions.osc99-kitty-notify is exposed for identity only: its verdict is decided by the unchanged stock collector in the same owned run and by the exact-query
// witness, so this layer refuses unless the retained left/right-margin extension still contains both exact registrations and the unmeasuredCellResult helper,
// each verbatim and once.
import { notifyCaseIds } from './notify-contracts.mjs';

/** Identity of this layered extension. */
export const notifyAdapterId = 'ntilde-owned-notify-grading-v1';

/** Exact pinned seams from terminfo.dev@4.0.0 (as retained by the left/right-margin extension). Each must appear exactly once: the two registrations, the
 * unmeasuredCellResult helper, then the retained previous layer's exports. */
const pinnedNotifySeams = Object.freeze([
    // extensions.osc777-notify registration
    "    probe(\"extensions.osc777-notify\", null, async (ctx) => {\n      ctx.write(\"\\x1B[1;1H\\x1B[2K\");\n      ctx.write(\"\\x1B]777;notify;test;body\\x07\");\n      const pos = await ctx.queryCursorPosition();\n      return unmeasuredCellResult(pos, \"OSC 777 notification delivery\");\n    }),\n",
    // extensions.osc99-kitty-notify registration
    "    queryOnly(probe(\"extensions.osc99-kitty-notify\", null, async (ctx) => {\n      const id = globalThis.crypto.randomUUID();\n      const reply = await ctx.queryWithSentinelOutcome(`\\x1B]99;i=${id}:p=?;\\x1B\\\\`, new RegExp(`\\\\x1b\\\\]99;i=${id}:p=\\\\?;([^\\\\x07\\\\x1b]*)(?:\\\\x07|\\\\x1b\\\\\\\\)`));\n      if (!reply.match) {\n        return unansweredQuery(reply, \"No matching notification query reply; OS display was not tested\");\n      }\n      const payloadFields = reply.match[1]?.split(\":\").filter((field) => field.startsWith(\"p=\")) ?? [];\n      const pass = payloadFields.length === 1 && payloadFields[0]?.slice(2).split(\",\").includes(\"title\") === true;\n      const note = pass ? \"Notification capabilities acknowledge title payloads; OS display, activation and close events were not tested\" : \"Notification reply does not advertise the required title payload; no support conclusion\";\n      return {\n        pass,\n        response: reply.match[0],\n        note,\n        observation: {\n          outcome: pass ? \"supported\" : \"inconclusive\",\n          evidence: \"query\",\n          ...!pass && { reason: \"invalid-reply\" },\n          note\n        },\n        ...pass && {\n          assertions: [\n            {\n              kind: \"positive\",\n              expected: `OSC 99 echoes i=${id}, p=? and advertises p=title`,\n              observed: reply.match[0] ?? \"\"\n            }\n          ]\n        }\n      };\n    }, \"query\")),\n",
    // unmeasuredCellResult helper
    "function unmeasuredCellResult(position, feature) {\n  return {\n    pass: false,\n    ...position && { response: `${position.row};${position.col}` },\n    note: position ? `Cursor response does not measure ${feature}` : `No cursor response after ${feature}`,\n    observation: {\n      outcome: \"inconclusive\",\n      reason: position ? \"insufficient-evidence\" : \"no-response\",\n      evidence: \"query\"\n    }\n  };\n}\n",
    // the retained previous layer's exports
    'export const ntildeLrMarginsAdapter = Object.freeze({',
    'export function ntildeRunLrMarginsTermless(probeId, ctx) {',
]);

/** The pinned seams, exposed so portable tests can build a synthetic layered source. */
export const notifySeams = pinnedNotifySeams;

/** Derive the notification grading extension from the left/right-margin grading extension. */
export function adaptNotifyCollector(lrMarginsSource) {
    const source = String(lrMarginsSource);
    for (const seam of pinnedNotifySeams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned notification probe seam changed; refusing derivation.');
    }
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local notification grading extension ${JSON.stringify(notifyAdapterId)}.
// Derived from the left/right-margin grading extension; probe definitions, policy
// gates and stock grading remain intact.
export const ntildeNotifyAdapter = Object.freeze({
  id: ${JSON.stringify(notifyAdapterId)},
  probeIds: ${JSON.stringify([...notifyCaseIds])}
});
/** Whether the pinned probe has a termless function at all (neither notification probe has one). */
export function ntildeNotifyHasTermless(probeId) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe) throw new Error("Unknown pinned probe " + probeId);
  return typeof probe.termless === "function";
}
/** Execute one pinned terminal-path probe over a caller-served context. */
export async function ntildeRunNotifyTerminal(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.term !== "function") throw new Error("Unknown pinned terminal probe " + probeId);
  return { probeId, ...await probe.term(ctx) };
}
`;
}
