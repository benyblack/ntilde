// Explicitly versioned collector extension for the line-editing qualification
// (editing.insert-lines and editing.delete-lines). This is a thirteenth digest-checked
// derivation layered on top of the erase-in-line grading extension: the pinned bundle, the
// owned adapter and every earlier grading extension stay byte-for-byte unchanged, and each graded
// result carries this extension's identity so it can never be presented as stock output.
//
// The extension adds no probe of its own. It exposes the PINNED termless probe definitions of the
// two contracted cases so live, independently observed cells can be graded by the collector's own
// criteria. Derivation refuses unless the retained erase-in-line extension still contains the exact pinned
// registrations (termless and terminal functions, which never carries a verdict), the parserStateResult and
// isBlank helper bodies and the unmeasuredCellResult pixel helper, each verbatim and once.
import { lineEditCaseIds } from './line-edit-contracts.mjs';

/** Identity of this thirteenth layered extension. */
export const lineEditAdapterId = 'ntilde-owned-line-editing-grading-v1';

/** Exact pinned seams from terminfo.dev@4.0.0 (as retained by the erase-in-line extension). Each must appear
 * exactly once, in this order: the insert-lines registration; the delete-lines registration; parserStateResult; isBlank;
 * unmeasuredCellResult (the never-a-verdict pixel path result); then the retained previous layer's exports. */
