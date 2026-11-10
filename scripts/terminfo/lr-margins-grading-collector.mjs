// Explicitly versioned collector extension for the left/right-margin qualification (modes.left-right-margin). This is a
// twenty-seventh digest-checked derivation layered on top of the rectangle-checksum grading extension: the pinned bundle, the owned
// adapter and every earlier grading extension stay byte-for-byte unchanged, and each graded result carries this extension's identity
// so it can never be presented as stock output.
//
// The extension adds no probe of its own. It exposes the PINNED termless probe definition of the contracted case so its mode read can
// be served from the App's journaled state; that probe always returns inconclusive (parserStateResult(null)), and the derivation
// refuses unless the retained rectangle-checksum extension still contains the exact pinned registration (termless and terminal
// functions, the latter returning inconclusive without touching the terminal) and the parserStateResult helper body, each verbatim
// and once.
import { lrMarginsCaseIds } from './lr-margins-contracts.mjs';

/** Identity of this twenty-seventh layered extension. */
export const lrMarginsAdapterId = 'ntilde-owned-left-right-margins-grading-v1';

/** Exact pinned seams from terminfo.dev@4.0.0 (as retained by the rectangle-checksum extension). Each must appear exactly once: the
 * modes.left-right-margin registration; parserStateResult; then the retained previous layer's exports. */
const pinnedLrMarginsSeams = Object.freeze([
    "    probe(\"modes.left-right-margin\", (ctx) => {\n      ctx.feed(\"\\x1B[?69h\");\n      const pass = ctx.getMode(\"leftRightMargin\") === true;\n      ctx.feed(\"\\x1B[?69l\");\n      return parserStateResult(null, \"Left-right margin mode constrains horizontal operations\", { mode: pass }, \"Mode metadata does not measure margins\");\n    }, () => Promise.resolve({\n      pass: false,\n      observation: {\n        outcome: \"inconclusive\",\n        reason: \"insufficient-evidence\",\n        evidence: \"none\",\n        note: \"Margin mode left unchanged; this probe does not measure left/right margin behavior\"\n      }\n    })),",
    "function parserStateResult(pass, expected, state, note) {\n  const response = JSON.stringify(state);\n  return {\n    pass: pass === true,\n    response,\n    ...note && { note },\n    observation: {\n      outcome: pass === null ? \"inconclusive\" : pass ? \"supported\" : \"unsupported\",\n      ...pass === null && { reason: \"insufficient-evidence\" },\n      evidence: \"parser-state\",\n      ...note && { note }\n    },\n    ...pass !== null && {\n      assertions: [{ kind: pass ? \"positive\" : \"negative\", expected, observed: response }]\n    }\n  };\n}",
    // the retained previous layer's exports
    'export const ntildeRectChecksumAdapter = Object.freeze({',
    'export function ntildeRunRectChecksumTermless(probeId, ctx) {',
]);

/** The pinned seams, exposed so portable tests can build a synthetic layered source. */
export const lrMarginsSeams = pinnedLrMarginsSeams;

/** Derive the left/right-margin grading extension from the rectangle-checksum grading extension. */
export function adaptLrMarginsCollector(rectChecksumSource) {
    const source = String(rectChecksumSource);
    for (const seam of pinnedLrMarginsSeams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned left/right-margin probe seam changed; refusing derivation.');
    }
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local left/right-margin grading extension ${JSON.stringify(lrMarginsAdapterId)}.
// Derived from the rectangle-checksum grading extension; probe definitions, policy
// gates and stock grading remain intact.
export const ntildeLrMarginsAdapter = Object.freeze({
  id: ${JSON.stringify(lrMarginsAdapterId)},
  probeIds: ${JSON.stringify([...lrMarginsCaseIds])}
});
/** Execute one pinned termless probe over caller-served mode metadata. */
export function ntildeRunLrMarginsTermless(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.termless !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = probe.termless(ctx);
  return { probeId, ...result };
}
`;
}
