// Explicitly versioned collector extension for the erase-in-line qualification
// (erase.line.right, erase.line.left and erase.line.all). This is an eleventh digest-checked
// derivation layered on top of the SGR selective-reset grading extension: the pinned bundle, the
// owned adapter and every earlier grading extension stay byte-for-byte unchanged, and each graded
// result carries this extension's identity so it can never be presented as stock output.
//
// The extension adds no probe of its own. It exposes the PINNED termless probe definitions of the
// three contracted cases so live, independently observed cells can be graded by the collector's own
// eraseRowResult criteria. Derivation refuses unless the retained SGR selective-reset extension
// still contains the exact pinned registrations, helper bodies (isBlank, rowCells, eraseRowResult)
// and the capture-only pixel helper (captureEraseFixture, which never returns a supported or
// unsupported verdict), each verbatim and once.
import { eraseLineCaseIds } from './erase-line-contracts.mjs';

/** Identity of this eleventh layered extension. */
export const eraseLineAdapterId = 'ntilde-owned-erase-line-grading-v1';

/** Exact pinned seams from terminfo.dev@4.0.0 (as retained by the SGR selective-reset extension). Each must appear
 * exactly once, in this order: right registration; left registration; all registration; isBlank; rowCells; eraseRowResult; captureEraseFixture (capture-only pixel path); then the retained previous layer's exports. */
