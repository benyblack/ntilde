// Explicitly versioned collector extension for the kitty color-control qualification (extensions.osc21-kitty-color). This is a digest-checked derivation layered on top of the left/right-margin grading
// extension, a sibling of the device-query, xterm color-stack, OSC 9, notification, OSC 7/8, title/icon-stack and OSC 133 layers: the pinned bundle, the owned adapter and every earlier grading extension
// stay byte-for-byte unchanged, and each graded result carries this extension's identity so it can never be presented as stock output.
//
// The extension adds no probe of its own. It exposes the PINNED probe definition so both of its paths can be replayed over the parent-journaled bytes of their own fenced exchange: the termless function
// (feedCapture of OSC 21;foreground=? and kittyForegroundResult over the captured text) and the terminal function (queryWithSentinelOutcome of the same request with the same frame pattern). The derivation
// refuses unless the retained left/right-margin extension still contains the exact registration, the two helper functions that decide its verdict and its own exports, each verbatim and once.
import { kclCaseIds } from './kcl-contracts.mjs';

/** Identity of this layered extension. */
export const kclAdapterId = 'ntilde-owned-kitty-color-grading-v1';

/** Exact pinned seams from terminfo.dev@4.0.0 (as retained by the left/right-margin extension). Each must appear exactly once: the probe registration, kittyForegroundResult and unansweredQuery (the two
 * functions that decide its verdict), then the retained previous layer's exports. */
const pinnedKclSeams = Object.freeze([
    // extensions.osc21-kitty-color registration
    [
        "    {",
        "      ...queryOnly(probe(\"extensions.osc21-kitty-color\", (ctx) => kittyForegroundResult(ctx.feedCapture(\"\\x1B]21;foreground=?\\x1B\\\\\")), async (ctx) => {",
        "        const reply = await ctx.queryWithSentinelOutcome(\"\\x1B]21;foreground=?\\x1B\\\\\", /\\x1b\\]21;([^\\x07\\x1b]*)(?:\\x07|\\x1b\\\\)/);",
        "        if (!reply.match)",
        "          return unansweredQuery(reply, \"No OSC 21 reply; color rendering was not tested\");",
        "        return kittyForegroundResult(reply.match[0] ?? \"\");",
        "      }, \"query\")),",
        "      termlessObservationEvidence: \"query\"",
        "    },"
    ].join('\n') + '\n',
    // kittyForegroundResult
    [
        "function kittyForegroundResult(response) {",
        "  const match = /\\x1b\\]21;([^\\x07\\x1b]*)(?:\\x07|\\x1b\\\\)/.exec(response);",
        "  if (!match) {",
        "    return unansweredQuery({ match: null, reason: \"sentinel\", raw: response, rawBase64: btoa(response) }, \"No OSC 21 reply; color rendering was not tested\");",
        "  }",
        "  const values = match[1]?.split(\";\").filter((field) => field.startsWith(\"foreground=\")) ?? [];",
        "  const value = values.length === 1 ? values[0]?.slice(\"foreground=\".length) : undefined;",
        "  const pass = value === \"\" || value !== undefined && /^rgb:[a-f\\d]{1,4}\\/[a-f\\d]{1,4}\\/[a-f\\d]{1,4}$/i.test(value);",
        "  const note = pass ? \"Foreground query recognized; setting colors and visible rendering were not tested\" : \"OSC 21 replied without one verifiable foreground value; no support conclusion\";",
        "  return {",
        "    pass,",
        "    response,",
        "    note,",
        "    observation: { outcome: pass ? \"supported\" : \"inconclusive\", evidence: \"query\", note },",
        "    ...pass && {",
        "      assertions: [",
        "        {",
        "          kind: \"positive\",",
        "          expected: \"OSC 21 foreground query returns RGB or an explicitly undefined value\",",
        "          observed: match[0]",
        "        }",
        "      ]",
        "    }",
        "  };",
        "}"
    ].join('\n') + '\n',
    // unansweredQuery
    [
        "function unansweredQuery(reply, note) {",
        "  return {",
        "    pass: false,",
        "    response: reply.raw,",
        "    note,",
        "    observation: {",
        "      outcome: \"inconclusive\",",
        "      evidence: \"query\",",
        "      reason: reply.reason === \"timeout\" ? \"timeout\" : \"no-response\",",
        "      note",
        "    }",
        "  };",
        "}"
    ].join('\n') + '\n',
    // the retained previous layer's exports
    'export const ntildeLrMarginsAdapter = Object.freeze({',
    'export function ntildeRunLrMarginsTermless(probeId, ctx) {',
]);

/** The pinned seams, exposed so portable tests can build a synthetic layered source. */
export const kclSeams = pinnedKclSeams;

/** Derive the kitty color-control grading extension from the left/right-margin grading extension. */
export function adaptKclCollector(lrMarginsSource) {
    const source = String(lrMarginsSource);
    for (const seam of pinnedKclSeams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned kitty color probe seam changed; refusing derivation.');
    }
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local kitty color-control grading extension ${JSON.stringify(kclAdapterId)}.
// Derived from the left/right-margin grading extension; probe definitions, policy
// gates and stock grading remain intact.
export const ntildeKclAdapter = Object.freeze({
  id: ${JSON.stringify(kclAdapterId)},
  probeIds: ${JSON.stringify([...kclCaseIds])}
});
/** Execute one pinned termless probe over a caller-served terminal context. */
export function ntildeRunKclTermless(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.termless !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = probe.termless(ctx);
  return { probeId, ...result };
}
/** Execute one pinned terminal-path probe over a caller-served query/sentinel context. */
export async function ntildeRunKclTerminal(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.term !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = await probe.term(ctx);
  return { probeId, ...result };
}
`;
}
