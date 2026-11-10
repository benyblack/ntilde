// Parent-side report builder for the left/right-margin qualification. Re-derives every conclusion from the retained parent journal,
// refuses any tampering, and requires the pinned probe's own result to agree with its replay over the journaled mode. The pinned verdict
// is inconclusive by design and is preserved as the conclusion; the separately identified effect contract carries the graded verdict.
import { validateGradedJournal, crossCheckGradedCapture, requirePinnedAgreement, requireReplayAgreement, validateGradedCaptureRecord,
    planReads } from './graded-plan.mjs';
import { colorBindingHash } from './resolved-colors.mjs';
import { screenSnapshot } from './agent-observer.mjs';
import { lrMarginsContracts, lrMarginsCaseIds, lrMarginsContractsVersion, lrMarginsNoncePrefix, lrMarginsSuiteId, gradeLrMarginsCase,
    lrMarginsObservation, validateLrMarginsScreen, pinned as pinnedFixture } from './lr-margins-contracts.mjs';
import { lrMarginsAdapterId } from './lr-margins-grading-collector.mjs';
import { lrMarginsRecordingContext } from './lr-margins-collector.mjs';

const label = 'Left/right-margin';
const derivedFrom = 'ntilde-owned-rectangle-checksum-grading-v1';
const scope = 'Pinned terminfo.dev@4.0.0 modes.left-right-margin termless code (ESC[?69h, the mode metadata, ESC[?69l; always inconclusive by design) replayed '
    + 'byte-exactly on a disposable App and preserved, beside a separately identified effect contract graded from parent-journaled stored cells, the cursor and '
    + 'the App-stored region, left/right margins, DECLRMM, origin and insert mode: an independent margins model must reproduce every control over a block seed '
    + '(SCOSC without DECLRMM, DECSLRM homing and refusals, wrap at the right margin, insert mode, CUF/CUB/CR/HT bounds, LF/RI/SU/SD band scrolls and the '
    + 'outside-margin no-op, IL/DL, ICH/DCH, DECIC/DECDC/SL/SR, DECOM addressing and rectangles, DECSTR and ?69 reset). Stored ASCII cells only: no CPR, wide-cell, '
    + 'side-table, image-plane or rendered-appearance claim; no stock grades.';

/** Margin screens carry attributes, stored details with protection, resolved colors, buffer state and the stored horizontal margins. */
const screenOf = value => {
    const screen = screenSnapshot(value, true, true, false, true, true, true, false, true, true);
    validateLrMarginsScreen(screen);
    return screen;
};

/** Validate journal, run identity, binding stability and restoration. */
export function validateLrMarginsEvidence(frames, run, runId, adapter) {
    const frameCount = 3 + lrMarginsContracts.reduce((sum, contract) => sum + planReads(contract), 0);
    validateGradedJournal(frames, { frameCount, prefix: 'lrmm', noncePrefix: lrMarginsNoncePrefix, runId, label, screenOf });
    if (!adapter || adapter.id !== lrMarginsAdapterId || run.adapterId !== lrMarginsAdapterId
        || !/^[a-f\d]{64}$/.test(adapter.bundleSha256 ?? '') || !/^[a-f\d]{64}$/.test(adapter.upstreamBundleSha256 ?? '')
        || adapter.derivedFromAdapterId !== derivedFrom) {
        throw new Error(`Invalid ${label} adapter identity.`);
    }
    if (JSON.stringify(run.probeIds) !== JSON.stringify([...lrMarginsCaseIds]) || !Array.isArray(run.cases) || run.cases.length !== lrMarginsContracts.length) {
        throw new Error(`${label} run does not cover the contracted cases.`);
    }
    const binding = colorBindingHash(frames[0].screen.colorBinding);
    if (frames.some(frame => colorBindingHash(frame.screen.colorBinding) !== binding)) throw new Error(`${label} observation saw the palette or theme change.`);
}