const pinnedEraseLineSeams = Object.freeze([
    "      ...probe(\"erase.line.right\", (ctx) => eraseRowResult(ctx, \"ABCDE\\x1B[3G\", \"\\x1B[K\", [\"A\", \"B\", \"blank\", \"blank\", \"blank\"], false, 2), (ctx) => captureEraseFixture(ctx, \"line\", \"\\x1B[0K\", \"row 1 AB___; row 2 KEEP!\")),\n      termNeedsGeometry: true,\n      termlessObservationEvidence: \"parser-state\"",
    "      ...probe(\"erase.line.left\", (ctx) => eraseRowResult(ctx, \"ABCDE\\x1B[3G\", \"\\x1B[1K\", [\"blank\", \"blank\", \"blank\", \"D\", \"E\"], false, 2), (ctx) => captureEraseFixture(ctx, \"line\", \"\\x1B[1K\", \"row 1 ___DE; row 2 KEEP!\")),\n      termNeedsGeometry: true,\n      termlessObservationEvidence: \"parser-state\"",
    "      ...probe(\"erase.line.all\", (ctx) => eraseRowResult(ctx, `ABCDE\\r\nKEEP!\\x1B[1;3H`, \"\\x1B[2K\", [\"blank\", \"blank\", \"blank\", \"blank\", \"blank\"], true), (ctx) => captureEraseFixture(ctx, \"line\", \"\\x1B[2K\", \"row 1 _____; row 2 KEEP!\")),\n      termNeedsGeometry: true,\n      termlessObservationEvidence: \"parser-state\"",
    "function isBlank(char) {\n  return char === \"\" || char === \" \";\n}",
    "function rowCells(ctx, row) {\n  return Array.from({ length: 5 }, (_, col) => {\n    const char = ctx.getCell(row, col).char;\n    return typeof char === \"string\" ? char : null;\n  });\n}",
    "function eraseRowResult(ctx, setup, sequence, expected, adjacentRow = false, expectedCursorX) {\n  const rows = ctx.getScrollback().screenLines;\n  if (!Number.isSafeInteger(ctx.cols) || ctx.cols < 6 || !Number.isSafeInteger(rows) || rows < (adjacentRow ? 2 : 1)) {\n    return parserStateResult(null, \"Complete erase row fixture\", { cols: ctx.cols, rows }, \"Measured geometry cannot fit the erase fixture\");\n  }\n  ctx.feed(setup);\n  const before = rowCells(ctx, 0);\n  const neighborBefore = adjacentRow ? rowCells(ctx, 1) : undefined;\n  const cursorBefore = expectedCursorX === undefined ? undefined : ctx.getCursor();\n  ctx.feed(sequence);\n  const after = rowCells(ctx, 0);\n  const neighborAfter = adjacentRow ? rowCells(ctx, 1) : undefined;\n  const response = JSON.stringify({\n    before,\n    ...cursorBefore && { cursorBefore: { x: cursorBefore.x, y: cursorBefore.y } },\n    after,\n    ...adjacentRow && { neighborBefore, neighborAfter }\n  });\n  const fixtureReady = before.join(\"\") === \"ABCDE\" && (!adjacentRow || neighborBefore?.join(\"\") === \"KEEP!\") && (expectedCursorX === undefined || cursorBefore?.x === expectedCursorX && cursorBefore?.y === 0);\n  const measured = [...after, ...neighborAfter ?? []];\n  if (!fixtureReady || measured.some((char) => char === null)) {\n    return {\n      pass: false,\n      response,\n      note: \"Erase fixture, cursor setup, or cell metadata unavailable\",\n      observation: { outcome: \"inconclusive\", reason: \"insufficient-evidence\", evidence: \"parser-state\" }\n    };\n  }\n  const exact = expected.every((char, col) => char === \"blank\" ? isBlank(after[col] ?? \"\\x00\") : after[col] === char);\n  const neighborPreserved = !adjacentRow || neighborAfter?.join(\"\") === \"KEEP!\";\n  const supported = exact && neighborPreserved;\n  return {\n    pass: supported,\n    response,\n    observation: { outcome: supported ? \"supported\" : \"unsupported\", evidence: \"parser-state\" },\n    assertions: [\n      {\n        kind: supported ? \"positive\" : \"negative\",\n        expected: `row 0 ${expected.join(\",\")}${adjacentRow ? \"; row 1 KEEP!\" : \"\"}${expectedCursorX === undefined ? \"\" : `; cursor before ${expectedCursorX},0`}`,\n        observed: response\n      }\n    ]\n  };\n}",
    "async function captureEraseFixture(ctx, kind, sequence, expected) {\n  const rows = ctx.rows;\n  const cols = ctx.cols;\n  const minRows = kind === \"line\" ? 2 : 3;\n  const capture = ctx.capture;\n  if (!capture || !Number.isSafeInteger(rows) || !Number.isSafeInteger(cols) || rows < minRows || cols < 6) {\n    const note = `Erase pixel fixture needs capture and measured ${minRows}x6 geometry; measured ${rows}x${cols}`;\n    return {\n      pass: false,\n      observation: { outcome: \"inconclusive\", reason: \"insufficient-evidence\", evidence: \"none\", note }\n    };\n  }\n  const middle = Math.floor((rows + 1) / 2);\n  const safeCursor = `\\x1B[${kind === \"line\" ? 2 : rows};6H`;\n  const seed = kind === \"line\" ? \"\\x1B[1;1HABCDE\\x1B[2;1HKEEP!\" : `\\x1B[1;1HAAAAA\\x1B[${middle};1HBBBBB\\x1B[${rows};1HCCCCC`;\n  const eraseAt = `\\x1B[${kind === \"line\" ? 1 : middle};3H`;\n  try {\n    ctx.write(\"\\x1B[0m\");\n    const blankRow = \" \".repeat(cols);\n    for (let row = 1;row <= rows; row++)\n      ctx.write(`\\x1B[${row};1H${blankRow}`);\n    ctx.write(safeCursor);\n    const blank = await capture({ role: \"control\", label: \"Blank erase comparator\" });\n    ctx.write(seed);\n    ctx.write(safeCursor);\n    const before = await capture({ role: \"control\", label: \"Before erase: seeded and unaffected cells\" });\n    ctx.write(eraseAt);\n    ctx.write(sequence);\n    ctx.write(safeCursor);\n    const target = await capture({ role: \"target\", label: \"After erase: target and unaffected cells\" });\n    const observed = JSON.stringify({\n      rows,\n      cols,\n      middle: kind === \"screen\" ? middle : undefined,\n      blank,\n      before,\n      target\n    });\n    return {\n      pass: false,\n      response: observed,\n      observation: {\n        outcome: \"inconclusive\",\n        reason: \"insufficient-evidence\",\n        evidence: \"pixels\",\n        screenshotRef: target.ref,\n        frames: [blank, before, target],\n        note: \"Blank comparator and seeded target/control pixels captured; erase effect requires independent review\"\n      },\n      assertions: [\n        {\n          kind: \"positive\",\n          expected: `Capture blank comparator, seeded before, and post-${sequence} target at measured geometry; visual expectation ${expected}`,\n          observed,\n          note: \"capture-only assertion; no erase support judgment\"\n        }\n      ]\n    };\n  } finally {\n    ctx.write(\"\\x1B[0m\\x1B[2J\\x1B[H\");\n  }\n}",
    // the retained previous layer's exports
    'export const ntildeSgrResetAdapter = Object.freeze({',
    'export function ntildeRunSgrResetTermless(probeId, ctx) {',
]);

/** The pinned seams, exposed so portable tests can build a synthetic layered source. */
export const eraseLineSeams = pinnedEraseLineSeams;

/** Derive the erase-in-line grading extension from the SGR selective-reset grading extension. */
export function adaptEraseLineCollector(sgrResetSource) {
    const source = String(sgrResetSource);
    for (const seam of pinnedEraseLineSeams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned erase-in-line probe seam changed; refusing derivation.');
    }
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local erase-in-line grading extension ${JSON.stringify(eraseLineAdapterId)}.
// Derived from the SGR selective-reset grading extension; probe definitions, policy
// gates and stock grading remain intact.
export const ntildeEraseLineAdapter = Object.freeze({
  id: ${JSON.stringify(eraseLineAdapterId)},
  probeIds: ${JSON.stringify([...eraseLineCaseIds])}
});
/** Execute one pinned termless probe over caller-served cells. */
export function ntildeRunEraseLineTermless(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.termless !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = probe.termless(ctx);
  return { probeId, ...result };
}
`;
}
