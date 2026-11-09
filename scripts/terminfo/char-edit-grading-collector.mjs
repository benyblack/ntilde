// Explicitly versioned collector extension for the character-editing qualification
// (editing.insert-chars, editing.delete-chars and editing.repeat-char). This is a thirteenth digest-checked
// derivation layered on top of the erase-in-line grading extension: the pinned bundle, the owned adapter and
// every earlier grading extension stay byte-for-byte unchanged, and each graded result carries this extension's
// identity so it can never be presented as stock output.
//
// The extension adds no probe of its own. It exposes the PINNED termless probe definitions of the three
// contracted cases so live, independently observed cells can be graded by the collector's own criteria.
// Derivation refuses unless the retained erase-in-line extension still contains the exact pinned registrations
// (each with its termless path and its separate cursor-query path, which never returns a cell verdict) and the helper
// bodies (isBlank, parserStateResult, unmeasuredCellResult), each verbatim and once.
import { charEditCaseIds } from './char-edit-contracts.mjs';

/** Identity of this thirteenth layered extension. */
export const charEditAdapterId = 'ntilde-owned-char-edit-grading-v1';

/** Exact pinned seams from terminfo.dev@4.0.0 (as retained by the erase-in-line extension). Each must appear
 * exactly once, in this order: insert-chars registration; delete-chars registration; repeat-char registration;
 * isBlank; parserStateResult; unmeasuredCellResult; then the retained previous layer's exports. */