/** The pinned parserStateResult(null, ...) the probe always returns, key order included, over the journaled mode. */
export function replayLrMarginsPinned(contract, screens) {
    const pass = screens[contract.setRead].horizontalMargins.leftRightMarginMode === true;
    const response = JSON.stringify({ mode: pass });
    return { pass: false, response, note: pinnedFixture.note,
        observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'parser-state', note: pinnedFixture.note } };
}

/** The pinned verdict is inconclusive by design: any other pinned outcome is an error; a decided effect verdict is a recorded difference. */
function reconcile(contract, pinnedOutcome, effect) {
    if (pinnedOutcome !== 'inconclusive') throw new Error(`Pinned ${contract.caseId} must be inconclusive by design.`);
    return effect.outcome === 'inconclusive' ? null : { pinnedOutcome, contractOutcome: effect.outcome, kind: 'pinned-inconclusive-by-design' };
}

/** Re-derive and cross-check every case; throw on any disagreement. */
export function buildLrMarginsGradingReport(frames, run, runId, adapter, { contractsVersion = lrMarginsContractsVersion, replayModule = null } = {}) {
    validateLrMarginsEvidence(frames, run, runId, adapter);
    let frameIndex = 2;
    const cases = [];
    for (const [position, contract] of lrMarginsContracts.entries()) {
        const recorded = run.cases[position];
        if (recorded.caseId !== contract.caseId) throw new Error(`${label} case order differs from the contracts.`);
        validateGradedCaptureRecord(contract, recorded, label);
        const readFrames = recorded.reads.map(() => frames[frameIndex++]);
        crossCheckGradedCapture(contract, recorded, readFrames, label);
        const screens = readFrames.map(frame => frame.screen);
        const observation = lrMarginsObservation(contract, screens);
        const effect = gradeLrMarginsCase(contract, observation, contractsVersion);
        const pinned = recorded.pinned;
        if (pinned?.probeId !== contract.caseId || pinned.observation?.evidence !== 'parser-state') throw new Error(`Missing pinned ${contract.caseId} grading result.`);
        requirePinnedAgreement(contract, pinned, replayLrMarginsPinned(contract, screens), label);
        const divergence = reconcile(contract, pinned.observation.outcome, effect);
        if (replayModule) {
            const context = lrMarginsRecordingContext(contract, screens);
            requireReplayAgreement(contract, replayModule.ntildeRunLrMarginsTermless(contract.caseId, context), pinned);
            if (context.fedCount !== contract.pinnedFeeds.length) throw new Error(`Pinned ${contract.caseId} replay fed a different number of pinned writes.`);
        }
        const theme = screens[0].colorBinding;
        cases.push({ caseId: contract.caseId, pinnedCriterion: contract.pinnedCriterion, pinnedFeeds: contract.pinnedFeeds,
            terminalFixture: contract.terminalFixture, feeds: recorded.feeds, barriers: recorded.barriers,
            reads: recorded.reads.map(read => read.cursor), observed: contract.observed(observation),
            colorBinding: { reverseVideo: theme.reverseVideo, themeName: theme.themeName, foregroundArgb: theme.foregroundArgb, backgroundArgb: theme.backgroundArgb,
                minimumContrast: theme.minimumContrast, paletteOverrideIndices: [...theme.paletteOverrideIndices], bindingSha256: colorBindingHash(theme) },
            pinned: { pass: pinned.pass, response: pinned.response, note: pinned.note ?? null, observation: pinned.observation, assertions: pinned.assertions ?? null },
            effectContract: { id: contract.effectContract, version: contractsVersion, outcome: effect.outcome, ...(effect.note ? { note: effect.note } : {}),
                attribution: 'separately identified Ntilde effect contract; not the pinned probe' },
            contractResult: effect, ...(divergence ? { divergence } : {}),
            conclusion: { outcome: pinned.observation.outcome, evidence: 'owned-stored-cells-and-margins', grading: 'pinned-termless-preserved-by-design',
                note: pinned.note ?? null, scope: contract.appearanceScope } });
    }
    return { schemaVersion: 1, suiteId: lrMarginsSuiteId, runId, contractsVersion, adapter, scope, initialSnapshot: 0,
        restoredSnapshot: frames.length - 1, cases, snapshots: frames };
}
