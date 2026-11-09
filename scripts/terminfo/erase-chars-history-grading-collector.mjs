// Explicitly versioned collector extension for the erase-character / saved-lines qualification
// (erase.character with ECH `ESC[3X` and erase.screen.scrollback with ED 3 `ESC[3J`). This is a twenty-first digest-checked
// derivation layered on top of the attribute-rectangle grading extension: the pinned bundle, the owned adapter and every earlier grading extension
// stay byte-for-byte unchanged, and each graded result carries this extension's identity so it can never be presented as stock output.
//
// The extension adds no probe of its own. It exposes the PINNED termless probe definitions of the two contracted cases so live,
// independently observed screens can be graded by the collector's own criteria. Derivation refuses unless the retained attribute-rectangle
// extension still contains the exact pinned registrations (each with its termless path and its separate cursor-query path, which never
// returns a verdict), the eraseRowResult helper and the isBlank, rowCells, parserStateResult and unmeasuredCellResult helpers, each verbatim
// and once. The ED 0/1/2 registrations (erase.screen.below, above and all) and eraseScreenResult are different cases and are not pinned here.
import { eraseCharsHistoryCaseIds } from './erase-chars-history-contracts.mjs';
import { eraseLineSeams } from './erase-line-grading-collector.mjs';
import { charEditSeams } from './char-edit-grading-collector.mjs';

/** Identity of this twenty-first layered extension. */
export const eraseCharsHistoryAdapterId = 'ntilde-owned-erase-chars-history-grading-v1';

/** A helper seam the earlier layers already pin, reused verbatim so one source of truth names it. */
const helper = (seams, start) => {
    const seam = seams.find(item => item.startsWith(start));
    if (seam === undefined) throw new Error('Retained helper seam is missing: ' + start);
    return seam;
};

/** Exact pinned seams from terminfo.dev@4.0.0 (as retained by the attribute-rectangle extension). Each must appear exactly once, in this
 * order: erase.screen.scrollback registration; erase.character registration; eraseRowResult; isBlank, rowCells, parserStateResult and
 * unmeasuredCellResult (also pinned by earlier layers, which is harmless: each still occurs once); then the retained previous layer's exports. */
