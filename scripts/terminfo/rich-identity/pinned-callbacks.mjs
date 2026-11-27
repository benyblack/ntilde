// Extracted from the digest-pinned terminfo.dev@4.0.0 bundle. Verified byte for byte during replay.
function probe(id, termless, term, termObservationEvidence) {
  return { id, termless, term, ...termObservationEvidence && { termObservationEvidence } };
}
function unmeasuredCellResult(position, feature) {
  return {
    pass: false,
    ...position && { response: `${position.row};${position.col}` },
    note: position ? `Cursor response does not measure ${feature}` : `No cursor response after ${feature}`,
    observation: {
      outcome: "inconclusive",
      reason: position ? "insufficient-evidence" : "no-response",
      evidence: "query"
    }
  };
}
export const pinned176 = probe("extensions.osc176-app-id", null, async (ctx) => {
      ctx.write("\x1B[1;1H\x1B[2K");
      ctx.write("\x1B]176;terminfo-test\x07");
      const pos = await ctx.queryCursorPosition();
      return unmeasuredCellResult(pos, "OSC 176 Wayland app-id");
    });
export const pinned666 = probe("extensions.osc666-termprop", null, async (ctx) => {
      ctx.write("\x1B[1;1H\x1B[2K");
      ctx.write("\x1B]666;test-prop=value\x07");
      const pos = await ctx.queryCursorPosition();
      return unmeasuredCellResult(pos, "OSC 666 terminal property");
    });
