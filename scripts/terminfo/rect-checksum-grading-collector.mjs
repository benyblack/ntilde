// Explicitly versioned collector extension for the rectangle-checksum qualification (editing.decrqcra). This is a
// twenty-sixth digest-checked derivation layered on top of the column-editing grading extension: the pinned bundle, the owned
// adapter and every earlier grading extension stay byte-for-byte unchanged, and each graded result carries this extension's
// identity so it can never be presented as stock output.
//
// The extension adds no probe of its own. It exposes the PINNED termless probe definition of the contracted case so a live,
// independently journaled reply can be graded by the collector's own criterion. Derivation refuses unless the retained
// column-editing extension still contains the exact pinned registration (termless and terminal functions) and the
// checksumResult helper body, each verbatim and once. The pinned terminal path (a 1x1 request through queryOutcome) is the stock
// collector's; this suite measures the same spelling as a graded control. modes.left-right-margin is a different case and is not
// pinned here.
import { rectChecksumCaseIds } from './rect-checksum-contracts.mjs';

/** Identity of this twenty-sixth layered extension. */
export const rectChecksumAdapterId = 'ntilde-owned-rectangle-checksum-grading-v1';

/** Exact pinned seams from terminfo.dev@4.0.0 (as retained by the column-editing extension). Each must appear exactly once: the
 * editing.decrqcra registration; checksumResult; then the retained previous layer's exports. */
const pinnedRectChecksumSeams = Object.freeze([
    "    probe(\"editing.decrqcra\", (ctx) => {\n      ctx.feed(\"ABCDE\\x1B[1;1H\");\n      return checksumResult(ctx.feedCapture(\"\\x1B[1;1;1;1;1;5*y\"));\n    }, async (ctx) => {\n      const reply = await ctx.queryOutcome(\"\\x1B[1;1;1;1;1;1*y\", /\\x1bP1!~[0-9A-Fa-f]{4}\\x1b\\\\/, 2000);\n      return checksumResult(reply.raw);\n    }),",
    "function checksumResult(response) {\n  const supported = /\\x1bP1!~[0-9A-Fa-f]{4}\\x1b\\\\/.test(response);\n  return {\n    pass: supported,\n    response,\n    note: \"Checks framed reply and request id; checksum arithmetic is not verified\",\n    observation: {\n      outcome: supported ? \"supported\" : \"inconclusive\",\n      evidence: \"query\",\n      ...!supported && { reason: response ? \"invalid-reply\" : \"no-response\" }\n    },\n    ...supported && {\n      assertions: [\n        { kind: \"positive\", expected: \"Complete four-digit checksum reply for request 1\", observed: response }\n      ]\n    }\n  };\n}",
    // the retained previous layer's exports
    'export const ntildeColEditAdapter = Object.freeze({',
    'export function ntildeRunColEditTermless(probeId, ctx) {',
]);

/** The pinned seams, exposed so portable tests can build a synthetic layered source. */
export const rectChecksumSeams = pinnedRectChecksumSeams;

/** Derive the rectangle-checksum grading extension from the column-editing grading extension. */
export function adaptRectChecksumCollector(colEditSource) {
    const source = String(colEditSource);
    for (const seam of pinnedRectChecksumSeams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned rectangle-checksum probe seam changed; refusing derivation.');
    }
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local rectangle-checksum grading extension ${JSON.stringify(rectChecksumAdapterId)}.
// Derived from the column-editing grading extension; probe definitions, policy
// gates and stock grading remain intact.
export const ntildeRectChecksumAdapter = Object.freeze({
  id: ${JSON.stringify(rectChecksumAdapterId)},
  probeIds: ${JSON.stringify([...rectChecksumCaseIds])}
});
/** Execute one pinned termless probe over a caller-served reply. */
export function ntildeRunRectChecksumTermless(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.termless !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = probe.termless(ctx);
  return { probeId, ...result };
}
`;
}
