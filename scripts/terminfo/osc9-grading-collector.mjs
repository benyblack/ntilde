// Explicitly versioned collector extension for the OSC 9 qualification (extensions.osc9-progress and extensions.notifications). This is a
// digest-checked derivation layered on top of the left/right-margin grading extension, a sibling of the OSC 7/8, title/icon-stack and OSC 133 layers:
// the pinned bundle, the owned adapter and every earlier grading extension stay byte-for-byte unchanged, and each graded result carries this
// extension's identity so it can never be presented as stock output.
//
// The extension adds no probe of its own. It exposes the PINNED probe definitions so their paths can be replayed over the parent-observed journal:
// extensions.osc9-progress (no termless function at all; terminal: unmeasuredStateEffect, which writes and reads nothing) and extensions.notifications
// (termless: a declared-capability report; terminal: no readback; neither writes or reads a byte). The derivation refuses unless the retained
// left/right-margin extension still contains both exact registrations and the unmeasuredStateEffect helper, each verbatim and once.
import { osc9CaseIds } from './osc9-contracts.mjs';

/** Identity of this layered extension. */
export const osc9AdapterId = 'ntilde-owned-osc9-grading-v1';

/** Exact pinned seams from terminfo.dev@4.0.0 (as retained by the left/right-margin extension). Each must appear exactly once: the two registrations, the
 * unmeasuredStateEffect helper, then the retained previous layer's exports. */
const pinnedOsc9Seams = Object.freeze([
    // extensions.osc9-progress registration
    "    probe(\"extensions.osc9-progress\", null, () => Promise.resolve(unmeasuredStateEffect(\"OSC 9;4 progress display\"))),\n",
    // extensions.notifications registration
    "    probe(\"extensions.notifications\", (ctx) => {\n      const note = \"Declared OSC 9 capability does not establish desktop notification delivery\";\n      return {\n        pass: false,\n        response: JSON.stringify({ declared: ctx.capabilities.extensions.has(\"osc9\") }),\n        note,\n        observation: { outcome: \"inconclusive\", reason: \"insufficient-evidence\", evidence: \"legacy\", note }\n      };\n    }, () => {\n      const note = \"No desktop notification delivery readback for OSC 9\";\n      return Promise.resolve({\n        pass: false,\n        note,\n        observation: { outcome: \"inconclusive\", reason: \"insufficient-evidence\", evidence: \"none\", note }\n      });\n    }),\n",
    // unmeasuredStateEffect helper
    "function unmeasuredStateEffect(feature) {\n  const note = `${feature} was not attempted; this probe has no effect readback or prior-state restoration`;\n  return {\n    pass: false,\n    note,\n    observation: { outcome: \"inconclusive\", reason: \"insufficient-evidence\", evidence: \"none\", note }\n  };\n}\n",
    // the retained previous layer's exports
    'export const ntildeLrMarginsAdapter = Object.freeze({',
    'export function ntildeRunLrMarginsTermless(probeId, ctx) {',
]);

/** The pinned seams, exposed so portable tests can build a synthetic layered source. */
export const osc9Seams = pinnedOsc9Seams;

/** Derive the OSC 9 grading extension from the left/right-margin grading extension. */
export function adaptOsc9Collector(lrMarginsSource) {
    const source = String(lrMarginsSource);
    for (const seam of pinnedOsc9Seams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned OSC 9 probe seam changed; refusing derivation.');
    }
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local OSC 9 grading extension ${JSON.stringify(osc9AdapterId)}.
// Derived from the left/right-margin grading extension; probe definitions, policy
// gates and stock grading remain intact.
export const ntildeOsc9Adapter = Object.freeze({
  id: ${JSON.stringify(osc9AdapterId)},
  probeIds: ${JSON.stringify([...osc9CaseIds])}
});
/** Whether the pinned probe has a termless function at all (extensions.osc9-progress has none). */
export function ntildeOsc9HasTermless(probeId) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe) throw new Error("Unknown pinned probe " + probeId);
  return typeof probe.termless === "function";
}
/** Execute one pinned termless probe over a caller-served context. */
export function ntildeRunOsc9Termless(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.termless !== "function") throw new Error("Unknown pinned termless probe " + probeId);
  return { probeId, ...probe.termless(ctx) };
}
/** Execute one pinned terminal-path probe over a caller-served context. */
export async function ntildeRunOsc9Terminal(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.term !== "function") throw new Error("Unknown pinned terminal probe " + probeId);
  return { probeId, ...await probe.term(ctx) };
}
`;
}
