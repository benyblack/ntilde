// Parent-side report builder for the DECCOLM qualification. Re-derives every conclusion from the retained parent journal (screens, kernel PTY grids and the raw reply lists bound to their barriers) and the
// independent contract grader, refuses any tampering, and requires both pinned paths' own grading to agree: the termless result is a pure function of the journaled replies and App grid width of its own
// exchange, the terminal result of the journaled replies of the pinned mode query. The pinned verdict is the replayed pinned code's own (inconclusive on both paths: a DECRPM state is not a width
// transition, and mode 3 is not recognized); the separately identified effect contract carries the graded conclusion (unsupported: DECCOLM ignored, no grid change in the App or the kernel) and any
// difference is recorded as a divergence, never absorbed.
import { validateGradedJournal, crossCheckGradedCapture, requirePinnedAgreement, requireReplayAgreement, validateGradedCaptureRecord, planReads } from './graded-plan.mjs';
import { suiteReplyLists, reconcilePinnedWithEffect, suitePinnedResult } from './probe-suite-shared.mjs';
import { colorBindingHash } from './resolved-colors.mjs';
import { screenSnapshot } from './agent-observer.mjs';
import { validateDclExchange } from './dcl-report.mjs';
import { dclContracts, dclCaseIds, dclContractsVersion, dclNoncePrefix, dclPrefix, dclSuiteId, dclEffectContractId, gradeDclCase, dclObservation,
    validateDclScreen, replayDclTermless, replayDclTerminal } from './dcl-contracts.mjs';
import { dclAdapterId } from './dcl-grading-collector.mjs';
import { dclRecordingContext } from './dcl-collector.mjs';
import { kernelGrid } from './dcl-capture.mjs';

const label = 'deccolm';
const derivedFrom = 'ntilde-owned-left-right-margins-grading-v1';
const isDigest = value => /^[a-f\d]{64}$/.test(value ?? '');
const scope = 'Pinned terminfo.dev@4.0.0 criteria for modes.deccolm (decrpmModeProbe("modes.deccolm", 3, ...): the termless path feeds CSI ? 3 h, captures the CSI ? 3 $ p reply and feeds CSI ? 3 l and returns '
    + 'parserStateResult(null, ...), inconclusive by construction; the terminal path asks queryMode(3) and returns decrpmResult, inconclusive while mode 3 is not recognized): both pinned paths replayed '
    + 'byte-exactly over the journaled replies and App grid width of their own fenced exchanges, beside the separately identified effect contract ntilde-deccolm-unsupported-effect-v1 graded by independent '
    + 'literals written from xterm\'s DECCOLM and allow-switching (mode 40) contract: CSI ? 3 h / l alone, with switching allowed and not allowed, and a set/reset cycle, are ignored (policy, #1000: the App has no '
    + 'program-initiated resize path), leave the complete screen, cursor, rendition, margins, modes and active screen unchanged, keep the App grid and the independently read kernel PTY grid at their baseline size '
    + '(a width other than 80 and 132, so either direction would be visible) and leave mode 3 and mode 40 answered not recognized, beside a recognised-mode control and a clear/home control. Raw terminal replies, '
    + 'stored state and PTY geometry, not a window size or painted pixels; no stock grades.';

/** Feature-reporting screens carry attributes, stored details (with overline and protection), the stored cursor mode, resolved colors, the App's stored buffer state and the DECLRMM margins. */
const screenOf = value => {
    const screen = screenSnapshot(value, true, true, true, true, true, true, false, true, true);
    validateDclScreen(screen);
    return screen;
};

/** Total read groups across the contracts, plus start, bind and finish frames. */
const frameCount = () => 3 + dclContracts.reduce((sum, contract) => sum + planReads(contract), 0);

/** Every frame carries the kernel grid the parent read right after the paired App read, as the system stty reported it. The grid itself is not required to equal the App's here (a disagreement is a
 *  graded observation of the read groups); the run's own start, bind and finish frames must agree, since nothing resized the pane around them. */
function validateKernel(frames) {
    for (const [index, frame] of frames.entries()) {
        const grid = kernelGrid(frame.kernel);
        if (JSON.stringify(grid) !== JSON.stringify(frame.kernel)) throw new Error(`${label} frame ${index} carries a kernel grid that is not the canonical one.`);
        const edge = index < 2 || index === frames.length - 1;
        if (edge && (grid.rows !== frame.screen.rows || grid.cols !== frame.screen.cols)) throw new Error(`${label} edge frame ${index} kernel grid differs from the App grid.`);
    }
}

/** Validate journal, run identity, binding stability and restoration. The run starts, binds and finishes on the host's own binding and nothing recolors it, so every frame must carry the same one. Only
 *  report reads carry a reply list; start, bind and finish never do. */
