// Explicitly versioned collector extension for the erase-with-attributes / erase-with-margins qualification
// (erase.el-with-attrs and erase.ed-scroll-region). This is a nineteenth digest-checked derivation layered on top of the
// saved-state / reset grading extension: the pinned bundle, the owned adapter and every earlier grading extension stay
// byte-for-byte unchanged, and each graded result carries this extension's identity so it can never be presented as stock output.
//
// The extension adds no probe of its own. It exposes the PINNED termless probe definitions of the two contracted cases so
// live, independently observed cells can be graded by the collector's own criteria. Derivation refuses unless the retained
// saved-state / reset extension still contains the exact pinned registrations (each with its cursor-query terminal path, which
// never carries a supported or unsupported verdict) and the helper bodies they use (isBlank, rowCells, parserStateResult,
// unmeasuredCellResult), each verbatim and once. The other erase cases (erase.line.*, erase.screen.*, erase.character,
// erase.selective) are different cases and are deliberately not seams here.
import { eraseAttrsRegionCaseIds } from './erase-attrs-region-contracts.mjs';
import { eraseLineSeams } from './erase-line-grading-collector.mjs';
import { charEditSeams } from './char-edit-grading-collector.mjs';

/** Identity of this nineteenth layered extension. */
export const eraseAttrsRegionAdapterId = 'ntilde-owned-erase-attrs-region-grading-v1';

/** A helper body an earlier layer already pins, reused verbatim so one source of truth names it. */
const helperSeam = (seams, start) => {
    const seam = seams.find(item => item.startsWith(start));
    if (seam === undefined) throw new Error('Retained helper seam is missing: ' + start);
    return seam;
};

/** Exact pinned seams from terminfo.dev@4.0.0 (as retained by the saved-state / reset extension). Each must appear exactly once, in this
 * order: the erase.el-with-attrs registration; the erase.ed-scroll-region registration; isBlank, rowCells, parserStateResult and
 * unmeasuredCellResult; then the retained previous layer's exports. */
