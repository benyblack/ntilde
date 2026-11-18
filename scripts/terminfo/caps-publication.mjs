// A publication-only projection of the independently rederived retained owned run for the iTerm2 feature-reporting (OSC 1337 RequestCapabilities/Capabilities) qualification.
import { capsCaseIds } from './caps-contracts.mjs';
import { makeQueryProbePublication } from './probe-suite-shared.mjs';

export const measuredScriptsRevision = '7bfbbdc1341509557780931d1a02f80dec4e80c3';
/** The measured App build revision. The retained selected-source manifest independently pins the measured scripts; these human-readable labels are not binary-to-source attestation. */
export const measuredAppRevision = '7bfbbdc1341509557780931d1a02f80dec4e80c3';

const scope = 'Pinned terminfo.dev@4.0.0 criterion for extensions.osc1337-capabilities (OSC 1337 ; RequestCapabilities BEL answered by one complete ESC]1337;Capabilities=<characters without BEL or ESC> '
    + 'frame ended by BEL or ST; a query-only probe that can never return unsupported and whose valid pattern accepts any feature string), decided by the unchanged stock collector against the App in this '
    + 'owned run, corroborated by the exact-query witness (query policy v7) and replayed byte-exactly on both pinned paths over the journaled raw reply of its own fenced exchange, beside a separately '
    + 'identified effect contract graded by an independent literal written from the iTerm2 feature-reporting specification\'s code table over this terminal\'s implemented, tested capabilities: the exact '
    + 'pinned request, the same request ended by ST answered by the same ST-terminated reply, two requests answered by two identical replies in request order, and silence for an unsolicited Capabilities '
    + 'assignment, a bare or query Capabilities form, a suffixed or wrong-case request and another OSC, with the exact request still answered after them; every exchange must leave the complete screen, '
    + 'cursor, rendition, margins, modes and active screen unchanged. The measured run\'s direct pane accepts OSC 52 writes under its default initial settings, so the declaration includes Cw; a host that '
    + 'cannot answer omits it (policy, unit-tested, not measured here). The declaration advertises implemented capabilities only — Aw, Uw and the measured-unsupported Sx stay out; the per-code behavior of '
    + 'every declared capability is owned by that capability\'s own implementations and tests; raw replies and stored state, not rendering or painted pixels; no remote/mux, Windows/Linux or common-harness claim.';

/** The projection admits only files bound to this one cleaned-up owned run and reprojects the graded case beside its exact-query witness and stock outcome. */
export const capsPublication = makeQueryProbePublication({ label: 'feature-reporting', caseIds: capsCaseIds, witnessId: 'q07.extensions.osc1337-capabilities',
    observationsFile: 'caps-observations.json', evidenceKey: 'capsEvidence', scope,
    note: 'Stock terminal-path grades from the same owned run; the unchanged pinned probe now passes against the App, moving extensions.osc1337-capabilities from inconclusive to supported.',
    revisions: { appRevision: measuredAppRevision, scriptsRevision: measuredScriptsRevision } });
