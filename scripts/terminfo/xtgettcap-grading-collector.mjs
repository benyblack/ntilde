// Explicitly versioned collector extension for the XTGETTCAP qualification (device.xtgettcap). This is a digest-checked
// derivation layered on top of the title/icon-stack grading extension, a sibling of the OSC-title and insert/replace layers: the pinned
// bundle, the owned adapter and every earlier grading extension stay byte-for-byte unchanged, and each graded result carries this
// extension's identity so it can never be presented as stock output.
//
// The extension adds no probe of its own. It exposes the PINNED probe definition so both of its paths can be replayed over the
// parent-journaled bytes of its own fenced exchange: the termless function (feeds DCS + q 544e ST through feedCapture and grades
// the captured reply with the collector's own deviceReplyResult) and the terminal function (writes the same query with a sentinel
// DA through queryWithSentinelOutcome, which can only ever reach the refusal branch on this terminal). Ntilde deliberately refuses
// TN — its value is the session's TERM, which VT does not know — so both pinned verdicts stay inconclusive and the separately
// identified effect contract carries the graded capability-report conclusion. The derivation refuses unless the retained
// title-stack extension still contains the exact pinned registration, verbatim and once.
import { xtgettcapCaseIds } from './xtgettcap-contracts.mjs';

/** Identity of this layered extension. */
export const xtgettcapAdapterId = 'ntilde-owned-xtgettcap-grading-v1';

/** Exact pinned seams from terminfo.dev@4.0.0 (as retained by the title-stack extension). Each must appear exactly once: the
 * device.xtgettcap registration, then the retained previous layer's exports. */
const pinnedXtgettcapSeams = Object.freeze([
    "    deviceQuery({\n      id: \"device.xtgettcap\",\n      query: \"\\x1BP+q544e\\x1B\\\\\",\n      valid: /\\x1bP1\\+r544e=(?:[0-9A-Fa-f]{2})+\\x1b\\\\/i,\n      refusal: /\\x1bP0\\+r\\x1b\\\\/,\n      malformed: /\\x1bP[01]\\+r/,\n      expected: \"complete XTGETTCAP status 1 for TN with even-length hex value and ST\"\n    }),",
    // the retained previous layer's exports
    'export const ntildeTitleStackAdapter = Object.freeze({',
    'export function ntildeRunTitleStackTermless(probeId, ctx) {',
]);

/** The pinned seams, exposed so portable tests can build a synthetic layered source. */
export const xtgettcapSeams = pinnedXtgettcapSeams;

/** Derive the XTGETTCAP grading extension from the title/icon-stack grading extension (sibling of the OSC-title layer). */
export function adaptXtgettcapCollector(titleStackSource) {
    const source = String(titleStackSource);
    for (const seam of pinnedXtgettcapSeams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned XTGETTCAP probe seam changed; refusing derivation.');
    }
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local XTGETTCAP grading extension ${JSON.stringify(xtgettcapAdapterId)}.
// Derived from the title/icon-stack grading extension (sibling of the OSC-title layer); probe definitions, policy
// gates and stock grading remain intact.
export const ntildeXtgettcapAdapter = Object.freeze({
  id: ${JSON.stringify(xtgettcapAdapterId)},
  probeIds: ${JSON.stringify([...xtgettcapCaseIds])}
});
/** Execute one pinned termless probe over a caller-served terminal context. */
export function ntildeRunXtgettcapTermless(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.termless !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = probe.termless(ctx);
  return { probeId, ...result };
}
/** Execute one pinned terminal-path probe over a caller-served query/sentinel context. */
export async function ntildeRunXtgettcapTerminal(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.term !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = await probe.term(ctx);
  return { probeId, ...result };
}
`;
}
