// Parent-side report builder for the DECSCLM qualification. Re-derives every conclusion from the retained parent journal (screens, the parent's own timing records and the raw reply lists bound to their
// barriers) and the independent contract grader, refuses any tampering, and requires both pinned paths' own grading to agree: the termless result is a pure function of the journaled replies of its own
// exchange, the terminal result of the journaled replies of the pinned mode query. The pinned verdict is the replayed pinned code's own (inconclusive on both paths: a DECRPM state is not a scroll timing,
// and mode 4 is not recognized); the separately identified effect contract carries the graded conclusion (unsupported: DECSCLM ignored, bursts identical and inside the timing bound) and any difference is
// recorded as a divergence, never absorbed.
import { validateGradedJournal, crossCheckGradedCapture, requirePinnedAgreement, requireReplayAgreement, validateGradedCaptureRecord, planReads } from './graded-plan.mjs';
import { suiteReplyLists, reconcilePinnedWithEffect, suitePinnedResult } from './probe-suite-shared.mjs';
import { colorBindingHash } from './resolved-colors.mjs';
import { screenSnapshot } from './agent-observer.mjs';
import { validateDslExchange } from './dsl-report.mjs';
import { dslContracts, dslCaseIds, dslContractsVersion, dslNoncePrefix, dslPrefix, dslSuiteId, dslEffectContractId, gradeDslCase, dslObservation,
    validateDslScreen, replayDslTermless, replayDslTerminal } from './dsl-contracts.mjs';
import { dslAdapterId } from './dsl-grading-collector.mjs';
import { dslRecordingContext } from './dsl-collector.mjs';
import { validateTiming } from './dsl-capture.mjs';

const label = 'decsclm';
const derivedFrom = 'ntilde-owned-left-right-margins-grading-v1';
const isDigest = value => /^[a-f\d]{64}$/.test(value ?? '');
const scope = 'Pinned terminfo.dev@4.0.0 criteria for modes.decsclm (decrpmModeProbe("modes.decsclm", 4, ...): the termless path feeds CSI ? 4 h, captures the CSI ? 4 $ p reply and feeds CSI ? 4 l and always returns '
    + 'parserStateResult(null, ...), inconclusive by construction; the terminal path asks queryMode(4) and returns decrpmResult, inconclusive while mode 4 is not recognized): both pinned paths replayed '
    + 'byte-exactly over the journaled replies of their own fenced exchanges, beside the separately identified effect contract ntilde-decsclm-unsupported-effect-v1 graded by independent literals written '
    + 'from DEC\'s DECSCLM contract (smooth scrolling paces scrolling output at about six lines per second): CSI ? 4 h / l alone and a forty-line scroll burst of the margin band with smooth scrolling selected, held, '
    + 'reset and never touched are ignored (policy, #1001: terminal output always jump-scrolls), leave the screen, cursor, rendition, margins, modes and active screen as contracted (identical across the bursts), '
    + 'finish every exchange inside a two-second bound that scrolling at six lines per second could not meet (read on the parent\'s monotonic clock, beside a control that reads a deliberate 2.5-second hold) and '
    + 'leave mode 4 answered not recognized, beside a recognised-mode control and a clear/home control. Raw terminal replies, stored state and parent timing, not a rendered frame or painted pixels; no stock grades.';

/** Feature-reporting screens carry attributes, stored details (with overline and protection), the stored cursor mode, resolved colors, the App's stored buffer state and the DECLRMM margins. */
const screenOf = value => {
    const screen = screenSnapshot(value, true, true, true, true, true, true, false, true, true);
    validateDslScreen(screen);
    return screen;
};

/** Total read groups across the contracts, plus start, bind and finish frames. */
const frameCount = () => 3 + dslContracts.reduce((sum, contract) => sum + planReads(contract), 0);

/** Every frame carries the timing the parent took for the exchange before it. The record must be the canonical one, and the run's start frame (nothing ran before it) must read zero. */
function validateTimings(frames) {
    for (const [index, frame] of frames.entries()) {
        const timing = validateTiming(frame.timing);
        if (JSON.stringify(timing) !== JSON.stringify(frame.timing)) throw new Error(`${label} frame ${index} carries a timing record that is not the canonical one.`);
        if (index === 0 && timing.elapsedMs !== 0) throw new Error(`${label} start frame carries a timing reading.`);
    }
}

/** Validate journal, run identity, binding stability and restoration. The run starts, binds and finishes on the host's own binding and nothing recolors it, so every frame must carry the same one. Only
 *  report reads carry a reply list; start, bind and finish never do. */
export function validateDslEvidence(frames, run, runId, adapter) {
    validateGradedJournal(frames, { frameCount: frameCount(), prefix: dslPrefix, noncePrefix: dslNoncePrefix, runId, label, screenOf });
    const adapterIdentityOk = adapter !== null && adapter !== undefined && adapter.id === dslAdapterId && run.adapterId === dslAdapterId
        && isDigest(adapter.bundleSha256) && isDigest(adapter.upstreamBundleSha256) && adapter.derivedFromAdapterId === derivedFrom;
    if (!adapterIdentityOk) throw new Error(`Invalid ${label} adapter identity.`);
    const coversCases = JSON.stringify(run.probeIds) === JSON.stringify([...dslCaseIds]) && Array.isArray(run.cases) && run.cases.length === dslContracts.length;
    if (!coversCases) throw new Error(`${label} run does not cover the contracted cases.`);
    const hash = colorBindingHash(frames[0].screen.colorBinding);
    if (frames.some(frame => colorBindingHash(frame.screen.colorBinding) !== hash)) throw new Error(`${label} observation saw the palette or theme change.`);
    for (const edge of [frames[0], frames[1], frames.at(-1)]) {
        if (edge.report !== undefined) throw new Error(`Unexpected ${label} report frame.`);
    }
    validateTimings(frames);
}

