// Explicitly versioned collector extension for the SGR selective-reset
// qualification (sgr.selective-reset.underline and sgr.selective-reset.inverse).
// This is a tenth digest-checked derivation layered on top of the SGR-underline-color
// grading extension: the pinned bundle, the owned adapter and every earlier grading
// extension stay byte-for-byte unchanged, and each graded result carries this
// extension's identity so it can never be presented as stock output.
//
// The extension adds no probe of its own. It exposes the PINNED termless resetProbe
// definitions of the two contracted cases so live, independently observed cells can be
// graded by the collector's own measuredReset criteria. Derivation refuses unless the
// retained SGR-underline-color extension still contains the exact pinned probe
// registrations, helper bodies (measuredAttribute, measuredReset, resetProbe) and
// criterion text, each verbatim and once.
import { sgrResetCaseIds } from './sgr-reset-contracts.mjs';

/** Identity of this tenth layered extension. */
export const sgrResetAdapterId = 'ntilde-owned-sgr-selective-reset-grading-v1';

/** Exact pinned seams from terminfo.dev@4.0.0 (as retained by the SGR-underline-color
 * extension). Each must appear exactly once. */
const pinnedResetSeams = Object.freeze([
    '    resetProbe("sgr.selective-reset.underline", "\\x1B[1;4m", "\\x1B[24m", ["underline"], ["bold"]),',
    '    resetProbe("sgr.selective-reset.inverse", "\\x1B[1;7m", "\\x1B[27m", ["inverse"], ["bold"]),',
    "function measuredAttribute(cell, attribute) {\n  const value = cell[attribute];\n  if (value === undefined)\n    return null;\n  if (attribute === \"underline\")\n    return value !== false && value !== null && value !== \"none\";\n  return typeof value === \"boolean\" ? value : null;\n}",
    "function measuredReset(ctx, id, setup, reset, set, preserve) {\n  ctx.feed(`C${setup}X${reset}Y`);\n  const baseline = ctx.getCell(0, 0);\n  const before = ctx.getCell(0, 1);\n  const after = ctx.getCell(0, 2);\n  const state = { baseline, before, after };\n  const expected = `${id}: measured setup attributes clear on Y and unrelated attributes remain set`;\n  if (baseline.char !== \"C\" || before.char !== \"X\" || after.char !== \"Y\") {\n    return parserStateResult(null, expected, state, \"Baseline, styled, or reset cell was not exposed\");\n  }\n  const attributes = [...new Set([...set, ...preserve])];\n  if (attributes.some((attribute) => measuredAttribute(baseline, attribute) !== false) || attributes.some((attribute) => measuredAttribute(before, attribute) !== true) || attributes.some((attribute) => measuredAttribute(after, attribute) === null)) {\n    return parserStateResult(null, expected, state, \"Neutral baseline, styled setup, or reset readback was not measured\");\n  }\n  const cleared = set.every((attribute) => measuredAttribute(after, attribute) === false);\n  const retained = preserve.every((attribute) => measuredAttribute(after, attribute) === true);\n  return parserStateResult(cleared && retained, expected, state);\n}",
    "function resetProbe(id, setup, reset, set, preserve = []) {\n  return {\n    ...probe(id, (ctx) => measuredReset(ctx, id, setup, reset, set, preserve), (ctx) => consumedResetSgr(ctx, setup, reset), \"consumed\"),\n    termlessObservationEvidence: \"parser-state\"\n  };\n}",
    'function parserStateResult(pass, expected, state, note) {',
    // the retained previous layer's exports
    'export const ntildeSgrUnderlineColorAdapter = Object.freeze({',
    'export function ntildeRunSgrUnderlineColorTermless(probeId, ctx) {',
]);

/** The pinned seams, exposed so portable tests can build a synthetic layered source. */
export const sgrResetSeams = pinnedResetSeams;

/** Derive the SGR selective-reset grading extension from the SGR-underline-color grading extension. */
export function adaptSgrResetCollector(sgrUnderlineColorSource) {
    const source = String(sgrUnderlineColorSource);
    for (const seam of pinnedResetSeams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned SGR selective-reset probe seam changed; refusing derivation.');
    }
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local SGR selective-reset grading extension ${JSON.stringify(sgrResetAdapterId)}.
// Derived from the SGR-underline-color grading extension; probe definitions, policy
// gates and stock grading remain intact.
export const ntildeSgrResetAdapter = Object.freeze({
  id: ${JSON.stringify(sgrResetAdapterId)},
  probeIds: ${JSON.stringify([...sgrResetCaseIds])}
});
/** Execute one pinned termless probe over caller-served cells. */
export function ntildeRunSgrResetTermless(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.termless !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = probe.termless(ctx);
  return { probeId, ...result };
}
`;
}
