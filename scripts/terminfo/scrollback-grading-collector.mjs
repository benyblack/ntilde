// Explicitly versioned collector extension for the scrolling/history qualification (the nine pinned
// scrollback.* cases). This is a fourteenth digest-checked derivation layered on top of the character-editing
// grading extension: the pinned bundle, the owned adapter and every earlier grading extension stay byte-for-byte
// unchanged, and each graded result carries this extension's identity so it can never be presented as stock output.
//
// The extension adds no probe of its own. It exposes the PINNED termless probe definitions of the nine contracted
// cases so live, independently observed states can be graded by the collector's own criteria. Derivation refuses
// unless the retained character-editing extension still contains the exact pinned scrollback section (every registration,
// the tooSmall2/validSize2 helpers and all nine termless and terminal bodies, verified by one SHA-256 over the verbatim
// section text) plus the parserStateResult and isBlank helpers the probes call, each exactly once.
import { createHash } from 'node:crypto';
import { scrollbackCaseIds } from './scrollback-contracts.mjs';

/** Identity of this thirteenth layered extension. */
export const scrollbackAdapterId = 'ntilde-owned-scrollback-grading-v1';

/** The verbatim pinned section markers and digest (terminfo.dev@4.0.0, bundle aa5f2bf9…d1f6). */
const sectionStart = '// ../probe-defs/src/scrollback.ts\n';
const sectionEnd = '// ../probe-defs/src/charsets.ts\n';
export const pinnedScrollbackSectionSha256 = '8501a798ecaed827a73d60d265b0dca354a5fbf0873ec065f5ea2071db71fb1c';

/** Small pinned helper bodies, each present verbatim once. */
const helperSeams = Object.freeze([
    'function isBlank(char) {\n  return char === "" || char === " ";\n}',
    'function parserStateResult(pass, expected, state, note) {\n  const response = JSON.stringify(state);\n  return {\n    pass: pass === true,\n    response,\n    ...note && { note },\n    observation: {\n      outcome: pass === null ? "inconclusive" : pass ? "supported" : "unsupported",\n      ...pass === null && { reason: "insufficient-evidence" },\n      evidence: "parser-state",\n      ...note && { note }\n    },\n    ...pass !== null && {\n      assertions: [{ kind: pass ? "positive" : "negative", expected, observed: response }]\n    }\n  };\n}',
    // the retained previous layer's exports
    'export const ntildeCharEditAdapter = Object.freeze({',
    'export function ntildeRunCharEditTermless(probeId, ctx) {',
]);

/** The pinned seams, exposed so portable tests can build a synthetic layered source. */
export const scrollbackSeams = Object.freeze({ sectionStart, sectionEnd, helperSeams });

/** The verbatim pinned scrollback section of a source, or a refusal when it is absent, repeated or changed. */
export function pinnedScrollbackSection(source) {
    const text = String(source);
    const start = text.indexOf(sectionStart);
    if (start < 0 || text.indexOf(sectionStart, start + 1) !== -1) throw new Error('Pinned scrollback section seam changed; refusing derivation.');
    const end = text.indexOf(sectionEnd, start);
    if (end < 0 || text.indexOf(sectionEnd, end + 1) !== -1) throw new Error('Pinned scrollback section seam changed; refusing derivation.');
    return text.slice(start, end);
}

/** Derive the scrolling/history grading extension from the character-editing grading extension. Only portable tests pass a different
 * expected digest, to exercise the refusals on a synthetic source; every production caller uses the pinned digest. */
export function adaptScrollbackCollector(charEditSource, expectedSectionSha256 = pinnedScrollbackSectionSha256) {
    const source = String(charEditSource);
    for (const seam of helperSeams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned scrollback helper seam changed; refusing derivation.');
    }
    const digest = createHash('sha256').update(pinnedScrollbackSection(source)).digest('hex');
    if (digest !== expectedSectionSha256) throw new Error('Pinned scrollback probes changed; refusing derivation.');
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local scrolling/history grading extension ${JSON.stringify(scrollbackAdapterId)}.
// Derived from the character-editing grading extension; probe definitions, policy
// gates and stock grading remain intact.
export const ntildeScrollbackAdapter = Object.freeze({
  id: ${JSON.stringify(scrollbackAdapterId)},
  probeIds: ${JSON.stringify([...scrollbackCaseIds])}
});
/** Execute one pinned termless probe over caller-served states. */
export function ntildeRunScrollbackTermless(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.termless !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = probe.termless(ctx);
  return { probeId, ...result };
}
`;
}
