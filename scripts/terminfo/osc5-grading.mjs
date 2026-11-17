// Parent-side report builder for the rxvt special-color qualification. Re-derives every conclusion from the retained parent journal (screens and the raw reply lists bound to their barriers) and the
// independent contract grader, refuses any tampering, and requires both pinned paths' own grading to agree: each pinned result is a pure function of the journaled replies of its own exchange (the terminal
// path also sees the DA barrier that follows them). The pinned verdict is the replayed pinned code's own (supported when the index-0 reply carries one complete frame); the separately identified effect
// contract carries the graded conclusion and any difference is recorded as a divergence, never absorbed.
import { validateGradedJournal, crossCheckGradedCapture, requirePinnedAgreement, requireReplayAgreement, validateGradedCaptureRecord, planReads } from './graded-plan.mjs';
import { colorBindingHash } from './resolved-colors.mjs';
import { screenSnapshot } from './agent-observer.mjs';
import { validateOsc5Exchange } from './osc5-report.mjs';
import { osc5Contracts, osc5CaseIds, osc5ContractsVersion, osc5NoncePrefix, osc5Prefix, osc5SuiteId, osc5EffectContractId, gradeOsc5Case, osc5Observation,
    validateOsc5Screen, replayOsc5Pinned } from './osc5-contracts.mjs';
import { osc5AdapterId } from './osc5-grading-collector.mjs';
import { osc5RecordingContext } from './osc5-collector.mjs';

const label = 'special-color';
const derivedFrom = 'ntilde-owned-left-right-margins-grading-v1';
const isDigest = value => /^[a-f\d]{64}$/.test(value ?? '');
const scope = 'Pinned terminfo.dev@4.0.0 criterion for extensions.osc5-special-color (OSC 5;0;? BEL answered by one complete ESC]5;0;rgb:h{1,4}/h{1,4}/h{1,4} frame ended by BEL or ST; '
    + 'a query-only probe that can never return unsupported): both pinned paths replayed byte-exactly over the journaled raw replies of their own fenced exchange, beside the separately identified effect '
    + 'contract ntilde-osc5-special-colors-effect-v1 graded by an independent oracle written from rxvt\'s special-color text (OSC 5 ; c ; spec sets special color c — 0 bold, 1 underline, 2 blink, 3 reverse, '
    + '4 italic — any number of chained pairs per sequence, a ? answered by a control sequence of the same form, OSC 105 ; c resets special color c and a bare OSC 105 resets all five): all five slots set '
    + 'and read back through OSC 5, per-slot and bare resets, the numeric encodings, chained pairs, reply order and the ST terminator, and the separation from the OSC 4 palette, the OSC 10-19 dynamic colors '
    + 'and the kitty OSC 21 keys pinned by within-exchange reply equalities; every exchange must leave the complete screen, cursor, rendition, margins, modes and active screen unchanged. Numeric selectors '
    + '0-4 only, ignored unmodeled selectors, malformed pairs, the oversize cap, set-only silence and the default-foreground slot default are policy groups that never decide a verdict. Raw terminal state, '
    + 'not host presentation or painted pixels; no stock grades.';

/** Special-color screens carry attributes, stored details (with overline and protection), the stored cursor mode, resolved colors, the App's stored buffer state and the DECLRMM margins. */
const screenOf = value => {
    const screen = screenSnapshot(value, true, true, true, true, true, true, false, true, true);
    validateOsc5Screen(screen);
    return screen;
};

/** Total read groups across the contracts, plus start, bind and finish frames. */
const frameCount = () => 3 + osc5Contracts.reduce((sum, contract) => sum + planReads(contract), 0);

/** Validate journal, run identity, binding stability and restoration. The case stores special colors (that is what it measures), so its reads legitimately carry another color binding - but the
 *  palette, theme and render state must be proven identical before the first request, after the bind and after the finish (every color reset back to the host's), and only report reads carry a
 *  reply list; start, bind and finish never do. */
