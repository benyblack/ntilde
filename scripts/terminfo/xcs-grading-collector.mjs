// Explicitly versioned collector extension for the xterm color-stack qualification (modes.xtpushcolors and modes.xtpopcolors). This is a digest-checked derivation layered on top of the left/right-margin
// grading extension, a sibling of the device-query, OSC 9, notification, OSC 7/8, title/icon-stack and OSC 133 layers: the pinned bundle, the owned adapter and every earlier grading extension stay
// byte-for-byte unchanged, and each graded result carries this extension's identity so it can never be presented as stock output.
//
// The extension adds no probe of its own. It exposes the PINNED probe definitions so both of their paths can be replayed over the parent-journaled bytes of their own fenced exchange: the termless
// function (feeds CSI # P / CSI # Q through feed and feedCapture, with a DA1 capture) and the terminal function (writes the same bytes and asks for a cursor position). The derivation refuses unless the
// retained left/right-margin extension still contains the two exact registrations and its own exports, each verbatim and once.
import { xcsCaseIds } from './xcs-contracts.mjs';

/** Identity of this layered extension. */
export const xcsAdapterId = 'ntilde-owned-xterm-color-stack-grading-v1';

/** Exact pinned seams from terminfo.dev@4.0.0 (as retained by the left/right-margin extension). Each must appear exactly once: the two probe registrations, then the retained previous layer's exports. */
const pinnedXcsSeams = Object.freeze([
    // modes.xtpushcolors registration
    "    probe(\"modes.xtpushcolors\", (ctx) => {\n      const pushOut = ctx.feedCapture(\"\\x1B[#P\");\n      if (pushOut.length > 0) {\n        return {\n          pass: false,\n          response: pushOut,\n          observation: {\n            outcome: \"inconclusive\",\n            reason: \"invalid-reply\",\n            evidence: \"query\",\n            note: \"Unexpected output during stack operation\"\n          }\n        };\n      }\n      const probeResponse = ctx.feedCapture(\"\\x1B[c\");\n      ctx.feed(\"\\x1B[#Q\");\n      return {\n        pass: false,\n        response: probeResponse,\n        observation: {\n          outcome: \"inconclusive\",\n          reason: \"insufficient-evidence\",\n          evidence: \"consumed\",\n          note: \"DA1 responsiveness does not measure saved/restored stack state\"\n        }\n      };\n    }, async (ctx) => {\n      ctx.write(\"\\x1B[#P\");\n      try {\n        const pos = await ctx.queryCursorPosition();\n        if (!pos) {\n          return { pass: false, observation: { outcome: \"inconclusive\", reason: \"no-response\", evidence: \"query\" } };\n        }\n        return {\n          pass: false,\n          response: JSON.stringify(pos),\n          observation: {\n            outcome: \"inconclusive\",\n            reason: \"insufficient-evidence\",\n            evidence: \"consumed\",\n            note: \"Cursor responsiveness does not measure saved/restored state\"\n          }\n        };\n      } finally {\n        ctx.write(\"\\x1B[#Q\");\n      }\n    }),\n",
    // modes.xtpopcolors registration
    "    probe(\"modes.xtpopcolors\", (ctx) => {\n      ctx.feed(\"\\x1B[#P\");\n      const popOut = ctx.feedCapture(\"\\x1B[#Q\");\n      if (popOut.length > 0) {\n        return {\n          pass: false,\n          response: popOut,\n          observation: {\n            outcome: \"inconclusive\",\n            reason: \"invalid-reply\",\n            evidence: \"query\",\n            note: \"Unexpected output during stack operation\"\n          }\n        };\n      }\n      const probeResponse = ctx.feedCapture(\"\\x1B[c\");\n      return {\n        pass: false,\n        response: probeResponse,\n        observation: {\n          outcome: \"inconclusive\",\n          reason: \"insufficient-evidence\",\n          evidence: \"consumed\",\n          note: \"DA1 responsiveness does not measure saved/restored stack state\"\n        }\n      };\n    }, async (ctx) => {\n      ctx.write(\"\\x1B[#P\");\n      ctx.write(\"\\x1B[#Q\");\n      const pos = await ctx.queryCursorPosition();\n      if (!pos) {\n        return { pass: false, observation: { outcome: \"inconclusive\", reason: \"no-response\", evidence: \"query\" } };\n      }\n      return {\n        pass: false,\n        response: JSON.stringify(pos),\n        observation: {\n          outcome: \"inconclusive\",\n          reason: \"insufficient-evidence\",\n          evidence: \"consumed\",\n          note: \"Cursor responsiveness does not measure saved/restored state\"\n        }\n      };\n    })\n",
    // the retained previous layer's exports
    'export const ntildeLrMarginsAdapter = Object.freeze({',
    'export function ntildeRunLrMarginsTermless(probeId, ctx) {',
]);

/** The pinned seams, exposed so portable tests can build a synthetic layered source. */
export const xcsSeams = pinnedXcsSeams;

/** Derive the xterm color-stack grading extension from the left/right-margin grading extension. */
export function adaptXcsCollector(lrMarginsSource) {
    const source = String(lrMarginsSource);
    for (const seam of pinnedXcsSeams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned xterm color-stack probe seam changed; refusing derivation.');
    }
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local xterm color-stack grading extension ${JSON.stringify(xcsAdapterId)}.
// Derived from the left/right-margin grading extension; probe definitions, policy
// gates and stock grading remain intact.
export const ntildeXcsAdapter = Object.freeze({
  id: ${JSON.stringify(xcsAdapterId)},
  probeIds: ${JSON.stringify([...xcsCaseIds])}
});
/** Execute one pinned termless probe over a caller-served terminal context. */
export function ntildeRunXcsTermless(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.termless !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = probe.termless(ctx);
  return { probeId, ...result };
}
/** Execute one pinned terminal-path probe over a caller-served write/cursor-position context. */
export async function ntildeRunXcsTerminal(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.term !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = await probe.term(ctx);
  return { probeId, ...result };
}
`;
}
