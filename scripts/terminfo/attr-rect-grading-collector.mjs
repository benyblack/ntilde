// Explicitly versioned collector extension for the attribute-rectangle qualification
// (editing.deccara, editing.decrara and editing.decsace). This is a nineteenth digest-checked derivation layered on top of
// the saved-state/reset grading extension: the pinned bundle, the owned adapter and every earlier grading extension stay
// byte-for-byte unchanged, and each graded result carries this extension's identity so it can never be presented as stock output.
//
// The extension adds no probe of its own. It exposes the PINNED termless probe definitions of the three contracted cases so
// live, independently observed cells can be graded by the collector's own criteria. Derivation refuses unless the retained
// saved-state/reset extension still contains the exact pinned registrations (each with its terminal path, which never carries a
// cell verdict: the cursor-query paths of DECCARA and DECRARA and the not-attempted placeholder of DECSACE) and the two result
// helpers, each verbatim and once. DECSACE's termless path is pinned inconclusive by design; a later upstream change that gave it
// a verdict would be refused.
import { attrRectCaseIds } from './attr-rect-contracts.mjs';

/** Identity of this nineteenth layered extension. */
export const attrRectAdapterId = 'ntilde-owned-attr-rect-grading-v1';

/** Exact pinned seams from terminfo.dev@4.0.0 (as retained by the saved-state/reset extension). Each must appear exactly once;
 * then the retained previous layer's exports. */
