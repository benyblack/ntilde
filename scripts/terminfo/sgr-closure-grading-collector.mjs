// Explicitly versioned collector extension for the SGR closure qualification (sgr.blink, sgr.overline and
// sgr.underline.color). This is an eleventh digest-checked derivation layered on top of the SGR selective-reset
// grading extension: the pinned bundle, the owned adapter and every earlier grading extension stay byte-for-byte
// unchanged, and each graded result carries this extension's identity so it can never be presented as stock output.
//
// The extension adds no probe of its own. It exposes the PINNED termless definitions of the three cases so live,
// independently observed cells can be graded by the collector's own criteria. Derivation refuses unless the retained
// SGR selective-reset extension still contains the exact pinned registrations (including the overline
// no-semantic-observable guard), the complete sgrProbe termless body and notTestedResult, each verbatim and once.
// The rgbUnderlineProbe body shared with sgr.underline-color-rgb is already locked by the underline-color layer.
import { sgrClosureCaseIds } from './sgr-closure-contracts.mjs';

/** Identity of this eleventh layered extension. */
export const sgrClosureAdapterId = 'ntilde-owned-sgr-closure-grading-v1';

/** Exact pinned seams from terminfo.dev@4.0.0 (as retained by the earlier extensions). Each must appear exactly once. */
const pinnedClosureSeams = Object.freeze([
    "    sgrProbe(\"sgr.blink\", \"\\x1B[5m\", (cell) => cell.blink === true),",
    "    sgrProbe(\"sgr.overline\", \"\\x1B[53m\", (cell) => cell.overline === undefined ? null : cell.overline === true, (cell) => cell.overline === undefined ? \"cell.overline field not exposed\" : null),",
    "    rgbUnderlineProbe(\"sgr.underline.color\"),",
    "function sgrProbe(id, sequence, check, noObservableWhen) {\n  return {\n    id,\n    termNeedsGeometry: true,\n    termObservationEvidence: \"consumed\",\n    termless(ctx) {\n      ctx.feed(sequence + \"X\");\n      const cell = ctx.getCell(0, 0);\n      if (cell.char === \"X\") {\n        const noObservable = noObservableWhen?.(cell);\n        if (noObservable)\n          return notTestedResult(noObservable, cell);\n      }\n      const measured = cell.char === \"X\" && check(cell) === true;\n      return parserStateResult(measured ? true : null, `${id}: attribute applied to the rendered X cell`, cell, cell.char !== \"X\" ? \"No rendered X cell to evaluate\" : measured ? undefined : \"Cell readback cannot distinguish unsupported styling from unreported attributes or collapsed styles; a negative needs rendering review\");\n    },",
    "function notTestedResult(noObservable, state) {\n  return {\n    pass: false,\n    response: JSON.stringify(state),\n    note: noObservable,\n    notTested: { reason: \"no-semantic-observable\", noObservable }\n  };\n}",
    // the retained previous layer's exports
    'export const ntildeSgrResetAdapter = Object.freeze({',
    'export function ntildeRunSgrResetTermless(probeId, ctx) {',
]);

/** The pinned seams, exposed so portable tests can build a synthetic layered source. */
export const sgrClosureSeams = pinnedClosureSeams;

/** Derive the SGR closure grading extension from the SGR selective-reset grading extension. */
export function adaptSgrClosureCollector(sgrResetSource) {
    const source = String(sgrResetSource);
    for (const seam of pinnedClosureSeams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned SGR closure probe seam changed; refusing derivation.');
    }
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local SGR closure grading extension ${JSON.stringify(sgrClosureAdapterId)}.
// Derived from the SGR selective-reset grading extension; probe definitions, policy
// gates and stock grading remain intact.
export const ntildeSgrClosureAdapter = Object.freeze({
  id: ${JSON.stringify(sgrClosureAdapterId)},
  probeIds: ${JSON.stringify([...sgrClosureCaseIds])}
});
/** Execute one pinned termless probe over caller-served cells. */
export function ntildeRunSgrClosureTermless(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.termless !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = probe.termless(ctx);
  return { probeId, ...result };
}
`;
}
