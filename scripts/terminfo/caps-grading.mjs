// Parent-side report builder for the iTerm2 feature-reporting qualification. Re-derives every conclusion from the retained parent journal (screens and the raw reply lists bound to their barriers) and
// the independent contract grader, refuses any tampering, and requires both pinned paths' own grading to agree: each pinned result is a pure function of the journaled replies of its own exchange (the
// terminal path also sees the DA barrier that follows them). The pinned verdict is the replayed pinned code's own (supported when the captured text carries one complete Capabilities frame, whatever the
// feature string); the separately identified effect contract carries the graded conclusion and any difference is recorded as a divergence, never absorbed.
import { validateGradedJournal, crossCheckGradedCapture, requirePinnedAgreement, requireReplayAgreement, validateGradedCaptureRecord, planReads } from './graded-plan.mjs';
import { colorBindingHash } from './resolved-colors.mjs';
import { screenSnapshot } from './agent-observer.mjs';
import { validateCapsExchange } from './caps-report.mjs';
import { capsContracts, capsCaseIds, capsContractsVersion, capsNoncePrefix, capsPrefix, capsSuiteId, capsEffectContractId, gradeCapsCase, capsObservation,
    validateCapsScreen, replayCapsPinned } from './caps-contracts.mjs';
import { capsAdapterId } from './caps-grading-collector.mjs';
import { capsRecordingContext } from './caps-collector.mjs';

const label = 'feature-reporting';
const derivedFrom = 'ntilde-owned-left-right-margins-grading-v1';
const isDigest = value => /^[a-f\d]{64}$/.test(value ?? '');
const scope = 'Pinned terminfo.dev@4.0.0 criterion for extensions.osc1337-capabilities (OSC 1337 ; RequestCapabilities BEL answered by one complete ESC]1337;Capabilities=<characters without BEL or ESC> '
    + 'frame ended by BEL or ST; a query-only probe that can never return unsupported and whose valid pattern accepts any feature string): both pinned paths replayed byte-exactly over the journaled raw '
    + 'replies of their own fenced exchange, beside the separately identified effect contract ntilde-osc1337-capabilities-effect-v1 graded by an independent literal written from the iTerm2 '
    + 'feature-reporting specification\'s code table over this terminal\'s implemented, tested capabilities: the exact pinned request, the same request ended by ST answered by the same ST-terminated '
    + 'reply, two requests answered by two identical replies in request order, and silence for an unsolicited Capabilities assignment, a bare or query Capabilities form, a suffixed or wrong-case request '
    + 'and another OSC, with the exact request still answered after them; every exchange must leave the complete screen, cursor, rendition, margins, modes and active screen unchanged. The measured run\'s '
    + 'direct pane accepts OSC 52 writes under its default initial settings, so the declaration includes Cw; a host that cannot answer omits it (policy, unit-tested, not measured here). The declaration '
    + 'advertises implemented capabilities only — Aw, Uw and the measured-unsupported Sx stay out; the per-code behavior of every declared capability is owned by that capability\'s own implementations and '
    + 'tests. Raw terminal state, not host presentation or painted pixels; no stock grades.';

/** Feature-reporting screens carry attributes, stored details (with overline and protection), the stored cursor mode, resolved colors, the App's stored buffer state and the DECLRMM margins. */
const screenOf = value => {
    const screen = screenSnapshot(value, true, true, true, true, true, true, false, true, true);
    validateCapsScreen(screen);
    return screen;
};

/** Total read groups across the contracts, plus start, bind and finish frames. */
const frameCount = () => 3 + capsContracts.reduce((sum, contract) => sum + planReads(contract), 0);

/** Validate journal, run identity, binding stability and restoration. The case changes no color, so every frame must carry the same binding, and only report reads carry a reply list; start, bind and
 *  finish never do. */
export function validateCapsEvidence(frames, run, runId, adapter) {
    validateGradedJournal(frames, { frameCount: frameCount(), prefix: capsPrefix, noncePrefix: capsNoncePrefix, runId, label, screenOf });
    const adapterIdentityOk = adapter !== null && adapter !== undefined && adapter.id === capsAdapterId && run.adapterId === capsAdapterId
        && isDigest(adapter.bundleSha256) && isDigest(adapter.upstreamBundleSha256) && adapter.derivedFromAdapterId === derivedFrom;
    if (!adapterIdentityOk) throw new Error(`Invalid ${label} adapter identity.`);
    const coversCases = JSON.stringify(run.probeIds) === JSON.stringify([...capsCaseIds]) && Array.isArray(run.cases) && run.cases.length === capsContracts.length;
    if (!coversCases) throw new Error(`${label} run does not cover the contracted cases.`);
    const edgeBinding = colorBindingHash(frames[0].screen.colorBinding);
    if (frames.some(frame => colorBindingHash(frame.screen.colorBinding) !== edgeBinding)) throw new Error(`${label} observation saw the palette or theme change.`);
    for (const edge of [frames[0], frames[1], frames.at(-1)]) {
        if (edge.report !== undefined) throw new Error(`Unexpected ${label} report frame.`);
    }
}

