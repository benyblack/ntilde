// Explicitly versioned collector extension for the runtime-color reset qualification (extensions.osc104-reset-palette,
// extensions.osc110-reset-fg, extensions.osc111-reset-bg, extensions.osc112-reset-cursor). This is a twenty-eighth digest-checked
// derivation layered on top of the left/right-margin grading extension: the pinned bundle, the owned adapter and every earlier grading
// extension stay byte-for-byte unchanged, and each graded result carries this extension's identity so it can never be presented as
// stock output.
//
// The extension adds no probe of its own. It exposes the PINNED termless probe definitions of the contracted cases so live, independently
// journaled replies can be graded by the collector's own criterion. Derivation refuses unless the retained left/right-margin extension still
// contains the four pinned registrations (termless and terminal functions) and the helper bodies they run (colorResetProbe, probeForeground,
// sameRgb2, oscReplyResult and unmeasuredStateEffect), each verbatim and once. The pinned terminal path of every case is
// unmeasuredStateEffect and is inconclusive by design; it is preserved, never substituted. No other case is pinned here.
import { colorResetCaseIds } from './color-reset-contracts.mjs';

/** Identity of this twenty-eighth layered extension. */
export const colorResetAdapterId = 'ntilde-owned-runtime-color-reset-grading-v1';

const registration = (id, setCode, resetCode, index, label) => `    probe("${id}", colorResetProbe(${setCode}, ${resetCode}${index === undefined ? '' : ', ' + index}), () => Promise.resolve(unmeasuredStateEffect("${label}"))),`;

/** Exact pinned seams from terminfo.dev@4.0.0 (as retained by the left/right-margin extension). Each must appear exactly once. */
const pinnedColorResetSeams = Object.freeze([
    registration('extensions.osc104-reset-palette', 4, 104, 0, 'OSC 104 palette reset'),
    registration('extensions.osc110-reset-fg', 10, 110, undefined, 'OSC 110 foreground reset'),
    registration('extensions.osc111-reset-bg', 11, 111, undefined, 'OSC 111 background reset'),
    registration('extensions.osc112-reset-cursor', 12, 112, undefined, 'OSC 112 cursor-color reset'),
    // colorResetProbe: the complete function, verbatim
    "function colorResetProbe(setCode, resetCode, index) {\n  const indexPart = index === undefined ? \"\" : `${index};`;\n  const query = `\\x1B]${setCode};${indexPart}?\\x07`;\n  const replyPattern = new RegExp(`\\\\x1b\\\\]${setCode};${indexPart}(rgb:[0-9a-f]{1,4}/[0-9a-f]{1,4}/[0-9a-f]{1,4})(?:\\\\x07|\\\\x1b\\\\\\\\)`, \"i\");\n  return (ctx) => {\n    const originalRaw = ctx.feedCapture(query);\n    const original = replyPattern.exec(originalRaw)?.[1];\n    if (!original) {\n      return oscReplyResult(originalRaw, null, `OSC ${setCode} color before reset`, new RegExp(`\\\\x1b\\\\]${setCode};`));\n    }\n    const requested = probeForeground(original);\n    let mustReset = false;\n    try {\n      ctx.feed(`\\x1B]${setCode};${indexPart}${requested}\\x07`);\n      mustReset = true;\n      const changedRaw = ctx.feedCapture(query);\n      const changed = replyPattern.exec(changedRaw)?.[1];\n      ctx.feed(`\\x1B]${resetCode}${index === undefined ? \"\" : `;${index}`}\\x07`);\n      mustReset = false;\n      const restoredRaw = ctx.feedCapture(query);\n      const restored = replyPattern.exec(restoredRaw)?.[1];\n      const response = JSON.stringify({ originalRaw, changedRaw, restoredRaw });\n      if (!changed || !restored || !sameRgb2(changed, requested)) {\n        return {\n          pass: false,\n          response,\n          observation: {\n            outcome: \"inconclusive\",\n            reason: \"insufficient-evidence\",\n            evidence: \"query\",\n            note: \"Color mutation or readback control was not established\"\n          }\n        };\n      }\n      const pass = sameRgb2(restored, original);\n      return {\n        pass,\n        response,\n        observation: { outcome: pass ? \"supported\" : \"unsupported\", evidence: \"behavior\" },\n        assertions: [\n          {\n            kind: pass ? \"positive\" : \"negative\",\n            expected: `OSC ${resetCode} restores the prior OSC ${setCode} color after a verified change`,\n            observed: response\n          }\n        ]\n      };\n    } finally {\n      if (mustReset)\n        ctx.feed(`\\x1B]${resetCode}${index === undefined ? \"\" : `;${index}`}\\x07`);\n    }\n  };\n}",
    "function sameRgb2(left, right) {\n  const channels = (color) => color.slice(4).split(\"/\").map((channel) => Math.round(parseInt(channel, 16) / (16 ** channel.length - 1) * 65535));\n  const actual = channels(left);\n  const expected = channels(right);\n  return actual.every((channel, index) => channel === expected[index]);\n}",
    "function probeForeground(before) {\n  return sameRgb2(before, \"rgb:aa/bb/cc\") ? \"rgb:12/34/56\" : \"rgb:aa/bb/cc\";\n}",
    "function oscReplyResult(raw, frame, expected, prefix) {\n  if (frame) {\n    return {\n      pass: true,\n      response: raw,\n      observation: { outcome: \"supported\", evidence: \"query\" },\n      assertions: [{ kind: \"positive\", expected, observed: frame }]\n    };\n  }\n  return {\n    pass: false,\n    response: raw,\n    observation: {\n      outcome: \"inconclusive\",\n      reason: prefix.test(raw) ? \"invalid-reply\" : \"no-response\",\n      evidence: \"query\"\n    }\n  };\n}",
    "function unmeasuredStateEffect(feature) {\n  const note = `${feature} was not attempted; this probe has no effect readback or prior-state restoration`;\n  return {\n    pass: false,\n    note,\n    observation: { outcome: \"inconclusive\", reason: \"insufficient-evidence\", evidence: \"none\", note }\n  };\n}",
    // the retained previous layer's exports
    'export const ntildeLrMarginsAdapter = Object.freeze({',
    'export function ntildeRunLrMarginsTermless(probeId, ctx) {',
]);

/** The pinned seams, exposed so portable tests can build a synthetic layered source. */
export const colorResetSeams = pinnedColorResetSeams;

/** Derive the runtime-color reset grading extension from the left/right-margin grading extension. */
export function adaptColorResetCollector(lrMarginsSource) {
    const source = String(lrMarginsSource);
    for (const seam of pinnedColorResetSeams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned runtime-color reset probe seam changed; refusing derivation.');
    }
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local runtime-color reset grading extension ${JSON.stringify(colorResetAdapterId)}.
// Derived from the left/right-margin grading extension; probe definitions, policy
// gates and stock grading remain intact.
export const ntildeColorResetAdapter = Object.freeze({
  id: ${JSON.stringify(colorResetAdapterId)},
  probeIds: ${JSON.stringify([...colorResetCaseIds])}
});
/** Execute one pinned termless probe over caller-served replies. */
export function ntildeRunColorResetTermless(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.termless !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = probe.termless(ctx);
  return { probeId, ...result };
}
`;
}