const pinnedAttrRectSeams = Object.freeze([
    // editing.deccara registration (termless criterion and its cursor-query terminal path)
    "      ...probe(\"editing.deccara\", (ctx) => {\n        const expected = \"DECCARA sets inverse on measured 3x5 cells, preserving text and right flank\";\n        const rows = ctx.getScrollback().screenLines;\n        if (!Number.isSafeInteger(ctx.cols) || ctx.cols < 6 || !Number.isSafeInteger(rows) || rows < 3) {\n          return parserStateResult(null, expected, { cols: ctx.cols, rows }, \"Need a measured 3x6 fixture\");\n        }\n        ctx.feed(\"\\x1B[1;1HaaaaaZ\\x1B[2;1HbbbbbY\\x1B[3;1HcccccX\");\n        const before = Array.from({ length: 3 }, (_, row) => Array.from({ length: 6 }, (_, col) => ctx.getCell(row, col)));\n        if (before.map((line) => line.map((cell) => cell.char).join(\"\")).join(\"|\") !== \"aaaaaZ|bbbbbY|cccccX\" || before.some((line) => line.some((cell) => cell.inverse))) {\n          return parserStateResult(null, expected, { before }, \"DECCARA character and attribute seed was not measured\");\n        }\n        ctx.feed(\"\\x1B[1;1H\");\n        const setup = ctx.getCursor();\n        if (setup.x !== 0 || setup.y !== 0) {\n          return parserStateResult(null, expected, { before, setup }, \"DECCARA cursor setup failed\");\n        }\n        ctx.feed(\"\\x1B[1;1;3;5;7$r\");\n        const after = Array.from({ length: 3 }, (_, row) => Array.from({ length: 6 }, (_, col) => ctx.getCell(row, col)));\n        const controlsValid = after.every((line, row) => line.every((cell, col) => cell.char === before[row]?.[col]?.char) && !line[5]?.inverse);\n        return parserStateResult(controlsValid ? after.every((line) => line.slice(0, 5).every((cell) => cell.inverse)) : null, expected, { before, setup, after }, controlsValid ? undefined : \"DECCARA character or outside attribute control changed\");\n      }, async (ctx) => {\n        if (!Number.isSafeInteger(ctx.rows) || !Number.isSafeInteger(ctx.cols) || ctx.rows < 3 || ctx.cols < 5) {\n          return {\n            pass: false,\n            observation: {\n              outcome: \"inconclusive\",\n              reason: \"insufficient-evidence\",\n              evidence: \"none\",\n              note: `Editing fixture needs at least 3x5, measured ${ctx.rows}x${ctx.cols}`\n            }\n          };\n        }\n        ctx.write(\"\\x1B[1;1H\");\n        ctx.write(\"\\x1B[1;1;3;5;7$r\");\n        const pos = await ctx.queryCursorPosition();\n        return unmeasuredCellResult(pos, \"edited cells or attributes\");\n      }),\n      termNeedsGeometry: true,\n      termlessObservationEvidence: \"parser-state\"",
    // editing.decrara registration (termless criterion and its cursor-query terminal path)
    "      ...probe(\"editing.decrara\", (ctx) => {\n        const expected = \"DECRARA clears inverse on measured 3x5 inverse cells, preserving text and right flank\";\n        const rows = ctx.getScrollback().screenLines;\n        if (!Number.isSafeInteger(ctx.cols) || ctx.cols < 6 || !Number.isSafeInteger(rows) || rows < 3) {\n          return parserStateResult(null, expected, { cols: ctx.cols, rows }, \"Need a measured 3x6 fixture\");\n        }\n        ctx.feed(\"\\x1B[1;1H\\x1B[7maaaaa\\x1B[0mZ\\x1B[2;1H\\x1B[7mbbbbb\\x1B[0mY\\x1B[3;1H\\x1B[7mccccc\\x1B[0mX\");\n        const before = Array.from({ length: 3 }, (_, row) => Array.from({ length: 6 }, (_, col) => ctx.getCell(row, col)));\n        if (before.map((line) => line.map((cell) => cell.char).join(\"\")).join(\"|\") !== \"aaaaaZ|bbbbbY|cccccX\" || before.some((line) => !line.slice(0, 5).every((cell) => cell.inverse) || line[5]?.inverse)) {\n          return parserStateResult(null, expected, { before }, \"DECRARA inverse seed was not measured\");\n        }\n        ctx.feed(\"\\x1B[1;1H\");\n        const setup = ctx.getCursor();\n        if (setup.x !== 0 || setup.y !== 0) {\n          return parserStateResult(null, expected, { before, setup }, \"DECRARA cursor setup failed\");\n        }\n        ctx.feed(\"\\x1B[1;1;3;5;7$t\");\n        const after = Array.from({ length: 3 }, (_, row) => Array.from({ length: 6 }, (_, col) => ctx.getCell(row, col)));\n        const controlsValid = after.every((line, row) => line.every((cell, col) => cell.char === before[row]?.[col]?.char) && !line[5]?.inverse);\n        return parserStateResult(controlsValid ? after.every((line) => line.slice(0, 5).every((cell) => !cell.inverse)) : null, expected, { before, setup, after }, controlsValid ? undefined : \"DECRARA character or outside attribute control changed\");\n      }, async (ctx) => {\n        if (!Number.isSafeInteger(ctx.rows) || !Number.isSafeInteger(ctx.cols) || ctx.rows < 3 || ctx.cols < 5) {\n          return {\n            pass: false,\n            observation: {\n              outcome: \"inconclusive\",\n              reason: \"insufficient-evidence\",\n              evidence: \"none\",\n              note: `Editing fixture needs at least 3x5, measured ${ctx.rows}x${ctx.cols}`\n            }\n          };\n        }\n        ctx.write(\"\\x1B[1;1H\");\n        ctx.write(\"\\x1B[1;1;3;5;7$t\");\n        const pos = await ctx.queryCursorPosition();\n        return unmeasuredCellResult(pos, \"edited cells or attributes\");\n      }),\n      termNeedsGeometry: true,\n      termlessObservationEvidence: \"parser-state\"",
    // editing.decsace registration (the always-inconclusive consumed termless path and its not-attempted terminal path)
    "    probe(\"editing.decsace\", (ctx) => {\n      ctx.feed(\"\\x1B[1;1H\\x1B[2*x\");\n      const text = ctx.getText();\n      return {\n        pass: false,\n        response: JSON.stringify({ text }),\n        note: \"Sequence consumption does not measure attribute-change extent\",\n        observation: { outcome: \"inconclusive\", evidence: \"consumed\", reason: \"insufficient-evidence\" }\n      };\n    }, () => {\n      const note = \"DECSACE was not attempted; this probe does not measure attribute-change extent\";\n      return Promise.resolve({\n        pass: false,\n        note,\n        observation: { outcome: \"inconclusive\", reason: \"insufficient-evidence\", evidence: \"none\", note }\n      });\n    }),",
    // parserStateResult helper
    "function parserStateResult(pass, expected, state, note) {\n  const response = JSON.stringify(state);\n  return {\n    pass: pass === true,\n    response,\n    ...note && { note },\n    observation: {\n      outcome: pass === null ? \"inconclusive\" : pass ? \"supported\" : \"unsupported\",\n      ...pass === null && { reason: \"insufficient-evidence\" },\n      evidence: \"parser-state\",\n      ...note && { note }\n    },\n    ...pass !== null && {\n      assertions: [{ kind: pass ? \"positive\" : \"negative\", expected, observed: response }]\n    }\n  };\n}",
    // unmeasuredCellResult helper
    "function unmeasuredCellResult(position, feature) {\n  return {\n    pass: false,\n    ...position && { response: `${position.row};${position.col}` },\n    note: position ? `Cursor response does not measure ${feature}` : `No cursor response after ${feature}`,\n    observation: {\n      outcome: \"inconclusive\",\n      reason: position ? \"insufficient-evidence\" : \"no-response\",\n      evidence: \"query\"\n    }\n  };\n}",
    // the retained previous layer's exports
    'export const ntildeSavedResetAdapter = Object.freeze({',
    'export function ntildeRunSavedResetTermless(probeId, ctx) {',
]);

/** The pinned seams, exposed so portable tests can build a synthetic layered source. */
export const attrRectSeams = pinnedAttrRectSeams;

/** Derive the attribute-rectangle grading extension from the saved-state/reset grading extension. */
export function adaptAttrRectCollector(savedResetSource) {
    const source = String(savedResetSource);
    for (const seam of pinnedAttrRectSeams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned attribute-rectangle probe seam changed; refusing derivation.');
    }
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local attribute-rectangle grading extension ${JSON.stringify(attrRectAdapterId)}.
// Derived from the saved-state/reset grading extension; probe definitions, policy
// gates and stock grading remain intact.
export const ntildeAttrRectAdapter = Object.freeze({
  id: ${JSON.stringify(attrRectAdapterId)},
  probeIds: ${JSON.stringify([...attrRectCaseIds])}
});
/** Execute one pinned termless probe over caller-served cells. */
export function ntildeRunAttrRectTermless(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.termless !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = probe.termless(ctx);
  return { probeId, ...result };
}
`;
}