const pinnedCharEditSeams = Object.freeze([
    "      ...probe(\"editing.insert-chars\", (ctx) => {\n        const expected = \"ICH inserts a blank cell at column 3 and shifts measured text right; prefix stays intact\";\n        if (!Number.isSafeInteger(ctx.cols) || ctx.cols < 8) {\n          return parserStateResult(null, expected, { cols: ctx.cols }, \"Need eight measured columns for the ICH fixture\");\n        }\n        ctx.feed(\"\\x1B[1;1H\\x1B[2KABCDEZQH\");\n        const before = Array.from({ length: 8 }, (_, col) => ctx.getCell(0, col).char);\n        if (before.join(\"\") !== \"ABCDEZQH\") {\n          return parserStateResult(null, expected, { before }, \"ICH seed was not measured before the edit\");\n        }\n        ctx.feed(\"\\x1B[1;3H\");\n        const setup = ctx.getCursor();\n        if (setup.x !== 2 || setup.y !== 0) {\n          return parserStateResult(null, expected, { before, setup }, \"ICH cursor setup did not reach column 3\");\n        }\n        ctx.feed(\"\\x1B[1@\");\n        const after = Array.from({ length: 8 }, (_, col) => ctx.getCell(0, col).char);\n        const controlsValid = after[0] === \"A\" && after[1] === \"B\";\n        return parserStateResult(controlsValid ? isBlank(after[2] ?? \"\") && after.slice(3).join(\"\") === \"CDEZQ\" : null, expected, { before, setup, after }, controlsValid ? undefined : \"ICH prefix control changed during the edit\");\n      }, async (ctx) => {\n        if (!Number.isSafeInteger(ctx.rows) || !Number.isSafeInteger(ctx.cols) || ctx.rows < 1 || ctx.cols < 5) {\n          return {\n            pass: false,\n            observation: {\n              outcome: \"inconclusive\",\n              reason: \"insufficient-evidence\",\n              evidence: \"none\",\n              note: `Editing fixture needs at least 1x5, measured ${ctx.rows}x${ctx.cols}`\n            }\n          };\n        }\n        ctx.write(\"\\x1B[1;1H\\x1B[2K\");\n        ctx.write(\"ABCD\");\n        ctx.write(\"\\x1B[1;2H\");\n        ctx.write(\"\\x1B[1@\");\n        const pos = await ctx.queryCursorPosition();\n        return unmeasuredCellResult(pos, \"edited cells or attributes\");\n      }),\n      termNeedsGeometry: true,\n      termlessObservationEvidence: \"parser-state\"",
    "      ...probe(\"editing.delete-chars\", (ctx) => {\n        const expected = \"DCH deletes the cell at column 3 and shifts measured text left; prefix stays intact\";\n        if (!Number.isSafeInteger(ctx.cols) || ctx.cols < 8) {\n          return parserStateResult(null, expected, { cols: ctx.cols }, \"Need eight measured columns for the DCH fixture\");\n        }\n        ctx.feed(\"\\x1B[1;1H\\x1B[2KABCDEZQH\");\n        const before = Array.from({ length: 8 }, (_, col) => ctx.getCell(0, col).char);\n        if (before.join(\"\") !== \"ABCDEZQH\") {\n          return parserStateResult(null, expected, { before }, \"DCH seed was not measured before the edit\");\n        }\n        ctx.feed(\"\\x1B[1;3H\");\n        const setup = ctx.getCursor();\n        if (setup.x !== 2 || setup.y !== 0) {\n          return parserStateResult(null, expected, { before, setup }, \"DCH cursor setup did not reach column 3\");\n        }\n        ctx.feed(\"\\x1B[1P\");\n        const after = Array.from({ length: 7 }, (_, col) => ctx.getCell(0, col).char);\n        const controlsValid = after[0] === \"A\" && after[1] === \"B\";\n        return parserStateResult(controlsValid ? after.slice(2).join(\"\") === \"DEZQH\" : null, expected, { before, setup, after }, controlsValid ? undefined : \"DCH prefix control changed during the edit\");\n      }, async (ctx) => {\n        if (!Number.isSafeInteger(ctx.rows) || !Number.isSafeInteger(ctx.cols) || ctx.rows < 1 || ctx.cols < 5) {\n          return {\n            pass: false,\n            observation: {\n              outcome: \"inconclusive\",\n              reason: \"insufficient-evidence\",\n              evidence: \"none\",\n              note: `Editing fixture needs at least 1x5, measured ${ctx.rows}x${ctx.cols}`\n            }\n          };\n        }\n        ctx.write(\"\\x1B[1;1H\\x1B[2K\");\n        ctx.write(\"ABCD\");\n        ctx.write(\"\\x1B[1;2H\");\n        ctx.write(\"\\x1B[1P\");\n        const pos = await ctx.queryCursorPosition();\n        return unmeasuredCellResult(pos, \"edited cells or attributes\");\n      }),\n      termNeedsGeometry: true,\n      termlessObservationEvidence: \"parser-state\"",
    "      ...probe(\"editing.repeat-char\", (ctx) => {\n        const expected = \"REP repeats the immediately preceding X into three target cells without changing flank controls\";\n        if (!Number.isSafeInteger(ctx.cols) || ctx.cols < 6) {\n          return parserStateResult(null, expected, { cols: ctx.cols }, \"Need six measured columns for the REP fixture\");\n        }\n        ctx.feed(\"\\x1B[1;1H\\x1B[2KAX   Z\");\n        const seed = Array.from({ length: 6 }, (_, col) => ctx.getCell(0, col).char);\n        if (seed.join(\"\") !== \"AX   Z\") {\n          return parserStateResult(null, expected, { seed }, \"REP flank and blank seed was not measured\");\n        }\n        ctx.feed(\"\\x1B[1;2H\");\n        const targetPosition = ctx.getCursor();\n        if (targetPosition.x !== 1 || targetPosition.y !== 0) {\n          return parserStateResult(null, expected, { seed, targetPosition }, \"REP cursor setup did not reach column 2\");\n        }\n        ctx.feed(\"X\");\n        const before = Array.from({ length: 6 }, (_, col) => ctx.getCell(0, col).char);\n        const setup = ctx.getCursor();\n        if (before.join(\"\") !== \"AX   Z\" || setup.x !== 2 || setup.y !== 0) {\n          return parserStateResult(null, expected, { seed, targetPosition, before, setup }, \"REP X setup was not measured\");\n        }\n        ctx.feed(\"\\x1B[3b\");\n        const after = Array.from({ length: 6 }, (_, col) => ctx.getCell(0, col).char);\n        const controlsValid = after[0] === \"A\" && after[5] === \"Z\";\n        return parserStateResult(controlsValid ? after.slice(1, 5).join(\"\") === \"XXXX\" : null, expected, { seed, targetPosition, before, setup, after }, controlsValid ? undefined : \"REP flank control changed during the edit\");\n      }, async (ctx) => {\n        if (!Number.isSafeInteger(ctx.rows) || !Number.isSafeInteger(ctx.cols) || ctx.rows < 1 || ctx.cols < 6) {\n          return {\n            pass: false,\n            observation: {\n              outcome: \"inconclusive\",\n              reason: \"insufficient-evidence\",\n              evidence: \"none\",\n              note: `Editing fixture needs at least 1x6, measured ${ctx.rows}x${ctx.cols}`\n            }\n          };\n        }\n        ctx.write(\"\\x1B[1;1H\\x1B[2K\");\n        ctx.write(\"X\");\n        ctx.write(\"\\x1B[4b\");\n        const pos = await ctx.queryCursorPosition();\n        return unmeasuredCellResult(pos, \"edited cells or attributes\");\n      }),\n      termNeedsGeometry: true,\n      termlessObservationEvidence: \"parser-state\"",
    "function isBlank(char) {\n  return char === \"\" || char === \" \";\n}",
    "function parserStateResult(pass, expected, state, note) {\n  const response = JSON.stringify(state);\n  return {\n    pass: pass === true,\n    response,\n    ...note && { note },\n    observation: {\n      outcome: pass === null ? \"inconclusive\" : pass ? \"supported\" : \"unsupported\",\n      ...pass === null && { reason: \"insufficient-evidence\" },\n      evidence: \"parser-state\",\n      ...note && { note }\n    },\n    ...pass !== null && {\n      assertions: [{ kind: pass ? \"positive\" : \"negative\", expected, observed: response }]\n    }\n  };\n}",
    "function unmeasuredCellResult(position, feature) {\n  return {\n    pass: false,\n    ...position && { response: `${position.row};${position.col}` },\n    note: position ? `Cursor response does not measure ${feature}` : `No cursor response after ${feature}`,\n    observation: {\n      outcome: \"inconclusive\",\n      reason: position ? \"insufficient-evidence\" : \"no-response\",\n      evidence: \"query\"\n    }\n  };\n}",
    // the retained previous layer's exports
    'export const ntildeEraseLineAdapter = Object.freeze({',
    'export function ntildeRunEraseLineTermless(probeId, ctx) {',
]);

/** The pinned seams, exposed so portable tests can build a synthetic layered source. */
export const charEditSeams = pinnedCharEditSeams;

/** Derive the character-editing grading extension from the erase-in-line grading extension. */
export function adaptCharEditCollector(eraseLineSource) {
    const source = String(eraseLineSource);
    for (const seam of pinnedCharEditSeams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned character-editing probe seam changed; refusing derivation.');
    }
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local character-editing grading extension ${JSON.stringify(charEditAdapterId)}.
// Derived from the erase-in-line grading extension; probe definitions, policy
// gates and stock grading remain intact.
export const ntildeCharEditAdapter = Object.freeze({
  id: ${JSON.stringify(charEditAdapterId)},
  probeIds: ${JSON.stringify([...charEditCaseIds])}
});
/** Execute one pinned termless probe over caller-served cells. */
export function ntildeRunCharEditTermless(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.termless !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = probe.termless(ctx);
  return { probeId, ...result };
}
`;
}