/** The reply lists of one case's reads, in plan order. A cells read carries no report; a report read's journaled replies must equal the worker's record and be ordered by that read's own barrier. */
function repliesOf(contract, recorded, readFrames) {
    const reads = contract.plan.filter(step => step.read), out = [];
    for (const [index, step] of reads.entries()) {
        const frame = readFrames[index], read = recorded.reads[index];
        if (step.read !== 'report') {
            if (frame.report !== undefined || read.report !== undefined) throw new Error(`Unexpected ${contract.caseId} report on a cells read.`);
            out.push([]);
            continue;
        }
        const journaled = validateCapsExchange(frame.report), worker = validateCapsExchange(read.report);
        if (JSON.stringify(journaled) !== JSON.stringify(worker) || journaled.barrier !== frame.barrier) throw new Error(`${label} ${contract.caseId} replies differ from their read frame.`);
        out.push(journaled.replies);
    }
    return out;
}

/** A difference between the pinned verdict and the effect verdict is a recorded divergence, never absorbed. */
function reconcile(pinnedOutcome, effect) {
    return pinnedOutcome === effect.outcome ? null : { pinnedOutcome, contractOutcome: effect.outcome, kind: `pinned-${pinnedOutcome}-effect-${effect.outcome}` };
}

/** Verify and replay one case's pinned results over its journaled exchange; returns the verified pair. */
function verifyPinnedReplays(contract, recorded, exchange, { replayModule = null } = {}) {
    const { termless, terminal } = recorded.pinned ?? {};
    if (termless?.probeId !== contract.caseId || terminal?.probeId !== contract.caseId || !termless.observation || !terminal.observation) throw new Error(`Missing pinned ${contract.caseId} grading results.`);
    const replies = exchange.replies.join('');
    requirePinnedAgreement(contract, termless, replayCapsPinned(contract.caseId, 'termless', { replies }), label);
    requirePinnedAgreement(contract, terminal, replayCapsPinned(contract.caseId, 'terminal', { replies, barrier: exchange.barrier }), label);
    if (replayModule) {
        const context = capsRecordingContext(contract, exchange);
        requireReplayAgreement(contract, replayModule.ntildeRunCapsTermless(contract.caseId, context), termless);
        if (context.fedCount !== contract.pinnedFeeds.length || context.askedCount !== 0) throw new Error(`Pinned ${contract.caseId} replay made a different number of calls.`);
    }
    return { termless, terminal };
}

const resultOf = pinned => ({ pass: pinned.pass, response: pinned.response ?? null, note: pinned.note ?? null, observation: pinned.observation, assertions: pinned.assertions ?? null });

/** Derive one case's published record from its journaled frames, worker record and the graded effect. */
function deriveCapsCase(contract, recorded, readFrames, { replies, observation, effect, termless, terminal, contractsVersion }) {
    const theme = readFrames[0].screen.colorBinding, observed = contract.observed(observation);
    const divergence = reconcile(termless.observation.outcome, effect);
    const reads = recorded.reads.map((read, index) => ({ kind: read.kind, cursor: read.cursor, ...(read.kind === 'report' ? { replies: replies[index] } : {}) }));
    const binding = { reverseVideo: theme.reverseVideo, themeName: theme.themeName, foregroundArgb: theme.foregroundArgb, backgroundArgb: theme.backgroundArgb,
        minimumContrast: theme.minimumContrast, paletteOverrideIndices: [...theme.paletteOverrideIndices], bindingSha256: colorBindingHash(theme) };
    return { caseId: contract.caseId, pinnedCriterion: contract.pinnedCriterion, pinnedScript: observed.pinnedScript, terminalFixture: contract.terminalFixture,
        feeds: recorded.feeds, barriers: recorded.barriers,
        reads,
        observed,
        colorBinding: binding,
        pinned: { termless: resultOf(termless), terminal: resultOf(terminal) },
        effectContract: { id: capsEffectContractId, version: contractsVersion, outcome: effect.outcome, ...(effect.note ? { note: effect.note } : {}),
            attribution: 'separately identified Ntilde effect contract; not the pinned probe' },
        contractResult: effect, ...(divergence ? { divergence } : {}),
        conclusion: { outcome: termless.observation.outcome, evidence: 'owned-raw-replies-and-unchanged-state', grading: 'pinned-termless-replayed', note: termless.observation.note ?? null,
            reason: termless.observation.reason ?? null, scope: contract.appearanceScope } };
}

/** Re-derive and cross-check every case; throw on any disagreement. */
export function buildCapsGradingReport(frames, run, runId, adapter, { contractsVersion = capsContractsVersion, replayModule = null } = {}) {
    validateCapsEvidence(frames, run, runId, adapter);
    let frameIndex = 2;
    const cases = [];
    for (const [position, contract] of capsContracts.entries()) {
        const recorded = run.cases[position];
        if (recorded.caseId !== contract.caseId) throw new Error(`${label} case order differs from the contracts.`);
        validateGradedCaptureRecord(contract, recorded, label);
        const readFrames = recorded.reads.map(() => frames[frameIndex++]);
        crossCheckGradedCapture(contract, recorded, readFrames, label);
        const replies = repliesOf(contract, recorded, readFrames);
        const observation = capsObservation(contract, readFrames.map(frame => frame.screen), replies, capsNoncePrefix + runId);
        const effect = gradeCapsCase(contract, observation, contractsVersion);
        const exchange = validateCapsExchange(readFrames[contract.pinnedRead].report);
        const { termless, terminal } = verifyPinnedReplays(contract, recorded, exchange, { replayModule });
        cases.push(deriveCapsCase(contract, recorded, readFrames, { replies, observation, effect, termless, terminal, contractsVersion }));
    }
    return { schemaVersion: 1, suiteId: capsSuiteId, runId, contractsVersion, adapter, scope, initialSnapshot: 0, restoredSnapshot: frames.length - 1, cases, snapshots: frames };
}
