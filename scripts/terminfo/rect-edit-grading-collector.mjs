// Explicitly versioned collector extension for the rectangle-editing qualification
// (editing.decfra, editing.decera and editing.deccra). This is a twenty-third digest-checked
// derivation layered on top of the erase-character/saved-lines grading extension: the pinned bundle, the
// owned adapter and every earlier grading extension stay byte-for-byte unchanged, and each graded
// result carries this extension's identity so it can never be presented as stock output.
//
// The extension adds no probe of its own. It exposes the PINNED termless probe definitions of the
// three contracted cases so live, independently observed cells can be graded by the collector's own
// criteria. Derivation refuses unless the retained erase-character/saved-lines extension still contains the
// exact pinned registrations (termless and terminal functions, the latter never carrying a verdict),
// the parserStateResult and isBlank helper bodies and the unmeasuredCellResult pixel helper, each
// verbatim and once. editing.decsera, editing.deccara and editing.decrara are different cases and are not pinned here.
import { rectEditCaseIds } from './rect-edit-contracts.mjs';

/** Identity of this twenty-third layered extension. */
export const rectEditAdapterId = 'ntilde-owned-rectangle-editing-grading-v1';

/** Exact pinned seams from terminfo.dev@4.0.0 (as retained by the erase-character/saved-lines extension). Each must appear
 * exactly once, in this order: the editing.decfra, editing.decera and editing.deccra registrations; parserStateResult; isBlank;
 * unmeasuredCellResult (the never-a-verdict pixel path result); then the retained previous layer's exports. */