const pinnedEraseAttrsRegionSeams = Object.freeze([
    "      ...probe(\"erase.el-with-attrs\", (ctx) => {\n        const expected = \"EL erases X while preserving its measured non-default background\";\n        try {\n          ctx.feed(\"\\x1B[42m\\x1B[1;1HXXXXX\\x1B[1;1H\");\n          const before = ctx.getCell(0, 0);\n          if (before.char !== \"X\" || before.bg == null) {\n            return parserStateResult(null, expected, { before }, \"Colored X setup was not measured\");\n          }\n          ctx.feed(\"\\x1B[K\");\n          const after = ctx.getCell(0, 0);\n          const measured = typeof after.char === \"string\" && after.bg != null;\n          return parserStateResult(measured ? isBlank(after.char) && after.bg?.r === before.bg.r && after.bg?.g === before.bg.g && after.bg?.b === before.bg.b : null, expected, { before, after }, measured ? undefined : \"Erased cell background metadata unavailable\");\n        } finally {\n          ctx.feed(\"\\x1B[0m\");\n        }\n      }, async (ctx) => {\n        if (!Number.isSafeInteger(ctx.rows) || !Number.isSafeInteger(ctx.cols) || ctx.rows < 1 || ctx.cols < 6) {\n          return {\n            pass: false,\n            observation: {\n              outcome: \"inconclusive\",\n              reason: \"insufficient-evidence\",\n              evidence: \"none\",\n              note: `Erase fixture needs at least 1x6, measured ${ctx.rows}x${ctx.cols}`\n            }\n          };\n        }\n        ctx.write(\"\\x1B[1;1H\\x1B[2K\");\n        try {\n          ctx.write(\"\\x1B[42m\");\n          ctx.write(\"XXXXX\");\n          ctx.write(\"\\x1B[1;1H\");\n          ctx.write(\"\\x1B[K\");\n          const pos = await ctx.queryCursorPosition();\n          return unmeasuredCellResult(pos, \"erased cells or scrollback\");\n        } finally {\n          ctx.write(\"\\x1B[0m\");\n        }\n      }),\n      termNeedsGeometry: true",
    "      ...probe(\"erase.ed-scroll-region\", (ctx) => {\n        const expected = \"ED0 erases measured cells below the cursor and preserves the preceding row\";\n        const rows = ctx.getScrollback().screenLines;\n        if (!Number.isSafeInteger(rows) || rows < 10 || !Number.isSafeInteger(ctx.cols) || ctx.cols < 10) {\n          return parserStateResult(null, expected, { rows, cols: ctx.cols }, \"Need a measured 10x10 fixture\");\n        }\n        try {\n          ctx.feed(\"\\x1B[1;1HKEEP!\\x1B[3;1HERASE\\x1B[3;10r\\x1B[3;1H\");\n          const before = { control: rowCells(ctx, 0), target: rowCells(ctx, 2), cursor: ctx.getCursor() };\n          if (before.control.join(\"\") !== \"KEEP!\" || before.target.join(\"\") !== \"ERASE\" || before.cursor.x !== 0 || before.cursor.y !== 2) {\n            return parserStateResult(null, expected, { before }, \"Erase region seed or cursor setup failed\");\n          }\n          ctx.feed(\"\\x1B[J\");\n          const after = { control: rowCells(ctx, 0), target: rowCells(ctx, 2) };\n          const valid = after.control.join(\"\") === \"KEEP!\" && after.target.every((char) => char !== null);\n          return parserStateResult(valid ? after.target.every((char) => char !== null && isBlank(char)) : null, expected, { before, after }, valid ? undefined : \"Preceding row or cell metadata was not preserved\");\n        } finally {\n          ctx.feed(\"\\x1B[r\");\n        }\n      }, async (ctx) => {\n        if (!Number.isSafeInteger(ctx.rows) || !Number.isSafeInteger(ctx.cols) || ctx.rows < 10 || ctx.cols < 10) {\n          return {\n            pass: false,\n            observation: {\n              outcome: \"inconclusive\",\n              reason: \"insufficient-evidence\",\n              evidence: \"none\",\n              note: `Erase fixture needs at least 10x10, measured ${ctx.rows}x${ctx.cols}`\n            }\n          };\n        }\n        ctx.write(\"\\x1B[2J\\x1B[H\");\n        ctx.write(`KEEP_THIS\\r\n`);\n        for (let i = 1;i <= 5; i++)\n          ctx.write(`row${i}\\r\n`);\n        try {\n          ctx.write(\"\\x1B[3;10r\");\n          ctx.write(\"\\x1B[3;1H\");\n          ctx.write(\"\\x1B[J\");\n          const pos = await ctx.queryCursorPosition();\n          return unmeasuredCellResult(pos, \"erased cells or scrollback\");\n        } finally {\n          ctx.write(\"\\x1B[r\");\n        }\n      }),\n      termNeedsGeometry: true",
    helperSeam(eraseLineSeams, 'function isBlank('),
    helperSeam(eraseLineSeams, 'function rowCells('),
    helperSeam(charEditSeams, 'function parserStateResult('),
    helperSeam(charEditSeams, 'function unmeasuredCellResult('),
    // the retained previous layer's exports
    'export const ntildeSavedResetAdapter = Object.freeze({',
    'export function ntildeRunSavedResetTermless(probeId, ctx) {',
]);

/** The pinned seams, exposed so portable tests can build a synthetic layered source. */
export const eraseAttrsRegionSeams = pinnedEraseAttrsRegionSeams;

/** Derive the erase-with-attributes / erase-with-margins grading extension from the saved-state / reset grading extension. */
export function adaptEraseAttrsRegionCollector(savedResetSource) {
    const source = String(savedResetSource);
    for (const seam of pinnedEraseAttrsRegionSeams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned erase-attributes probe seam changed; refusing derivation.');
    }
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local erase-attributes / erase-margins grading extension ${JSON.stringify(eraseAttrsRegionAdapterId)}.
// Derived from the saved-state / reset grading extension; probe definitions, policy
// gates and stock grading remain intact.
export const ntildeEraseAttrsRegionAdapter = Object.freeze({
  id: ${JSON.stringify(eraseAttrsRegionAdapterId)},
  probeIds: ${JSON.stringify([...eraseAttrsRegionCaseIds])}
});
/** Execute one pinned termless probe over caller-served cells. */
export function ntildeRunEraseAttrsRegionTermless(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.termless !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = probe.termless(ctx);
  return { probeId, ...result };
}
`;
}