const pinnedLineEditSeams = Object.freeze([
    "      ...probe(\"editing.insert-lines\", (ctx) => {\n        const expected = \"IL inserts a blank row 2 and shifts measured rows down while row 1 stays intact\";\n        const screenLines = ctx.getScrollback().screenLines;\n        if (!Number.isSafeInteger(ctx.cols) || ctx.cols < 6 || !Number.isSafeInteger(screenLines) || screenLines < 4) {\n          return parserStateResult(null, expected, { cols: ctx.cols, screenLines }, \"Need six measured columns and four measured screen lines for IL\");\n        }\n        const seed = [\"AAAAA\", \"BBBBB\", \"CCCCC\", \"DDDDD\"];\n        for (const [row, text] of seed.entries())\n          ctx.feed(`\\x1B[${row + 1};1H\\x1B[2K${text}`);\n        const before = seed.map((_, row) => Array.from({ length: 5 }, (_, col) => ctx.getCell(row, col).char));\n        if (before.some((cells, row) => cells.join(\"\") !== seed[row])) {\n          return parserStateResult(null, expected, { before }, \"IL source rows were not measured before the edit\");\n        }\n        ctx.feed(\"\\x1B[2;1H\");\n        const setup = ctx.getCursor();\n        if (setup.x !== 0 || setup.y !== 1) {\n          return parserStateResult(null, expected, { before, setup }, \"IL cursor setup did not reach row 2 column 1\");\n        }\n        ctx.feed(\"\\x1B[1L\");\n        const after = seed.map((_, row) => Array.from({ length: 5 }, (_, col) => ctx.getCell(row, col).char));\n        const controlsValid = after[0]?.join(\"\") === \"AAAAA\";\n        return parserStateResult(controlsValid ? after[1]?.every(isBlank) === true && after[2]?.join(\"\") === \"BBBBB\" && after[3]?.join(\"\") === \"CCCCC\" : null, expected, { before, setup, after }, controlsValid ? undefined : \"IL first-row control changed during the edit\");\n      }, async (ctx) => {\n        if (!Number.isSafeInteger(ctx.rows) || !Number.isSafeInteger(ctx.cols) || ctx.rows < 3 || ctx.cols < 5) {\n          return {\n            pass: false,\n            observation: {\n              outcome: \"inconclusive\",\n              reason: \"insufficient-evidence\",\n              evidence: \"none\",\n              note: `Editing fixture needs at least 3x5, measured ${ctx.rows}x${ctx.cols}`\n            }\n          };\n        }\n        ctx.write(\"\\x1B[3;5H\");\n        ctx.write(\"\\x1B[1L\");\n        const pos = await ctx.queryCursorPosition();\n        return unmeasuredCellResult(pos, \"edited cells or attributes\");\n      }),\n      termNeedsGeometry: true,\n      termlessObservationEvidence: \"parser-state\"",
    "      ...probe(\"editing.delete-lines\", (ctx) => {\n        const expected = \"DL removes row 2 and shifts measured rows up while row 1 stays intact\";\n        const screenLines = ctx.getScrollback().screenLines;\n        if (!Number.isSafeInteger(ctx.cols) || ctx.cols < 6 || !Number.isSafeInteger(screenLines) || screenLines < 4) {\n          return parserStateResult(null, expected, { cols: ctx.cols, screenLines }, \"Need six measured columns and four measured screen lines for DL\");\n        }\n        const seed = [\"AAAAA\", \"BBBBB\", \"CCCCC\", \"DDDDD\"];\n        for (const [row, text] of seed.entries())\n          ctx.feed(`\\x1B[${row + 1};1H\\x1B[2K${text}`);\n        const before = seed.map((_, row) => Array.from({ length: 5 }, (_, col) => ctx.getCell(row, col).char));\n        if (before.some((cells, row) => cells.join(\"\") !== seed[row])) {\n          return parserStateResult(null, expected, { before }, \"DL source rows were not measured before the edit\");\n        }\n        ctx.feed(\"\\x1B[2;1H\");\n        const setup = ctx.getCursor();\n        if (setup.x !== 0 || setup.y !== 1) {\n          return parserStateResult(null, expected, { before, setup }, \"DL cursor setup did not reach row 2 column 1\");\n        }\n        ctx.feed(\"\\x1B[1M\");\n        const after = Array.from({ length: 3 }, (_, row) => Array.from({ length: 5 }, (_, col) => ctx.getCell(row, col).char));\n        const controlsValid = after[0]?.join(\"\") === \"AAAAA\";\n        return parserStateResult(controlsValid ? after[1]?.join(\"\") === \"CCCCC\" && after[2]?.join(\"\") === \"DDDDD\" : null, expected, { before, setup, after }, controlsValid ? undefined : \"DL first-row control changed during the edit\");\n      }, async (ctx) => {\n        if (!Number.isSafeInteger(ctx.rows) || !Number.isSafeInteger(ctx.cols) || ctx.rows < 3 || ctx.cols < 5) {\n          return {\n            pass: false,\n            observation: {\n              outcome: \"inconclusive\",\n              reason: \"insufficient-evidence\",\n              evidence: \"none\",\n              note: `Editing fixture needs at least 3x5, measured ${ctx.rows}x${ctx.cols}`\n            }\n          };\n        }\n        ctx.write(\"\\x1B[3;5H\");\n        ctx.write(\"\\x1B[1M\");\n        const pos = await ctx.queryCursorPosition();\n        return unmeasuredCellResult(pos, \"edited cells or attributes\");\n      }),\n      termNeedsGeometry: true,\n      termlessObservationEvidence: \"parser-state\"",
    "function parserStateResult(pass, expected, state, note) {\n  const response = JSON.stringify(state);\n  return {\n    pass: pass === true,\n    response,\n    ...note && { note },\n    observation: {\n      outcome: pass === null ? \"inconclusive\" : pass ? \"supported\" : \"unsupported\",\n      ...pass === null && { reason: \"insufficient-evidence\" },\n      evidence: \"parser-state\",\n      ...note && { note }\n    },\n    ...pass !== null && {\n      assertions: [{ kind: pass ? \"positive\" : \"negative\", expected, observed: response }]\n    }\n  };\n}",
    "function isBlank(char) {\n  return char === \"\" || char === \" \";\n}",
    "function unmeasuredCellResult(position, feature) {\n  return {\n    pass: false,\n    ...position && { response: `${position.row};${position.col}` },\n    note: position ? `Cursor response does not measure ${feature}` : `No cursor response after ${feature}`,\n    observation: {\n      outcome: \"inconclusive\",\n      reason: position ? \"insufficient-evidence\" : \"no-response\",\n      evidence: \"query\"\n    }\n  };\n}",
    // the retained previous layer's exports
    'export const ntildeEraseLineAdapter = Object.freeze({',
    'export function ntildeRunEraseLineTermless(probeId, ctx) {',
]);

/** The pinned seams, exposed so portable tests can build a synthetic layered source. */
export const lineEditSeams = pinnedLineEditSeams;

/** Derive the line-editing grading extension from the erase-in-line grading extension. */
export function adaptLineEditCollector(eraseLineSource) {
    const source = String(eraseLineSource);
    for (const seam of pinnedLineEditSeams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned line-editing probe seam changed; refusing derivation.');
    }
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local line-editing grading extension ${JSON.stringify(lineEditAdapterId)}.
// Derived from the erase-in-line grading extension; probe definitions, policy
// gates and stock grading remain intact.
export const ntildeLineEditAdapter = Object.freeze({
  id: ${JSON.stringify(lineEditAdapterId)},
  probeIds: ${JSON.stringify([...lineEditCaseIds])}
});
/** Execute one pinned termless probe over caller-served cells. */
export function ntildeRunLineEditTermless(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.termless !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = probe.termless(ctx);
  return { probeId, ...result };
}
`;
}
