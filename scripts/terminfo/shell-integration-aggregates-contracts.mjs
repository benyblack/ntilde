// Versioned aggregate evidence contract for issue #984: the two terminfo.dev@4.0.0 AGGREGATE cases
// extensions.semantic-prompts and extensions.osc-633-vscode. This is the first batch that qualifies
// aggregate ids at all (sgr.underline.color deliberately stayed unqualified), so the aggregate
// limitations are recorded beside the evidence set instead of being absorbed into a marker grade.
//
// Pinned probe definitions (read from the retained bundle; the exact registrations are hash-pinned
// and replayed in shell-integration-aggregates-publication.mjs, never inferred from the names):
//
//   extensions.semantic-prompts  termless: reports {"declared": ctx.capabilities.semanticPrompts === true} and
//     returns pass:false / inconclusive / "insufficient-evidence" / evidence "legacy" with note "Declared
//     semantic-prompt capability does not establish shell prompt integration". Terminal: pass:false /
//     inconclusive / evidence "none" with note "No shell prompt integration metadata or behavior readback
//     for OSC 133". It feeds no byte, writes no byte and reads no state.
//
//   extensions.osc-633-vscode   termless: the same declared report with note "Declared semantic-prompt
//     capability does not establish VS Code OSC 633 shell integration". Terminal: inconclusive /
//     evidence "none" with note "No VS Code shell integration metadata or behavior readback for OSC 633".
//     It feeds no byte, writes no byte and reads no state.
//
// Both aggregates therefore have fixtureBytes:false and can never produce a pinned supported verdict:
// a declared capability is not an observation, and the probe's own note says so. Their pinned verdicts
// are preserved exactly as registered (replay contexts declare a capability value; it never decides a
// verdict - both declared values resolve to the same inconclusive observation), and the graded verdict
// is carried by separately identified aggregate effect contracts that depend on ADMITTED, separately
// receipted marker publications. Dependence is directional: the marker publications do not claim these
// aggregates (each of their scopes explicitly excludes them), and this suite does not turn their sum
// into an upstream supported verdict.

/** Identity of this suite's projection output. */
export const aggregatesSuiteId = 'ntilde-owned-shell-integration-aggregates-projection-v1';
/** Contracts v1 is the first aggregate evidence set of this batch. */
export const aggregatesVersion = 1;
/** The two pinned aggregate cases, in report order. */
export const aggregateCaseIds = Object.freeze(['extensions.osc-633-vscode', 'extensions.semantic-prompts']);
/** Identities of the two separately identified aggregate effect contracts. */
export const semanticPromptsEffectContractId = 'ntilde-semantic-prompts-aggregate-effect-v1';
export const osc633AggregateEffectContractId = 'ntilde-osc633-vscode-aggregate-effect-v1';

/** The admitted marker publications this batch depends on. Each is already registered, receipted and
 * independently rederived by its own suite; each of their scopes explicitly excludes these aggregates. */
export const osc133Carrier = Object.freeze({
    suite: 'osc133', suiteId: 'ntilde-owned-osc133-graded-v1', dir: '2026-11-11-osc133',
    publication: 'docs/qa/terminfo/2026-11-11-osc133/', effectContractId: 'ntilde-osc133-shell-lifecycle-effect-v1',
});
export const osc633Carrier = Object.freeze({
    suite: 'osc633', suiteId: 'ntilde-owned-osc633-graded-v1', dir: '2026-11-16-osc633-implementation',
    publication: 'docs/qa/terminfo/2026-11-16-osc633-implementation/', effectContractId: 'ntilde-osc633-shell-lifecycle-effect-v1',
});

/** The verbatim pinned probe notes, keyed per aggregate (the replay asserts these byte for byte). */
export const aggregatePinnedNotes = Object.freeze({
    'extensions.semantic-prompts': Object.freeze({
        termless: 'Declared semantic-prompt capability does not establish shell prompt integration',
        terminal: 'No shell prompt integration metadata or behavior readback for OSC 133',
    }),
    'extensions.osc-633-vscode': Object.freeze({
        termless: 'Declared semantic-prompt capability does not establish VS Code OSC 633 shell integration',
        terminal: 'No VS Code shell integration metadata or behavior readback for OSC 633',
    }),
});

