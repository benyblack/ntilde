// Explicitly versioned collector extension for the SGR underline-color
// qualification (indexed, RGB and reset). This is a ninth digest-checked derivation
// layered on top of the SGR-underline-style grading extension: the pinned bundle,
// the owned adapter and every earlier grading extension stay byte-for-byte
// unchanged, and each graded result carries this extension's identity so it can
// never be presented as stock output.
//
// The extension adds no probe of its own. It exposes the PINNED termless
// definitions of three cases (sgr.underline-color-indexed, sgr.underline-color-rgb
// and sgr.underline-color-reset) so live, independently observed cells can be graded
// by the collector's own criteria. The aggregate sgr.underline.color case shares the
// RGB probe body under a different id and is NOT part of this batch. Derivation
// refuses unless the retained SGR-underline extension still contains the exact
// pinned probe bodies (fixture bytes, criteria and notes), each verbatim and once.
import { sgrUnderlineColorCaseIds } from './sgr-underline-color-contracts.mjs';

/** Identity of this ninth layered extension. */
export const sgrUnderlineColorAdapterId = 'ntilde-owned-sgr-underline-color-grading-v1';

/** Exact pinned seams from terminfo.dev@4.0.0 (as retained by the SGR-underline
 * extension). Each must appear exactly once: three complete probe bodies, the
 * registrations, shared constants and helpers, and the previous layer's exports. */