const repliesOf = (contract, recorded, readFrames) =>
    suiteReplyLists({ label, contract, recorded, readFrames, validateExchange: validateDslExchange });
const verifyPinnedReplays = (contract, recorded, readFrames, { replayModule = null } = {}) => {
    const { termless, terminal } = recorded.pinned ?? {};
    if (termless?.probeId !== contract.caseId || terminal?.probeId !== contract.caseId || !termless.observation || !terminal.observation) throw new Error(`Missing pinned ${contract.caseId} grading results.`);
    const termlessExchange = validateDslExchange(readFrames[contract.pinnedRead].report), terminalExchange = validateDslExchange(readFrames[contract.pinnedTerminalRead].report);
    requirePinnedAgreement(contract, termless, replayDslTermless(contract.caseId, termlessExchange.replies.join('')), label);
    requirePinnedAgreement(contract, terminal, replayDslTerminal(contract.caseId, terminalExchange.replies), label);
    if (replayModule) {
        const termlessContext = dslRecordingContext(contract, termlessExchange);
        requireReplayAgreement(contract, replayModule.ntildeRunDslTermless(contract.caseId, termlessContext.context), termless);
        if (termlessContext.callCount !== termlessContext.expectedCalls) throw new Error(`Pinned ${contract.caseId} replay made a different number of calls.`);
    }
    return { termless, terminal };
};

/** Derive one case's published record from its journaled frames, worker record and the graded effect. */
function deriveDslCase(contract, recorded, { replies, observation, effect, termless, terminal, contractsVersion, edgeBinding }) {
    const theme = edgeBinding, observed = contract.observed(observation);
    const divergence = reconcilePinnedWithEffect(termless.observation.outcome, effect);
    const reads = recorded.reads.map((read, index) => ({ kind: read.kind, cursor: read.cursor, size: read.size, ...(read.kind === 'report' ? { replies: replies[index] } : {}) }));
    const binding = { reverseVideo: theme.reverseVideo, themeName: theme.themeName, foregroundArgb: theme.foregroundArgb, backgroundArgb: theme.backgroundArgb,
        minimumContrast: theme.minimumContrast, paletteOverrideIndices: [...theme.paletteOverrideIndices], bindingSha256: colorBindingHash(theme) };
    return { caseId: contract.caseId, pinnedCriterion: contract.pinnedCriterion, pinnedScript: observed.pinnedScript, terminalFixture: contract.terminalFixture,
        feeds: recorded.feeds, barriers: recorded.barriers,
        reads,
        observed,
        colorBinding: binding,
        pinned: { termless: suitePinnedResult(termless), terminal: suitePinnedResult(terminal) },
        effectContract: { id: dslEffectContractId, version: contractsVersion, outcome: effect.outcome, ...(effect.note ? { note: effect.note } : {}),
            attribution: 'separately identified Ntilde effect contract; not the pinned probe' },
        contractResult: effect, ...(divergence ? { divergence } : {}),
        conclusion: { outcome: termless.observation.outcome, evidence: 'owned-raw-replies-state-and-timing', grading: 'pinned-termless-replayed', note: termless.observation.note ?? null,
            reason: termless.observation.reason ?? null, scope: contract.appearanceScope } };
}

/** Re-derive and cross-check every case; throw on any disagreement. */
export function buildDslGradingReport(frames, run, runId, adapter, { contractsVersion = dslContractsVersion, replayModule = null } = {}) {
    validateDslEvidence(frames, run, runId, adapter);
    let frameIndex = 2;
    const cases = [];
    for (const [position, contract] of dslContracts.entries()) {
        const recorded = run.cases[position];
        if (recorded.caseId !== contract.caseId) throw new Error(`${label} case order differs from the contracts.`);
        validateGradedCaptureRecord(contract, recorded, label);
        const readFrames = recorded.reads.map(() => frames[frameIndex++]);
        crossCheckGradedCapture(contract, recorded, readFrames, label);
        for (const [index, read] of recorded.reads.entries()) {
            if (JSON.stringify(read.size) !== JSON.stringify([readFrames[index].screen.rows, readFrames[index].screen.cols])) throw new Error(`${label} ${contract.caseId} size differs from its read frame.`);
        }
        const replies = repliesOf(contract, recorded, readFrames);
        const observation = dslObservation(contract, readFrames, replies, dslNoncePrefix + runId);
        const effect = gradeDslCase(contract, observation, contractsVersion);
        const { termless, terminal } = verifyPinnedReplays(contract, recorded, readFrames, { replayModule });
        cases.push(deriveDslCase(contract, recorded, { replies, observation, effect, termless, terminal, contractsVersion, edgeBinding: frames[0].screen.colorBinding }));
    }
    return { schemaVersion: 1, suiteId: dslSuiteId, runId, contractsVersion, adapter, scope, initialSnapshot: 0, restoredSnapshot: frames.length - 1, cases, snapshots: frames };
}