export function validateOsc5Evidence(frames, run, runId, adapter) {
    validateGradedJournal(frames, { frameCount: frameCount(), prefix: osc5Prefix, noncePrefix: osc5NoncePrefix, runId, label, screenOf });
    const adapterIdentityOk = adapter !== null && adapter !== undefined && adapter.id === osc5AdapterId && run.adapterId === osc5AdapterId
        && isDigest(adapter.bundleSha256) && isDigest(adapter.upstreamBundleSha256) && adapter.derivedFromAdapterId === derivedFrom;
    if (!adapterIdentityOk) throw new Error(`Invalid ${label} adapter identity.`);
    const coversCases = JSON.stringify(run.probeIds) === JSON.stringify([...osc5CaseIds]) && Array.isArray(run.cases) && run.cases.length === osc5Contracts.length;
    if (!coversCases) throw new Error(`${label} run does not cover the contracted cases.`);
    const edgeBinding = colorBindingHash(frames[0].screen.colorBinding);
    const edges = [frames[1], frames.at(-1)];
    if (edges.some(edge => colorBindingHash(edge.screen.colorBinding) !== edgeBinding)) throw new Error(`${label} observation saw the palette or theme change.`);
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
        const journaled = validateOsc5Exchange(frame.report), worker = validateOsc5Exchange(read.report);
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
    requirePinnedAgreement(contract, termless, replayOsc5Pinned(contract.caseId, 'termless', { replies }), label);
    requirePinnedAgreement(contract, terminal, replayOsc5Pinned(contract.caseId, 'terminal', { replies, barrier: exchange.barrier }), label);
    if (replayModule) {
        const context = osc5RecordingContext(contract, exchange);
        requireReplayAgreement(contract, replayModule.ntildeRunOsc5Termless(contract.caseId, context), termless);
        if (context.fedCount !== contract.pinnedFeeds.length || context.askedCount !== 0) throw new Error(`Pinned ${contract.caseId} replay made a different number of calls.`);
    }
    return { termless, terminal };
}

const resultOf = pinned => ({ pass: pinned.pass, response: pinned.response ?? null, note: pinned.note ?? null, observation: pinned.observation, assertions: pinned.assertions ?? null });

/** Derive one case's published record from its journaled frames, worker record and the graded effect. */
function deriveOsc5Case(contract, recorded, readFrames, { replies, observation, effect, termless, terminal, contractsVersion }) {
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
        colorBinding: { reverseVideo: theme.reverseVideo, themeName: theme.themeName, foregroundArgb: theme.foregroundArgb, backgroundArgb: theme.backgroundArgb,
            minimumContrast: theme.minimumContrast, paletteOverrideIndices: [...theme.paletteOverrideIndices], bindingSha256: colorBindingHash(theme) },
        pinned: { termless: resultOf(termless), terminal: resultOf(terminal) },
        effectContract: { id: osc5EffectContractId, version: contractsVersion, outcome: effect.outcome, ...(effect.note ? { note: effect.note } : {}),
            attribution: 'separately identified Ntilde effect contract; not the pinned probe' },
        contractResult: effect, ...(divergence ? { divergence } : {}),
        conclusion: { outcome: termless.observation.outcome, evidence: 'owned-raw-replies-and-unchanged-state', grading: 'pinned-termless-replayed', note: termless.observation.note ?? null,
            reason: termless.observation.reason ?? null, scope: contract.appearanceScope } };
}

/** Re-derive and cross-check every case; throw on any disagreement. */
export function buildOsc5GradingReport(frames, run, runId, adapter, { contractsVersion = osc5ContractsVersion, replayModule = null } = {}) {
    validateOsc5Evidence(frames, run, runId, adapter);
    let frameIndex = 2;
    const cases = [];
    for (const [position, contract] of osc5Contracts.entries()) {
        const recorded = run.cases[position];
        if (recorded.caseId !== contract.caseId) throw new Error(`${label} case order differs from the contracts.`);
        validateGradedCaptureRecord(contract, recorded, label);
        const readFrames = recorded.reads.map(() => frames[frameIndex++]);
        crossCheckGradedCapture(contract, recorded, readFrames, label);
        const replies = repliesOf(contract, recorded, readFrames);
        const observation = osc5Observation(contract, readFrames.map(frame => frame.screen), replies, osc5NoncePrefix + runId);
        const effect = gradeOsc5Case(contract, observation, contractsVersion);
        const exchange = validateOsc5Exchange(readFrames[contract.pinnedRead].report);
        const { termless, terminal } = verifyPinnedReplays(contract, recorded, exchange, { replayModule });
        cases.push(deriveOsc5Case(contract, recorded, readFrames, { replies, observation, effect, termless, terminal, contractsVersion }));
    }
    return { schemaVersion: 1, suiteId: osc5SuiteId, runId, contractsVersion, adapter, scope, initialSnapshot: 0, restoredSnapshot: frames.length - 1, cases, snapshots: frames };
}
