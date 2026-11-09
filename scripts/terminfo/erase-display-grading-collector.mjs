// Explicitly versioned collector extension for the erase-in-display qualification
// (erase.screen.below, erase.screen.above and erase.screen.all). This is a thirteenth digest-checked
// derivation layered on top of the erase-in-line grading extension: the pinned bundle, the owned
// adapter and every earlier grading extension stay byte-for-byte unchanged, and each graded result
// carries this extension's identity so it can never be presented as stock output.
//
// The extension adds no probe of its own. It exposes the PINNED termless probe definitions of the three
// contracted cases so live, independently observed cells can be graded by the collector's own
// eraseScreenResult criteria. Derivation refuses unless the retained erase-in-line extension still contains the
// exact pinned registrations, helper bodies (isBlank, rowCells, eraseScreenResult) and the capture-only pixel helper
// (captureEraseFixture, which never returns a supported or unsupported verdict), each verbatim and once. The
// erase.screen.scrollback (ED 3) registration is deliberately NOT a seam: that case is not part of this batch.
import { eraseDisplayCaseIds } from './erase-display-contracts.mjs';
import { eraseLineSeams } from './erase-line-grading-collector.mjs';

/** Identity of this thirteenth layered extension. */
export const eraseDisplayAdapterId = 'ntilde-owned-erase-display-grading-v1';

/** The helper bodies the erase-in-line layer already pins, reused verbatim so one source of truth names them. */
const helperSeams = ['function isBlank(', 'function rowCells(', 'async function captureEraseFixture('].map(start => {
    const seam = eraseLineSeams.find(item => item.startsWith(start));
    if (seam === undefined) throw new Error('Retained erase-in-line helper seam is missing: ' + start);
    return seam;
});

/** Exact pinned seams from terminfo.dev@4.0.0 (as retained by the erase-in-line extension). Each must appear exactly once, in this
 * order: below registration; above registration; all registration; eraseScreenResult; isBlank, rowCells and captureEraseFixture
 * (the capture-only pixel path); then the retained previous layer's exports. */