const pinnedRectEditSeams = Object.freeze([
    "      ...probe(\"editing.decfra\", (ctx) => {\n        const expected = \"DECFRA fills the measured 3x5 area and preserves its measured right flank\";\n        if (!Number.isSafeInteger(ctx.cols) || ctx.cols < 6 || !Number.isSafeInteger(ctx.getScrollback().screenLines) || ctx.getScrollback().screenLines < 3) {\n          return parserStateResult(null, expected, { cols: ctx.cols, rows: ctx.getScrollback().screenLines }, \"Need a measured 3x6 fixture\");\n        }\n        ctx.feed(\"\\x1B[1;1HaaaaaZ\\x1B[2;1HbbbbbY\\x1B[3;1HcccccW\");\n        const before = Array.from({ length: 3 }, (_, row) => Array.from({ length: 6 }, (_, col) => ctx.getCell(row, col).char).join(\"\"));\n        if (before.join(\"|\") !== \"aaaaaZ|bbbbbY|cccccW\") {\n          return parserStateResult(null, expected, { before }, \"DECFRA seed was not measured\");\n        }\n        ctx.feed(\"\\x1B[1;1H\");\n        const setup = ctx.getCursor();\n        if (setup.x !== 0 || setup.y !== 0) {\n          return parserStateResult(null, expected, { before, setup }, \"DECFRA cursor setup failed\");\n        }\n        ctx.feed(\"\\x1B[88;1;1;3;5$x\");\n        const after = Array.from({ length: 3 }, (_, row) => Array.from({ length: 6 }, (_, col) => ctx.getCell(row, col).char).join(\"\"));\n        const controlsValid = after.every((line, row) => line[5] === before[row]?.[5]);\n        return parserStateResult(controlsValid ? after.every((line) => line.slice(0, 5) === \"XXXXX\") : null, expected, { before, setup, after }, controlsValid ? undefined : \"DECFRA outside control changed\");\n      }, async (ctx) => {\n        if (!Number.isSafeInteger(ctx.rows) || !Number.isSafeInteger(ctx.cols) || ctx.rows < 3 || ctx.cols < 5) {\n          return {\n            pass: false,\n            observation: {\n              outcome: \"inconclusive\",\n              reason: \"insufficient-evidence\",\n              evidence: \"none\",\n              note: `Editing fixture needs at least 3x5, measured ${ctx.rows}x${ctx.cols}`\n            }\n          };\n        }\n        ctx.write(\"\\x1B[1;1H\");\n        ctx.write(\"\\x1B[88;1;1;3;5$x\");\n        const pos = await ctx.queryCursorPosition();\n        return unmeasuredCellResult(pos, \"edited cells or attributes\");\n      }),\n      termNeedsGeometry: true,\n      termlessObservationEvidence: \"parser-state\"",
    "      ...probe(\"editing.decera\", (ctx) => {\n        const expected = \"DECERA blanks measured 3x5 cells and preserves their measured right flank\";\n        const rows = ctx.getScrollback().screenLines;\n        if (!Number.isSafeInteger(ctx.cols) || ctx.cols < 6 || !Number.isSafeInteger(rows) || rows < 3) {\n          return parserStateResult(null, expected, { cols: ctx.cols, rows }, \"Need a measured 3x6 fixture\");\n        }\n        const seeds = [\"aaaaaZ\", \"bbbbbY\", \"cccccX\"];\n        ctx.feed(\"\\x1B[1;1HaaaaaZ\\x1B[2;1HbbbbbY\\x1B[3;1HcccccX\");\n        const before = Array.from({ length: 3 }, (_, row) => Array.from({ length: 6 }, (_, col) => ctx.getCell(row, col).char));\n        if (!before.every((line, row) => line.every((char, col) => char === seeds[row]?.[col]))) {\n          return parserStateResult(null, expected, { before }, \"DECERA seed was not measured\");\n        }\n        ctx.feed(\"\\x1B[1;1H\");\n        const setup = ctx.getCursor();\n        if (setup.x !== 0 || setup.y !== 0) {\n          return parserStateResult(null, expected, { before, setup }, \"DECERA cursor setup failed\");\n        }\n        ctx.feed(\"\\x1B[1;1;3;5$z\");\n        const after = Array.from({ length: 3 }, (_, row) => Array.from({ length: 6 }, (_, col) => ctx.getCell(row, col).char));\n        const controlsValid = after.every((line, row) => line[5] === before[row]?.[5]);\n        return parserStateResult(controlsValid ? after.every((line) => line.slice(0, 5).every(isBlank)) : null, expected, { before, setup, after }, controlsValid ? undefined : \"DECERA outside control changed\");\n      }, async (ctx) => {\n        if (!Number.isSafeInteger(ctx.rows) || !Number.isSafeInteger(ctx.cols) || ctx.rows < 3 || ctx.cols < 5) {\n          return {\n            pass: false,\n            observation: {\n              outcome: \"inconclusive\",\n              reason: \"insufficient-evidence\",\n              evidence: \"none\",\n              note: `Editing fixture needs at least 3x5, measured ${ctx.rows}x${ctx.cols}`\n            }\n          };\n        }\n        ctx.write(\"\\x1B[1;1H\");\n        ctx.write(\"\\x1B[1;1;3;5$z\");\n        const pos = await ctx.queryCursorPosition();\n        return unmeasuredCellResult(pos, \"edited cells or attributes\");\n      }),\n      termNeedsGeometry: true,\n      termlessObservationEvidence: \"parser-state\"",
    "      ...probe(\"editing.deccra\", (ctx) => {\n        const expected = \"DECCRA copies measured 2x5 source cells to row 5 column 10 without changing the source\";\n        const rows = ctx.getScrollback().screenLines;\n        if (!Number.isSafeInteger(ctx.cols) || ctx.cols < 14 || !Number.isSafeInteger(rows) || rows < 6) {\n          return parserStateResult(null, expected, { cols: ctx.cols, rows }, \"Need a measured 6x14 fixture\");\n        }\n        ctx.feed(\"\\x1B[1;1HABCDE.........\\x1B[2;1HFGHIJ.........\\x1B[3;1H..............\\x1B[4;1H..............\\x1B[5;1H.........12345\\x1B[6;1H.........67890\");\n        const before = Array.from({ length: 6 }, (_, row) => Array.from({ length: 14 }, (_, col) => ctx.getCell(row, col).char).join(\"\"));\n        if (before.join(\"|\") !== \"ABCDE.........|FGHIJ.........|..............|..............|.........12345|.........67890\") {\n          return parserStateResult(null, expected, { before }, \"DECCRA source and destination were not measured\");\n        }\n        ctx.feed(\"\\x1B[1;1H\");\n        const setup = ctx.getCursor();\n        if (setup.x !== 0 || setup.y !== 0) {\n          return parserStateResult(null, expected, { before, setup }, \"DECCRA cursor setup failed\");\n        }\n        ctx.feed(\"\\x1B[1;1;2;5;1;5;10$v\");\n        const after = Array.from({ length: 6 }, (_, row) => Array.from({ length: 14 }, (_, col) => ctx.getCell(row, col).char).join(\"\"));\n        const controlsValid = after.slice(0, 4).every((line, row) => line === before[row]) && after[4]?.slice(0, 9) === before[4]?.slice(0, 9) && after[5]?.slice(0, 9) === before[5]?.slice(0, 9);\n        return parserStateResult(controlsValid ? after[4]?.slice(9) === \"ABCDE\" && after[5]?.slice(9) === \"FGHIJ\" : null, expected, { before, setup, after }, controlsValid ? undefined : \"DECCRA source or outside control changed\");\n      }, async (ctx) => {\n        if (!Number.isSafeInteger(ctx.rows) || !Number.isSafeInteger(ctx.cols) || ctx.rows < 6 || ctx.cols < 14) {\n          return {\n            pass: false,\n            observation: {\n              outcome: \"inconclusive\",\n              reason: \"insufficient-evidence\",\n              evidence: \"none\",\n              note: `Editing fixture needs at least 6x14, measured ${ctx.rows}x${ctx.cols}`\n            }\n          };\n        }\n        ctx.write(\"\\x1B[1;1H\");\n        ctx.write(\"\\x1B[1;1;2;5;1;5;10$v\");\n        const pos = await ctx.queryCursorPosition();\n        return unmeasuredCellResult(pos, \"edited cells or attributes\");\n      }),\n      termNeedsGeometry: true,\n      termlessObservationEvidence: \"parser-state\"",
    "function parserStateResult(pass, expected, state, note) {\n  const response = JSON.stringify(state);\n  return {\n    pass: pass === true,\n    response,\n    ...note && { note },\n    observation: {\n      outcome: pass === null ? \"inconclusive\" : pass ? \"supported\" : \"unsupported\",\n      ...pass === null && { reason: \"insufficient-evidence\" },\n      evidence: \"parser-state\",\n      ...note && { note }\n    },\n    ...pass !== null && {\n      assertions: [{ kind: pass ? \"positive\" : \"negative\", expected, observed: response }]\n    }\n  };\n}",
    "function isBlank(char) {\n  return char === \"\" || char === \" \";\n}",
    "function unmeasuredCellResult(position, feature) {\n  return {\n    pass: false,\n    ...position && { response: `${position.row};${position.col}` },\n    note: position ? `Cursor response does not measure ${feature}` : `No cursor response after ${feature}`,\n    observation: {\n      outcome: \"inconclusive\",\n      reason: position ? \"insufficient-evidence\" : \"no-response\",\n      evidence: \"query\"\n    }\n  };\n}",
    // the retained previous layer's exports
    'export const ntildeEraseCharsHistoryAdapter = Object.freeze({',
    'export function ntildeRunEraseCharsHistoryTermless(probeId, ctx) {',
]);

/** The pinned seams, exposed so portable tests can build a synthetic layered source. */
export const rectEditSeams = pinnedRectEditSeams;

/** Derive the rectangle-editing grading extension from the erase-character/saved-lines grading extension. */
export function adaptRectEditCollector(eraseCharsHistorySource) {
    const source = String(eraseCharsHistorySource);
    for (const seam of pinnedRectEditSeams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned rectangle-editing probe seam changed; refusing derivation.');
    }
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local rectangle-editing grading extension ${JSON.stringify(rectEditAdapterId)}.
// Derived from the erase-character/saved-lines grading extension; probe definitions, policy
// gates and stock grading remain intact.
export const ntildeRectEditAdapter = Object.freeze({
  id: ${JSON.stringify(rectEditAdapterId)},
  probeIds: ${JSON.stringify([...rectEditCaseIds])}
});
/** Execute one pinned termless probe over caller-served cells. */
export function ntildeRunRectEditTermless(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.termless !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = probe.termless(ctx);
  return { probeId, ...result };
}
`;
}
