// Explicitly versioned collector extension for the selective-erasure qualification
// (erase.selective, DECSED CSI ? 2 J, and editing.decsera, DECSERA CSI 1;1;1;5 $ {). This is a twenty-third
// digest-checked derivation layered on top of the erase-character / saved-lines grading extension: the pinned bundle, the owned
// adapter and every earlier grading extension stay byte-for-byte unchanged, and each graded result carries this
// extension's identity so it can never be presented as stock output.
//
// The extension adds no probe of its own. It exposes the PINNED termless probe definitions of the two contracted
// cases so live, independently observed cells can be graded by the collector's own selectiveEraseResult criteria.
// Derivation refuses unless the retained erase-character / saved-lines extension still contains the exact pinned helper bodies
// (parserStateResult, selectiveEraseResult, unmeasuredCellResult) and both registrations, each verbatim and once. The
// registrations carry the terminal paths: those are cursor-query paths that never return a supported or unsupported
// verdict (unmeasuredCellResult is always inconclusive), so a later upstream change that gave one a verdict would be
// refused. editing.decsera's sibling variants and erase.screen.* are different cases and are not pinned here.
import { selectiveEraseCaseIds } from './selective-erase-contracts.mjs';
import { eraseCharsHistorySeams } from './erase-chars-history-grading-collector.mjs';

/** Identity of this twenty-third layered extension. */
export const selectiveEraseAdapterId = 'ntilde-owned-selective-erase-grading-v1';

/** The helper seams the erase-character / saved-lines layer already pins, reused verbatim so one source of truth names them. */
const parserStateSeam = eraseCharsHistorySeams.find(item => item.startsWith('function parserStateResult('));
if (parserStateSeam === undefined) throw new Error('Retained erase-character / saved-lines parserStateResult helper seam is missing.');

/** Exact pinned seams from terminfo.dev@4.0.0 (as retained by the erase-character / saved-lines extension). Each must appear exactly once:
 * the parserStateResult helper that shapes the verdict; selectiveEraseResult; unmeasuredCellResult (the terminal paths' only
 * return); the erase.selective registration (termless DECSED mode 2 and its cursor-query path); the editing.decsera
 * registration (termless DECSERA and its cursor-query path); then the retained previous layer's exports. */
const pinnedSelectiveEraseSeams = Object.freeze([
    parserStateSeam,
    // selectiveEraseResult helper
    "function selectiveEraseResult(ctx, sequence, rectangular) {\n  const expected = \"DECSCA-protected P survives while selective erase clears ABCD\";\n  if (!Number.isSafeInteger(ctx.cols) || ctx.cols < 6) {\n    return parserStateResult(null, expected, { cols: ctx.cols }, \"Need six measured columns\");\n  }\n  try {\n    ctx.feed('\\x1B[1;1H\\x1B[1\"qP\\x1B[0\"qABCDZ');\n    const before = Array.from({ length: 6 }, (_, col) => ctx.getCell(0, col).char);\n    if (before.join(\"\") !== \"PABCDZ\") {\n      return parserStateResult(null, expected, { before }, \"Selective erase seed was not measured\");\n    }\n    ctx.feed(sequence);\n    const after = Array.from({ length: 6 }, (_, col) => ctx.getCell(0, col).char);\n    const controlsValid = after[0] === \"P\" && (!rectangular || after[5] === \"Z\");\n    return parserStateResult(controlsValid ? after.slice(1, rectangular ? 5 : 6).every(isBlank) : null, expected, { before, after }, controlsValid ? undefined : \"Protection or outside-rectangle control failed; cannot attribute selective erase\");\n  } finally {\n    ctx.feed('\\x1B[0\"q');\n  }\n}",
    // unmeasuredCellResult helper (the terminal paths' only result)
    "function unmeasuredCellResult(position, feature) {\n  return {\n    pass: false,\n    ...position && { response: `${position.row};${position.col}` },\n    note: position ? `Cursor response does not measure ${feature}` : `No cursor response after ${feature}`,\n    observation: {\n      outcome: \"inconclusive\",\n      reason: position ? \"insufficient-evidence\" : \"no-response\",\n      evidence: \"query\"\n    }\n  };\n}",
    // erase.selective registration (termless criterion and its cursor-query terminal path)
    "      ...probe(\"erase.selective\", (ctx) => selectiveEraseResult(ctx, \"\\x1B[?2J\", false), async (ctx) => {\n        if (!Number.isSafeInteger(ctx.rows) || !Number.isSafeInteger(ctx.cols) || ctx.rows < 1 || ctx.cols < 6) {\n          return {\n            pass: false,\n            observation: {\n              outcome: \"inconclusive\",\n              reason: \"insufficient-evidence\",\n              evidence: \"none\",\n              note: `Erase fixture needs at least 1x6, measured ${ctx.rows}x${ctx.cols}`\n            }\n          };\n        }\n        ctx.write(\"\\x1B[1;1H\\x1B[2K\");\n        ctx.write(\"ABCDE\");\n        ctx.write(\"\\x1B[?2J\");\n        const pos = await ctx.queryCursorPosition();\n        return unmeasuredCellResult(pos, \"erased cells or scrollback\");\n      }),\n      termNeedsGeometry: true",
    // editing.decsera registration (termless criterion and its cursor-query terminal path)
    "      ...probe(\"editing.decsera\", (ctx) => selectiveEraseResult(ctx, \"\\x1B[1;1;1;5${\", true), async (ctx) => {\n        if (!Number.isSafeInteger(ctx.rows) || !Number.isSafeInteger(ctx.cols) || ctx.rows < 3 || ctx.cols < 5) {\n          return {\n            pass: false,\n            observation: {\n              outcome: \"inconclusive\",\n              reason: \"insufficient-evidence\",\n              evidence: \"none\",\n              note: `Editing fixture needs at least 3x5, measured ${ctx.rows}x${ctx.cols}`\n            }\n          };\n        }\n        ctx.write(\"\\x1B[1;1H\");\n        ctx.write(\"\\x1B[1;1;3;5${\");\n        const pos = await ctx.queryCursorPosition();\n        return unmeasuredCellResult(pos, \"edited cells or attributes\");\n      }),\n      termNeedsGeometry: true",
    // the retained previous layer's exports
    'export const ntildeEraseCharsHistoryAdapter = Object.freeze({',
    'export function ntildeRunEraseCharsHistoryTermless(probeId, ctx) {',
]);

/** The pinned seams, exposed so portable tests can build a synthetic layered source. */
export const selectiveEraseSeams = pinnedSelectiveEraseSeams;

/** Derive the selective-erasure grading extension from the erase-character / saved-lines grading extension. */
export function adaptSelectiveEraseCollector(eraseCharsHistorySource) {
    const source = String(eraseCharsHistorySource);
    for (const seam of pinnedSelectiveEraseSeams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned selective-erase probe seam changed; refusing derivation.');
    }
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local selective-erasure grading extension ${JSON.stringify(selectiveEraseAdapterId)}.
// Derived from the erase-character / saved-lines grading extension; probe definitions, policy
// gates and stock grading remain intact.
export const ntildeSelectiveEraseAdapter = Object.freeze({
  id: ${JSON.stringify(selectiveEraseAdapterId)},
  probeIds: ${JSON.stringify([...selectiveEraseCaseIds])}
});
/** Execute one pinned termless probe over caller-served cells. */
export function ntildeRunSelectiveEraseTermless(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.termless !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = probe.termless(ctx);
  return { probeId, ...result };
}
`;
}