/** The declared-capability values the replay contexts present to the pinned termless code. The stock
 * collector context declared neither (response {"declared":false}); the owned observation API does
 * report the admitted lifecycle metadata, so the projection also replays declared:true. Both values
 * resolve to the same inconclusive observation - the declaration never decides a verdict. */
export const declaredCapabilityVariants = Object.freeze([false, true]);

const admitted = (caseId, outcome = 'supported') => Object.freeze({ caseId, requiredOutcome: outcome });

/** The exact lifecycle/metadata evidence each aggregate effect needs. A required entry is admitted
 * only while the named publication's own retained claim for that case is qualified with the required
 * effect outcome; a pending entry names the batch that must publish the evidence first. Missing
 * evidence is not a demonstrated terminal failure: a pending aggregate is inconclusive, never unsupported. */
export const aggregateEvidenceSets = Object.freeze({
    'extensions.semantic-prompts': Object.freeze({
        protocol: 'OSC 133 shell-integration semantic prompts',
        carrier: osc133Carrier,
        required: Object.freeze(['extensions.osc133-a', 'extensions.osc133-b', 'extensions.osc133-c', 'extensions.osc133-d'].map(id => admitted(id))),
        pending: Object.freeze([]),
        // OSC 133 P prompt metadata is a separately owned audit batch (#985), exactly like the marker
        // batch excluded OSC 633 E/P from its lifecycle contract. It is a boundary of this evidence
        // set, not missing evidence: if a future authoritative contract requires P, this set grows.
        excluded: Object.freeze([Object.freeze({
            scope: 'extensions.osc133-p',
            reason: 'separately owned OSC 133 P prompt-metadata audit (#985); not part of this aggregate evidence set',
        })]),
        effectContractId: semanticPromptsEffectContractId,
        // The lifecycle publications are necessary and sufficient for this aggregate's declared
        // capability: semantic prompts are the OSC 133 prompt lifecycle observed as pane metadata
        // and session status.
        outcome: 'supported',
    }),
    'extensions.osc-633-vscode': Object.freeze({
        protocol: 'VS Code shell integration (OSC 633)',
        // The authoritative VS Code contract the OSC 633 batch implemented A-D from; E and P are part
        // of the same supported-sequences list and have no admitted publication yet.
        authoritativeContract: 'https://code.visualstudio.com/docs/terminal/shell-integration#_supported-escape-sequences',
        carrier: osc633Carrier,
        required: Object.freeze([
            ...['extensions.osc633-a', 'extensions.osc633-b', 'extensions.osc633-c', 'extensions.osc633-d'].map(id => admitted(id)),
            admitted('extensions.osc633-e'), admitted('extensions.osc633-p'),
        ]),
        pending: Object.freeze([
            Object.freeze({ caseId: 'extensions.osc633-e', owner: '#983', reason: 'the E command-line record has no implemented, qualified publication yet' }),
            Object.freeze({ caseId: 'extensions.osc633-p', owner: '#983', reason: 'the P property/cwd record has no implemented, qualified publication yet' }),
        ]),
        excluded: Object.freeze([Object.freeze({
            scope: 'OSC 633 F-I continuation-prompt records',
            reason: 'terminfo.dev registers no atomic case for them; recorded as an observation-only boundary of the authoritative contract, not evidence',
        })]),
        effectContractId: osc633AggregateEffectContractId,
        // The admitted A-D lifecycle evidence is necessary but not sufficient: until #983 publishes
        // the command/property records, the aggregate stays inconclusive rather than absorbing their
        // absence into a lifecycle-only verdict.
        outcome: 'inconclusive',
    }),
});

/** The classification tag of the evidence this suite's claims rest on. The admitted publications
 * measured stored cells together with pane metadata and session status; the projection replays over
 * exactly those layers and adds none of its own. */
export const aggregateEvidenceTag = 'admitted-stored-cells-and-pane-metadata-lifecycle-publications';