const pinnedUnderlineColorSeams = Object.freeze([
    // The shared RGB probe function (also registered under the aggregate id, which this batch does not qualify).
    "function rgbUnderlineProbe(id) {\n  const sequence = \"\\x1B[4m\\x1B[58;2;255;0;128m\";\n  const original = sgrProbe(id, sequence, () => null);\n  return {\n    ...original,\n    termless(ctx) {\n      ctx.feed(\"\\x1B[4m\\x1B[59m\\x1B[38;2;0;0;255mA\\x1B[38;2;0;255;0mB\\x1B[58;2;255;0;128mX\");\n      const blueControl = ctx.getCell(0, 0);\n      const greenControl = ctx.getCell(0, 1);\n      const target = ctx.getCell(0, 2);\n      const state = { blueControl, greenControl, target };\n      const expected = `${id}: X has explicit rgb(255,0,128) underline distinct from the reset/default B cell`;\n      if (blueControl.char !== \"A\" || greenControl.char !== \"B\" || target.char !== \"X\" || !blueControl.underline || !greenControl.underline || !target.underline || target.underlineColor == null) {\n        return parserStateResult(null, expected, state, \"Underlined control, target, or target color was not exposed\");\n      }\n      if (greenControl.underlineColor && sameRgb(greenControl.underlineColor, requestedUnderlineColor)) {\n        return parserStateResult(null, expected, state, \"Default underline already matches the requested color\");\n      }\n      if (sameRgb(target.underlineColor, requestedUnderlineColor))\n        return parserStateResult(true, expected, state);\n      if (blueControl.fg && greenControl.fg && !sameRgb(blueControl.fg, greenControl.fg) && blueControl.underlineColor && greenControl.underlineColor && !sameRgb(blueControl.underlineColor, greenControl.underlineColor)) {\n        return parserStateResult(false, expected, state);\n      }\n      return parserStateResult(null, expected, state, \"Underline color readback was not calibrated for a negative\");\n    },\n    termlessObservationEvidence: \"parser-state\"\n  };\n}",
    '    rgbUnderlineProbe("sgr.underline-color-rgb"),',
    '  requestedUnderlineColor = { r: 255, g: 0, b: 128 };',
    // The indexed probe: fixture, criteria and notes.
    "      ...probe(\"sgr.underline-color-indexed\", (ctx) => {\n        ctx.feed(\"\\x1B[4;38;5;4m\\x1B[59mA\\x1B[58;5;5mX\\x1B[38;5;5m\\x1B[59mB\\x1B[58;5;4mY\");\n        const default4 = ctx.getCell(0, 0);\n        const target5 = ctx.getCell(0, 1);\n        const default5 = ctx.getCell(0, 2);\n        const target4 = ctx.getCell(0, 3);\n        const state = {\n          default4,\n          target5,\n          default5,\n          target4\n        };\n        const expected = \"SGR 58 index 5/4 underline colors match the opposite SGR 38 palette controls, not their same-foreground defaults\";\n        if (default4.char !== \"A\" || target5.char !== \"X\" || default5.char !== \"B\" || target4.char !== \"Y\" || !default4.underline || !target5.underline || !default5.underline || !target4.underline || !default4.fg || !default5.fg || target5.underlineColor == null || target4.underlineColor == null) {\n          return parserStateResult(null, expected, state, \"Palette control or underline cell state was not exposed\");\n        }\n        if (sameRgb(default4.fg, default5.fg)) {\n          return parserStateResult(null, expected, state, \"Palette controls resolve to indistinguishable colors\");\n        }\n        const matches = sameRgb(default5.fg, target5.underlineColor) && sameRgb(default4.fg, target4.underlineColor) && (!default4.underlineColor || !sameRgb(default4.underlineColor, target5.underlineColor)) && (!default5.underlineColor || !sameRgb(default5.underlineColor, target4.underlineColor));\n        if (matches)\n          return parserStateResult(true, expected, state);\n        if (default4.underlineColor && default5.underlineColor && !sameRgb(default4.underlineColor, default5.underlineColor)) {\n          return parserStateResult(false, expected, state);\n        }\n        return parserStateResult(null, expected, state, \"Underline color readback was not calibrated for a negative\");\n      }, (ctx) => consumedSgr(ctx, \"\\x1B[4m\\x1B[58;5;5m\"), \"consumed\"),",
    // The reset probe: fixture, criteria and notes.
    "      ...probe(\"sgr.underline-color-reset\", (ctx) => {\n        ctx.feed(\"\\x1B[4m\\x1B[59mC\\x1B[58;2;255;0;128mX\\x1B[59mY\");\n        const baseline = ctx.getCell(0, 0);\n        const before = ctx.getCell(0, 1);\n        const after = ctx.getCell(0, 2);\n        const state = { baseline, before, after };\n        const expected = \"X has explicit underline rgb(255,0,128); SGR 59 restores Y to the measured reset-state C color\";\n        if (baseline.char !== \"C\" || before.char !== \"X\" || after.char !== \"Y\" || !baseline.underline || !before.underline || !after.underline || baseline.underlineColor === undefined || before.underlineColor === undefined || after.underlineColor === undefined) {\n          return parserStateResult(null, expected, state, \"Reset control or underline cell state was not exposed\");\n        }\n        if (!before.underlineColor || !sameRgb(before.underlineColor, requestedUnderlineColor) || baseline.underlineColor && sameRgb(baseline.underlineColor, requestedUnderlineColor)) {\n          return parserStateResult(null, expected, state, \"Requested colored underline prerequisite was not observed\");\n        }\n        if (after.underlineColor && sameRgb(after.underlineColor, before.underlineColor)) {\n          return parserStateResult(false, expected, state);\n        }\n        if (baseline.underlineColor === null || after.underlineColor === null) {\n          if (baseline.underlineColor === after.underlineColor)\n            return parserStateResult(true, expected, state);\n          return parserStateResult(null, expected, state, \"Reset-state color readback changed representation\");\n        }\n        return parserStateResult(sameRgb(baseline.underlineColor, after.underlineColor), expected, state);\n      }, (ctx) => consumedSgr(ctx, \"\\x1B[4m\\x1B[58;2;255;0;128m\\x1B[59m\"), \"consumed\"),",
    'function sameRgb(a, b) {',
    'function parserStateResult(pass, expected, state, note) {',
    'outcome: pass === null ? "inconclusive" : pass ? "supported" : "unsupported",',
    'function sgrProbe(id, sequence, check, noObservableWhen) {',
    // the retained previous layer's exports
    'export const ntildeSgrUnderlineAdapter = Object.freeze({',
    'export function ntildeRunSgrUnderlineTermless(probeId, ctx) {',
]);

/** The pinned seams, exposed so portable tests can build a synthetic layered source. */
export const sgrUnderlineColorSeams = pinnedUnderlineColorSeams;

/** Derive the SGR-underline-color grading extension from the SGR-underline grading extension. */
export function adaptSgrUnderlineColorCollector(sgrUnderlineSource) {
    const source = String(sgrUnderlineSource);
    for (const seam of pinnedUnderlineColorSeams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned SGR-underline-color probe seam changed; refusing derivation.');
    }
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local SGR-underline-color grading extension ${JSON.stringify(sgrUnderlineColorAdapterId)}.
// Derived from the SGR-underline grading extension; probe definitions, policy
// gates and stock grading remain intact.
export const ntildeSgrUnderlineColorAdapter = Object.freeze({
  id: ${JSON.stringify(sgrUnderlineColorAdapterId)},
  probeIds: ${JSON.stringify([...sgrUnderlineColorCaseIds])}
});
/** Execute one pinned termless probe over caller-served cells. */
export function ntildeRunSgrUnderlineColorTermless(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.termless !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = probe.termless(ctx);
  return { probeId, ...result };
}
`;
}