const pinnedEraseDisplaySeams = Object.freeze([
    "      ...probe(\"erase.screen.below\", (ctx) => eraseScreenResult(ctx, \"\\x1B[0J\", [\"AAAAA\", \"BB   \", \"     \"]), (ctx) => captureEraseFixture(ctx, \"screen\", \"\\x1B[0J\", \"top AAAAA; middle BB___; bottom _____\")),\n      termNeedsGeometry: true,\n      termlessObservationEvidence: \"parser-state\"",
    "      ...probe(\"erase.screen.above\", (ctx) => eraseScreenResult(ctx, \"\\x1B[1J\", [\"     \", \"   BB\", \"CCCCC\"]), (ctx) => captureEraseFixture(ctx, \"screen\", \"\\x1B[1J\", \"top _____; middle ___BB; bottom CCCCC\")),\n      termNeedsGeometry: true,\n      termlessObservationEvidence: \"parser-state\"",
    "      ...probe(\"erase.screen.all\", (ctx) => eraseScreenResult(ctx, \"\\x1B[2J\", [\"     \", \"     \", \"     \"]), (ctx) => captureEraseFixture(ctx, \"screen\", \"\\x1B[2J\", \"top/middle/bottom _____\")),\n      termNeedsGeometry: true,\n      termlessObservationEvidence: \"parser-state\"",
    "function eraseScreenResult(ctx, sequence, expected) {\n  const initialScrollback = ctx.getScrollback();\n  const lines = initialScrollback.screenLines;\n  if (!Number.isSafeInteger(lines) || lines < 3 || !Number.isSafeInteger(ctx.cols) || ctx.cols < 6) {\n    return {\n      pass: false,\n      response: JSON.stringify({ initialScrollback }),\n      observation: {\n        outcome: \"inconclusive\",\n        reason: \"insufficient-evidence\",\n        evidence: \"parser-state\",\n        note: \"Screen fixture needs at least three measured rows and six columns\"\n      }\n    };\n  }\n  const middle = Math.floor(lines / 2);\n  const rows = [0, middle, lines - 1];\n  ctx.feed(`\\x1B[1;1HAAAAA\\x1B[${middle + 1};1HBBBBB\\x1B[${lines};1HCCCCC\\x1B[${middle + 1};3H`);\n  const before = rows.map((row) => rowCells(ctx, row));\n  const cursorBefore = ctx.getCursor();\n  const scrollbackBefore = ctx.getScrollback();\n  ctx.feed(sequence);\n  const after = rows.map((row) => rowCells(ctx, row));\n  const cursorAfter = ctx.getCursor();\n  const scrollbackAfter = ctx.getScrollback();\n  const response = JSON.stringify({\n    rows,\n    before,\n    cursorBefore: { x: cursorBefore.x, y: cursorBefore.y },\n    scrollbackBefore,\n    after,\n    cursorAfter: { x: cursorAfter.x, y: cursorAfter.y },\n    scrollbackAfter\n  });\n  const fixtureReady = before.every((row, i) => row.join(\"\") === [\"AAAAA\", \"BBBBB\", \"CCCCC\"][i]) && cursorBefore.x === 2 && cursorBefore.y === middle && Number.isInteger(scrollbackBefore.totalLines) && scrollbackBefore.totalLines >= lines && !after.some((row) => row.some((char) => char === null));\n  if (!fixtureReady) {\n    return {\n      pass: false,\n      response,\n      observation: {\n        outcome: \"inconclusive\",\n        reason: \"insufficient-evidence\",\n        evidence: \"parser-state\",\n        note: \"Screen fixture, cursor setup, or cell readback unavailable\"\n      }\n    };\n  }\n  const cellsMatch = expected.every((expectedRow, row) => [...expectedRow].every((expectedChar, col) => {\n    const actual = after[row]?.[col];\n    return expectedChar === \" \" ? isBlank(actual ?? \"\\x00\") : actual === expectedChar;\n  }));\n  const preservedCursor = cursorAfter.x === cursorBefore.x && cursorAfter.y === cursorBefore.y;\n  const supported = cellsMatch && preservedCursor;\n  return {\n    pass: supported,\n    response,\n    observation: { outcome: supported ? \"supported\" : \"unsupported\", evidence: \"parser-state\" },\n    assertions: [\n      {\n        kind: supported ? \"positive\" : \"negative\",\n        expected: `rows ${rows.join(\",\")}: ${expected.join(\"/\")}; cursor preserved`,\n        observed: response\n      }\n    ]\n  };\n}",
    ...helperSeams,
    // the retained previous layer's exports
    'export const ntildeEraseLineAdapter = Object.freeze({',
    'export function ntildeRunEraseLineTermless(probeId, ctx) {',
]);

/** The pinned seams, exposed so portable tests can build a synthetic layered source. */
export const eraseDisplaySeams = pinnedEraseDisplaySeams;

/** Derive the erase-in-display grading extension from the erase-in-line grading extension. */
export function adaptEraseDisplayCollector(eraseLineSource) {
    const source = String(eraseLineSource);
    for (const seam of pinnedEraseDisplaySeams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned erase-in-display probe seam changed; refusing derivation.');
    }
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local erase-in-display grading extension ${JSON.stringify(eraseDisplayAdapterId)}.
// Derived from the erase-in-line grading extension; probe definitions, policy
// gates and stock grading remain intact.
export const ntildeEraseDisplayAdapter = Object.freeze({
  id: ${JSON.stringify(eraseDisplayAdapterId)},
  probeIds: ${JSON.stringify([...eraseDisplayCaseIds])}
});
/** Execute one pinned termless probe over caller-served cells. */
export function ntildeRunEraseDisplayTermless(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.termless !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = probe.termless(ctx);
  return { probeId, ...result };
}
`;
}