const pinnedEraseCharsHistorySeams = Object.freeze([
    "      ...probe(\"erase.screen.scrollback\", (ctx) => {\n        const expected = \"ED3 removes measured scrollback while keeping screen geometry\";\n        const initial = ctx.getScrollback();\n        const rows = initial.screenLines;\n        if (!Number.isSafeInteger(rows) || rows < 1 || rows > 1000) {\n          return parserStateResult(null, expected, { initial }, \"Need a measured screen size for history setup\");\n        }\n        ctx.feed(`\\r\n`.repeat(rows + 2));\n        const before = ctx.getScrollback();\n        if (!Number.isSafeInteger(before.totalLines) || before.totalLines <= rows || before.screenLines !== rows) {\n          return parserStateResult(null, expected, { initial, before }, \"No measured scrollback to erase\");\n        }\n        ctx.feed(\"\\x1B[3J\");\n        const after = ctx.getScrollback();\n        const measured = Number.isSafeInteger(after.totalLines) && after.totalLines >= rows && after.screenLines === rows;\n        return parserStateResult(measured ? after.totalLines === rows : null, expected, { before, after }, measured ? undefined : \"Scrollback readback or geometry changed unexpectedly\");\n      }, async (ctx) => {\n        if (!Number.isSafeInteger(ctx.rows) || !Number.isSafeInteger(ctx.cols) || ctx.rows < 5 || ctx.cols < 5) {\n          return {\n            pass: false,\n            observation: {\n              outcome: \"inconclusive\",\n              reason: \"insufficient-evidence\",\n              evidence: \"none\",\n              note: `Erase fixture needs at least 5x5, measured ${ctx.rows}x${ctx.cols}`\n            }\n          };\n        }\n        ctx.write(\"\\x1B[5;5H\");\n        ctx.write(\"\\x1B[3J\");\n        const pos = await ctx.queryCursorPosition();\n        return unmeasuredCellResult(pos, \"erased cells or scrollback\");\n      }),\n      termNeedsGeometry: true",
    "      ...probe(\"erase.character\", (ctx) => eraseRowResult(ctx, \"ABCDE\\x1B[1G\", \"\\x1B[3X\", [\"blank\", \"blank\", \"blank\", \"D\", \"E\"], false, 0), async (ctx) => {\n        if (!Number.isSafeInteger(ctx.rows) || !Number.isSafeInteger(ctx.cols) || ctx.rows < 1 || ctx.cols < 6) {\n          return {\n            pass: false,\n            observation: {\n              outcome: \"inconclusive\",\n              reason: \"insufficient-evidence\",\n              evidence: \"none\",\n              note: `Erase fixture needs at least 1x6, measured ${ctx.rows}x${ctx.cols}`\n            }\n          };\n        }\n        ctx.write(\"\\x1B[1;1H\\x1B[2K\");\n        ctx.write(\"ABCD\");\n        ctx.write(\"\\x1B[1;2H\");\n        ctx.write(\"\\x1B[2X\");\n        const pos = await ctx.queryCursorPosition();\n        return unmeasuredCellResult(pos, \"erased cells or scrollback\");\n      }),\n      termNeedsGeometry: true,\n      termlessObservationEvidence: \"parser-state\"",
    "function eraseRowResult(ctx, setup, sequence, expected, adjacentRow = false, expectedCursorX) {\n  const rows = ctx.getScrollback().screenLines;\n  if (!Number.isSafeInteger(ctx.cols) || ctx.cols < 6 || !Number.isSafeInteger(rows) || rows < (adjacentRow ? 2 : 1)) {\n    return parserStateResult(null, \"Complete erase row fixture\", { cols: ctx.cols, rows }, \"Measured geometry cannot fit the erase fixture\");\n  }\n  ctx.feed(setup);\n  const before = rowCells(ctx, 0);\n  const neighborBefore = adjacentRow ? rowCells(ctx, 1) : undefined;\n  const cursorBefore = expectedCursorX === undefined ? undefined : ctx.getCursor();\n  ctx.feed(sequence);\n  const after = rowCells(ctx, 0);\n  const neighborAfter = adjacentRow ? rowCells(ctx, 1) : undefined;\n  const response = JSON.stringify({\n    before,\n    ...cursorBefore && { cursorBefore: { x: cursorBefore.x, y: cursorBefore.y } },\n    after,\n    ...adjacentRow && { neighborBefore, neighborAfter }\n  });\n  const fixtureReady = before.join(\"\") === \"ABCDE\" && (!adjacentRow || neighborBefore?.join(\"\") === \"KEEP!\") && (expectedCursorX === undefined || cursorBefore?.x === expectedCursorX && cursorBefore?.y === 0);\n  const measured = [...after, ...neighborAfter ?? []];\n  if (!fixtureReady || measured.some((char) => char === null)) {\n    return {\n      pass: false,\n      response,\n      note: \"Erase fixture, cursor setup, or cell metadata unavailable\",\n      observation: { outcome: \"inconclusive\", reason: \"insufficient-evidence\", evidence: \"parser-state\" }\n    };\n  }\n  const exact = expected.every((char, col) => char === \"blank\" ? isBlank(after[col] ?? \"\\x00\") : after[col] === char);\n  const neighborPreserved = !adjacentRow || neighborAfter?.join(\"\") === \"KEEP!\";\n  const supported = exact && neighborPreserved;\n  return {\n    pass: supported,\n    response,\n    observation: { outcome: supported ? \"supported\" : \"unsupported\", evidence: \"parser-state\" },\n    assertions: [\n      {\n        kind: supported ? \"positive\" : \"negative\",\n        expected: `row 0 ${expected.join(\",\")}${adjacentRow ? \"; row 1 KEEP!\" : \"\"}${expectedCursorX === undefined ? \"\" : `; cursor before ${expectedCursorX},0`}`,\n        observed: response\n      }\n    ]\n  };\n}",
    helper(charEditSeams, 'function isBlank('),
    helper(eraseLineSeams, 'function rowCells('),
    helper(charEditSeams, 'function parserStateResult('),
    helper(charEditSeams, 'function unmeasuredCellResult('),
    // the retained previous layer's exports
    'export const ntildeAttrRectAdapter = Object.freeze({',
    'export function ntildeRunAttrRectTermless(probeId, ctx) {',
]);

/** The pinned seams, exposed so portable tests can build a synthetic layered source. */
export const eraseCharsHistorySeams = pinnedEraseCharsHistorySeams;

/** Derive the erase-character / saved-lines grading extension from the attribute-rectangle grading extension. */
export function adaptEraseCharsHistoryCollector(attrRectSource) {
    const source = String(attrRectSource);
    for (const seam of pinnedEraseCharsHistorySeams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned erase-character/saved-lines probe seam changed; refusing derivation.');
    }
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local erase-character / saved-lines grading extension ${JSON.stringify(eraseCharsHistoryAdapterId)}.
// Derived from the attribute-rectangle grading extension; probe definitions, policy
// gates and stock grading remain intact.
export const ntildeEraseCharsHistoryAdapter = Object.freeze({
  id: ${JSON.stringify(eraseCharsHistoryAdapterId)},
  probeIds: ${JSON.stringify([...eraseCharsHistoryCaseIds])}
});
/** Execute one pinned termless probe over caller-served screens. */
export function ntildeRunEraseCharsHistoryTermless(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.termless !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = probe.termless(ctx);
  return { probeId, ...result };
}
`;
}