export function validateDclEvidence(frames, run, runId, adapter) {
    validateGradedJournal(frames, { frameCount: frameCount(), prefix: dclPrefix, noncePrefix: dclNoncePrefix, runId, label, screenOf });
    const adapterIdentityOk = adapter !== null && adapter !== undefined && adapter.id === dclAdapterId && run.adapterId === dclAdapterId
        && isDigest(adapter.bundleSha256) && isDigest(adapter.upstreamBundleSha256) && adapter.derivedFromAdapterId === derivedFrom;
    if (!adapterIdentityOk) throw new Error(`Invalid ${label} adapter identity.`);
    const coversCases = JSON.stringify(run.probeIds) === JSON.stringify([...dclCaseIds]) && Array.isArray(run.cases) && run.cases.length === dclContracts.length;
    if (!coversCases) throw new Error(`${label} run does not cover the contracted cases.`);
    const hash = colorBindingHash(frames[0].screen.colorBinding);
    if (frames.some(frame => colorBindingHash(frame.screen.colorBinding) !== hash)) throw new Error(`${label} observation saw the palette or theme change.`);
    for (const edge of [frames[0], frames[1], frames.at(-1)]) {
        if (edge.report !== undefined) throw new Error(`Unexpected ${label} report frame.`);
    }
    validateKernel(frames);
}

const repliesOf = (contract, recorded, readFrames) =>
    suiteReplyLists({ label, contract, recorded, readFrames, validateExchange: validateDclExchange });
const verifyPinnedReplays = (contract, recorded, readFrames, { replayModule = null } = {}) => {
    const { termless, terminal } = recorded.pinned ?? {};
    if (termless?.probeId !== contract.caseId || terminal?.probeId !== contract.caseId || !termless.observation || !terminal.observation) throw new Error(`Missing pinned ${contract.caseId} grading results.`);
    const termlessExchange = validateDclExchange(readFrames[contract.pinnedRead].report), terminalExchange = validateDclExchange(readFrames[contract.pinnedTerminalRead].report);
    const cols = readFrames[contract.pinnedRead].screen.cols;
    requirePinnedAgreement(contract, termless, replayDclTermless(contract.caseId, termlessExchange.replies.join(''), cols), label);
    requirePinnedAgreement(contract, terminal, replayDclTerminal(contract.caseId, terminalExchange.replies), label);
    if (replayModule) {
        const termlessContext = dclRecordingContext(contract, termlessExchange, cols);
        requireReplayAgreement(contract, replayModule.ntildeRunDclTermless(contract.caseId, termlessContext.context), termless);
        if (termlessContext.callCount !== termlessContext.expectedCalls) throw new Error(`Pinned ${contract.caseId} replay made a different number of calls.`);
    }
    return { termless, terminal };
};

/** Derive one case's published record from its journaled frames, worker record and the graded effect. */
function deriveDclCase(contract, recorded, { replies, observation, effect, termless, terminal, contractsVersion, edgeBinding }) {
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
        effectContract: { id: dclEffectContractId, version: contractsVersion, outcome: effect.outcome, ...(effect.note ? { note: effect.note } : {}),
            attribution: 'separately identified Ntilde effect contract; not the pinned probe' },
        contractResult: effect, ...(divergence ? { divergence } : {}),
        conclusion: { outcome: termless.observation.outcome, evidence: 'owned-raw-replies-state-and-pty-geometry', grading: 'pinned-termless-replayed', note: termless.observation.note ?? null,
            reason: termless.observation.reason ?? null, scope: contract.appearanceScope } };
}

/** Re-derive and cross-check every case; throw on any disagreement. */
export function buildDclGradingReport(frames, run, runId, adapter, { contractsVersion = dclContractsVersion, replayModule = null } = {}) {
    validateDclEvidence(frames, run, runId, adapter);
    let frameIndex = 2;
    const cases = [];
    for (const [position, contract] of dclContracts.entries()) {
        const recorded = run.cases[position];
        if (recorded.caseId !== contract.caseId) throw new Error(`${label} case order differs from the contracts.`);
        validateGradedCaptureRecord(contract, recorded, label);
        const readFrames = recorded.reads.map(() => frames[frameIndex++]);
        crossCheckGradedCapture(contract, recorded, readFrames, label);
        for (const [index, read] of recorded.reads.entries()) {
            if (JSON.stringify(read.size) !== JSON.stringify([readFrames[index].screen.rows, readFrames[index].screen.cols])) throw new Error(`${label} ${contract.caseId} size differs from its read frame.`);
        }
        const replies = repliesOf(contract, recorded, readFrames);
        const observation = dclObservation(contract, readFrames, replies, dclNoncePrefix + runId);
        const effect = gradeDclCase(contract, observation, contractsVersion);
        const { termless, terminal } = verifyPinnedReplays(contract, recorded, readFrames, { replayModule });
        cases.push(deriveDclCase(contract, recorded, { replies, observation, effect, termless, terminal, contractsVersion, edgeBinding: frames[0].screen.colorBinding }));
    }
    return { schemaVersion: 1, suiteId: dclSuiteId, runId, contractsVersion, adapter, scope, initialSnapshot: 0, restoredSnapshot: frames.length - 1, cases, snapshots: frames };
}
