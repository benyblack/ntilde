// Explicitly versioned collector extension for the xterm saved-mode qualification (modes.xtsave and modes.xtrestore). This is a digest-checked derivation layered on top of the left/right-margin
// grading extension, a sibling of the device-query, OSC 9, notification, OSC 7/8, title/icon-stack and OSC 133 layers: the pinned bundle, the owned adapter and every earlier grading extension stay
// byte-for-byte unchanged, and each graded result carries this extension's identity so it can never be presented as stock output.
//
// The extension adds no probe of its own. It exposes the PINNED probe definitions so both of their paths can be replayed over the parent-journaled bytes of their own fenced exchange: the termless
// function (feeds CSI ? 7 s / CSI ? 7 r through feed and feedCapture, with a DA1 capture) and the terminal function (writes the same bytes and asks for a cursor position). The derivation refuses unless the
// retained left/right-margin extension still contains the two exact registrations and its own exports, each verbatim and once.
import { xsvCaseIds } from './xsv-contracts.mjs';

/** Identity of this layered extension. */
export const xsvAdapterId = 'ntilde-owned-xterm-saved-modes-grading-v1';

/** Exact pinned seams from terminfo.dev@4.0.0 (as retained by the left/right-margin extension). Each must appear exactly once: the two probe registrations, then the retained previous layer's exports. */
const pinnedXsvSeams = Object.freeze([
    // modes.xtsave registration
    "    probe(\"modes.xtsave\", (ctx) => {\n      const saveOut = ctx.feedCapture(\"\\x1B[?7s\");\n      if (saveOut.length > 0) {\n        return {\n          pass: false,\n          response: saveOut,\n          observation: {\n            outcome: \"inconclusive\",\n            reason: \"invalid-reply\",\n            evidence: \"query\",\n            note: \"Unexpected output during stack operation\"\n          }\n        };\n      }\n      const probeResponse = ctx.feedCapture(\"\\x1B[c\");\n      ctx.feed(\"\\x1B[?7r\");\n      return {\n        pass: false,\n        response: probeResponse,\n        observation: {\n          outcome: \"inconclusive\",\n          reason: \"insufficient-evidence\",\n          evidence: \"consumed\",\n          note: \"DA1 responsiveness does not measure saved/restored stack state\"\n        }\n      };\n    }, async (ctx) => {\n      ctx.write(\"\\x1B[?7s\");\n      try {\n        const pos = await ctx.queryCursorPosition();\n        if (!pos) {\n          return { pass: false, observation: { outcome: \"inconclusive\", reason: \"no-response\", evidence: \"query\" } };\n        }\n        return {\n          pass: false,\n          response: JSON.stringify(pos),\n          observation: {\n            outcome: \"inconclusive\",\n            reason: \"insufficient-evidence\",\n            evidence: \"consumed\",\n            note: \"Cursor responsiveness does not measure saved/restored state\"\n          }\n        };\n      } finally {\n        ctx.write(\"\\x1B[?7r\");\n      }\n    }),\n",
    // modes.xtrestore registration
    "    probe(\"modes.xtrestore\", (ctx) => {\n      ctx.feed(\"\\x1B[?7s\");\n      const restoreOut = ctx.feedCapture(\"\\x1B[?7r\");\n      if (restoreOut.length > 0) {\n        return {\n          pass: false,\n          response: restoreOut,\n          observation: {\n            outcome: \"inconclusive\",\n            reason: \"invalid-reply\",\n            evidence: \"query\",\n            note: \"Unexpected output during stack operation\"\n          }\n        };\n      }\n      const probeResponse = ctx.feedCapture(\"\\x1B[c\");\n      return {\n        pass: false,\n        response: probeResponse,\n        observation: {\n          outcome: \"inconclusive\",\n          reason: \"insufficient-evidence\",\n          evidence: \"consumed\",\n          note: \"DA1 responsiveness does not measure saved/restored stack state\"\n        }\n      };\n    }, async (ctx) => {\n      ctx.write(\"\\x1B[?7s\");\n      ctx.write(\"\\x1B[?7r\");\n      const pos = await ctx.queryCursorPosition();\n      if (!pos) {\n        return { pass: false, observation: { outcome: \"inconclusive\", reason: \"no-response\", evidence: \"query\" } };\n      }\n      return {\n        pass: false,\n        response: JSON.stringify(pos),\n        observation: {\n          outcome: \"inconclusive\",\n          reason: \"insufficient-evidence\",\n          evidence: \"consumed\",\n          note: \"Cursor responsiveness does not measure saved/restored state\"\n        }\n      };\n    }),\n",
    // the retained previous layer's exports
    'export const ntildeLrMarginsAdapter = Object.freeze({',
    'export function ntildeRunLrMarginsTermless(probeId, ctx) {',
]);

/** The pinned seams, exposed so portable tests can build a synthetic layered source. */
export const xsvSeams = pinnedXsvSeams;

/** Derive the xterm saved-mode grading extension from the left/right-margin grading extension. */
export function adaptXsvCollector(lrMarginsSource) {
    const source = String(lrMarginsSource);
    for (const seam of pinnedXsvSeams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned xterm saved-mode probe seam changed; refusing derivation.');
    }
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local xterm saved-mode grading extension ${JSON.stringify(xsvAdapterId)}.
// Derived from the left/right-margin grading extension; probe definitions, policy
// gates and stock grading remain intact.
export const ntildeXsvAdapter = Object.freeze({
  id: ${JSON.stringify(xsvAdapterId)},
  probeIds: ${JSON.stringify([...xsvCaseIds])}
});
/** Execute one pinned termless probe over a caller-served terminal context. */
export function ntildeRunXsvTermless(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.termless !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = probe.termless(ctx);
  return { probeId, ...result };
}
/** Execute one pinned terminal-path probe over a caller-served write/cursor-position context. */
export async function ntildeRunXsvTerminal(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.term !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = await probe.term(ctx);
  return { probeId, ...result };
}
`;
}
