// Explicitly versioned collector extension for the xterm pointer-color qualification (extensions.osc113-reset-pointer-fg, extensions.osc114-reset-pointer-bg). A digest-checked derivation layered on the
// left/right-margin grading extension, a sibling of the device-query, OSC 133, feature-reporting and rxvt special-color layers: the pinned bundle, the owned adapter and every earlier grading extension
// stay byte-for-byte unchanged, and each graded result carries this extension's identity so it can never be presented as stock output.
//
// The extension adds no probe of its own. It exposes the PINNED probe definitions so both paths can be replayed over the parent-journaled bytes of their own fenced exchange: the termless function
// (colorResetProbe(13, 113) / (14, 114): feedCapture of the OSC 13 / OSC 14 query and oscReplyResult over the captured text) and the terminal function (the CUP/EL and OSC 113 / 114 writes, a cursor query
// and unverifiedEffect). The derivation refuses unless the retained left/right-margin extension still contains each pinned seam verbatim and exactly once.

/** Identity of this layered extension. */
export const pclAdapterId = 'ntilde-owned-pointer-color-grading-v1';

/** The exact pinned bytes of one retained bundle seam: both registrations. */
export const seamRegistration = "    probe(\"extensions.osc113-reset-pointer-fg\", colorResetProbe(13, 113), async (ctx) => {\n      ctx.write(\"\\x1B[1;1H\\x1B[2K\");\n      ctx.write(\"\\x1B]113\\x07\");\n      const pos = await ctx.queryCursorPosition();\n      return unverifiedEffect(pos ? \"Cursor answered; pointer foreground restoration was not measured\" : \"No cursor response after OSC 113\");\n    }),\n    probe(\"extensions.osc114-reset-pointer-bg\", colorResetProbe(14, 114), async (ctx) => {\n      ctx.write(\"\\x1B[1;1H\\x1B[2K\");\n      ctx.write(\"\\x1B]114\\x07\");\n      const pos = await ctx.queryCursorPosition();\n      return unverifiedEffect(pos ? \"Cursor answered; pointer background restoration was not measured\" : \"No cursor response after OSC 114\");\n    }),\n";
/** The exact pinned bytes of one retained bundle seam. */
export const seamProbe = "function probe(id, termless, term, termObservationEvidence) {\n  return { id, termless, term, ...termObservationEvidence && { termObservationEvidence } };\n}\n";
/** The exact pinned bytes of one retained bundle seam. */
export const seamReplyResult = "function oscReplyResult(raw, frame, expected, prefix) {\n  if (frame) {\n    return {\n      pass: true,\n      response: raw,\n      observation: { outcome: \"supported\", evidence: \"query\" },\n      assertions: [{ kind: \"positive\", expected, observed: frame }]\n    };\n  }\n  return {\n    pass: false,\n    response: raw,\n    observation: {\n      outcome: \"inconclusive\",\n      reason: prefix.test(raw) ? \"invalid-reply\" : \"no-response\",\n      evidence: \"query\"\n    }\n  };\n}\n";
/** The exact pinned bytes of one retained bundle seam. */
export const seamUnverified = "function unverifiedEffect(note, response) {\n  return {\n    pass: false,\n    ...response !== undefined && { response },\n    note,\n    observation: { outcome: \"inconclusive\", reason: \"insufficient-evidence\", evidence: \"consumed\", note }\n  };\n}\n";
/** The exact pinned bytes of one retained bundle seam. */
export const seamColorReset = "function colorResetProbe(setCode, resetCode, index) {\n  const indexPart = index === undefined ? \"\" : `${index};`;\n  const query = `\\x1B]${setCode};${indexPart}?\\x07`;\n  const replyPattern = new RegExp(`\\\\x1b\\\\]${setCode};${indexPart}(rgb:[0-9a-f]{1,4}/[0-9a-f]{1,4}/[0-9a-f]{1,4})(?:\\\\x07|\\\\x1b\\\\\\\\)`, \"i\");\n  return (ctx) => {\n    const originalRaw = ctx.feedCapture(query);\n    const original = replyPattern.exec(originalRaw)?.[1];\n    if (!original) {\n      return oscReplyResult(originalRaw, null, `OSC ${setCode} color before reset`, new RegExp(`\\\\x1b\\\\]${setCode};`));\n    }\n    const requested = probeForeground(original);\n    let mustReset = false;\n    try {\n      ctx.feed(`\\x1B]${setCode};${indexPart}${requested}\\x07`);\n      mustReset = true;\n      const changedRaw = ctx.feedCapture(query);\n      const changed = replyPattern.exec(changedRaw)?.[1];\n      ctx.feed(`\\x1B]${resetCode}${index === undefined ? \"\" : `;${index}`}\\x07`);\n      mustReset = false;\n      const restoredRaw = ctx.feedCapture(query);\n      const restored = replyPattern.exec(restoredRaw)?.[1];\n      const response = JSON.stringify({ originalRaw, changedRaw, restoredRaw });\n      if (!changed || !restored || !sameRgb2(changed, requested)) {\n        return {\n          pass: false,\n          response,\n          observation: {\n            outcome: \"inconclusive\",\n            reason: \"insufficient-evidence\",\n            evidence: \"query\",\n            note: \"Color mutation or readback control was not established\"\n          }\n        };\n      }\n      const pass = sameRgb2(restored, original);\n      return {\n        pass,\n        response,\n        observation: { outcome: pass ? \"supported\" : \"unsupported\", evidence: \"behavior\" },\n        assertions: [\n          {\n            kind: pass ? \"positive\" : \"negative\",\n            expected: `OSC ${resetCode} restores the prior OSC ${setCode} color after a verified change`,\n            observed: response\n          }\n        ]\n      };\n    } finally {\n      if (mustReset)\n        ctx.feed(`\\x1B]${resetCode}${index === undefined ? \"\" : `;${index}`}\\x07`);\n    }\n  };\n}\n";
/** The retained previous layer's two export lines, pinned verbatim. */
export const seamLrMarginsAdapterLine = "export const ntildeLrMarginsAdapter = Object.freeze({";
export const seamLrMarginsTermlessLine = "export function ntildeRunLrMarginsTermless(probeId, ctx) {";

/** The pinned seams, exposed so portable tests can build a synthetic layered source. */
export const pclSeams = Object.freeze([seamRegistration, seamProbe, seamReplyResult, seamUnverified, seamColorReset, seamLrMarginsAdapterLine, seamLrMarginsTermlessLine]);

/** Derive the pointer-color grading extension from the left/right-margin grading extension. */
export function adaptPclCollector(lrMarginsSource) {
    const source = String(lrMarginsSource);
    for (const seam of pclSeams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned pointer-color probe seam changed; refusing derivation.');
    }
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local xterm pointer-color grading extension "ntilde-owned-pointer-color-grading-v1".
// Derived from the left/right-margin grading extension; probe definitions, policy
// gates and stock grading remain intact.
export const ntildePclAdapter = Object.freeze({
  id: "ntilde-owned-pointer-color-grading-v1",
  probeIds: ["extensions.osc113-reset-pointer-fg","extensions.osc114-reset-pointer-bg"]
});
/** Execute one pinned termless probe over a caller-served terminal context. */
export function ntildeRunPclTermless(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.termless !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = probe.termless(ctx);
  return { probeId, ...result };
}
/** Execute one pinned terminal-path probe over a caller-served query/cursor context. */
export async function ntildeRunPclTerminal(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.term !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = await probe.term(ctx);
  return { probeId, ...result };
}
`;
}
