// Parent-side report builder for the SGR standard-color RGB qualification.
// Re-derives every conclusion from the retained parent journal and the
// independent contract grader, refuses any tampering, and requires the pinned
// probe's own grading to agree — over the App-resolved RGB projection the
// named-color criterion was inconclusive for at the stored level.
import { validateGradedJournal, crossCheckGradedCapture, requirePinnedAgreement,
    requireReplayAgreement, validateGradedCaptureRecord, planFeeds, planReads } from './graded-plan.mjs';
import { sgrRgbContracts, sgrRgbCaseIds, sgrRgbContractsVersion, sgrRgbNoncePrefix,
    sgrRgbSuiteId, gradeSgrRgbCase, sgrRgbCellAt, validateSgrRgbScreen } from './sgr-rgb-contracts.mjs';
import { colorBindingHash } from './resolved-colors.mjs';
import { screenSnapshot } from './agent-observer.mjs';
import { sgrRgbAdapterId } from './sgr-rgb-grading-collector.mjs';

/** Total read groups across the suite's contracts. */
const readCount = sgrRgbContracts.reduce((sum, contract) => sum + planReads(contract), 0);
/** Start, bind, every read group, finish. */
const frameCount = 3 + readCount;

/** The RGB screens carry both projections; validation covers them together. */
const rgbScreen = value => {
    const screen = screenSnapshot(value, true, true, false, false, false, true);
    validateSgrRgbScreen(screen);
    return screen;
};

/** Validate journal, run identity, exact inputs, controls, binding coherence and restoration. */
export function validateSgrRgbEvidence(frames, run, runId, adapter) {
    validateGradedJournal(frames, { frameCount, prefix: 'sgrgb', noncePrefix: sgrRgbNoncePrefix,
        runId, label: 'SGR-RGB', screenOf: rgbScreen });
    if (!adapter || adapter.id !== sgrRgbAdapterId || run.adapterId !== sgrRgbAdapterId
        || !/^[a-f\d]{64}$/.test(adapter.bundleSha256 ?? '') || !/^[a-f\d]{64}$/.test(adapter.upstreamBundleSha256 ?? '')
        || adapter.derivedFromAdapterId !== 'ntilde-owned-sgr-colors-grading-v1') {
        throw new Error('Invalid SGR-RGB adapter identity.');
    }
    if (JSON.stringify(run.probeIds) !== JSON.stringify([...sgrRgbCaseIds])
        || !Array.isArray(run.cases) || run.cases.length !== sgrRgbContracts.length) {
        throw new Error('SGR-RGB run does not cover the contracted cases.');
    }
    // One palette/theme/render state for the whole suite: a theme change between
    // reads would resolve cells against different palettes and voids the run.
    const binding = colorBindingHash(frames[0].screen.colorBinding);
    if (frames.some(frame => colorBindingHash(frame.screen.colorBinding) !== binding)) {
        throw new Error('SGR-RGB observation saw the palette or theme change.');
    }
}

/** Re-derive the complete pinned named-color result from observed screens,
 * mirroring the pinned parserStateResult shapes exactly. */
export function replaySgrRgbPinned(contract, screens) {
    const cells = [0, 1, 2].map(col => sgrRgbCellAt(screens[0], 0, col));
    const response = JSON.stringify({ baseline: cells[0], first: cells[1], second: cells[2] });
    const expected = `${contract.caseId}: ${contract.pinnedCriterion}`;
    if (cells[0].char !== 'C' || cells[1].char !== 'X' || cells[2].char !== 'Y') {
        return incomplete(response, 'Neutral control or named-color cells were not exposed');
    }
    // The pinned distinguishability criterion over the exposed values: the
    // neutral baseline is defined (default resolves to null, never undefined),
    // both named colors expose resolved RGB, and all three differ pairwise in
    // the pinned sense.
    const neutral = cells[0][contract.channel];
    const a = cells[1][contract.channel];
    const b = cells[2][contract.channel];
    const sameRgb = (x, y) => x.r === y.r && x.g === y.g && x.b === y.b;
    const sameCellColor = (x, y) => {
        if (x === undefined || y === undefined) return false;
        if (x === null || y === null) return x === y;
        return sameRgb(x, y);
    };
    if (neutral !== undefined && a && b && !sameCellColor(neutral, a) && !sameRgb(a, b)) {
        return { pass: true, response,
            observation: { outcome: 'supported', evidence: 'parser-state' },
            assertions: [{ kind: 'positive', expected, observed: response }] };
    }
    return incomplete(response, contract.pinnedControlNote);
}

/** The pinned parserStateResult shape of a null verdict. */
function incomplete(response, note) {
    return { pass: false, response, note, assertions: [],
        observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'parser-state', note } };
}

/** Re-derive and cross-check every case; throw on any disagreement. */
export function buildSgrRgbGradingReport(frames, run, runId, adapter, { contractsVersion = sgrRgbContractsVersion, replayModule = null } = {}) {
    validateSgrRgbEvidence(frames, run, runId, adapter);
    let frameIndex = 2;
    const caseResults = [];
    for (const [position, contract] of sgrRgbContracts.entries()) {
        const recorded = run.cases[position];
        if (recorded.caseId !== contract.caseId) throw new Error('SGR-RGB case order differs from the contracts.');
        validateGradedCaptureRecord(contract, recorded, 'SGR-RGB');
        const readFrames = recorded.reads.map(() => frames[frameIndex++]);
        crossCheckGradedCapture(contract, recorded, readFrames, 'SGR-RGB');
        const screens = readFrames.map(frame => frame.screen);
        const projected = [0, 1, 2].map(col => sgrRgbCellAt(screens[0], 0, col));
        const contractResult = gradeSgrRgbCase(contract, projected, contractsVersion);
        const pinned = recorded.pinned;
        if (pinned?.probeId !== contract.caseId || pinned.observation?.evidence !== 'parser-state') {
            throw new Error(`Missing pinned ${contract.caseId} grading result.`);
        }
        // The complete pinned result is a pure function of the observed RGB
        // screens; the retained run-log values must match that derivation.
        requirePinnedAgreement(contract, pinned, replaySgrRgbPinned(contract, screens), 'SGR-RGB');
        if (pinned.observation.outcome !== contractResult.outcome) {
            throw new Error(`Pinned and contract ${contract.caseId} RGB grading disagree.`);
        }
        if (replayModule) {
            const replay = replayModule.ntildeRunSgrRgbTermless(contract.caseId,
                { feed() {}, getText() { throw new Error('unexpected'); }, getCell: (row, col) => sgrRgbCellAt(screens[0], row, col) });
            requireReplayAgreement(contract, replay, pinned);
        }
        caseResults.push({ caseId: contract.caseId, pinnedCriterion: contract.pinnedCriterion,
            terminalFixture: contract.terminalFixture, feeds: recorded.feeds, barriers: recorded.barriers,
            reads: recorded.reads.map(read => read.cursor), observed: contract.observed(projected),
            colorBinding: { ...bindingBrief(screens[0].colorBinding), bindingSha256: colorBindingHash(screens[0].colorBinding) },
            pinned: { pass: pinned.pass, response: pinned.response, note: pinned.note ?? null,
                observation: pinned.observation, assertions: pinned.assertions ?? null },
            contractResult,
            conclusion: { outcome: contractResult.outcome, evidence: 'owned-resolved-colors',
                grading: 'pinned-termless-plus-contract-v' + contractsVersion,
                ...(contractResult.note ? { note: contractResult.note } : {}),
                scope: 'App-resolved channel RGB at the storage-to-theme resolution stage of the measured theme; '
                    + 'distinct from rendered glyph appearance (no contrast adjustment, compositing or rasterisation claimed) '
                    + 'and from the stored-index conclusions of the SGR-colors suite' } });
    }
    return { schemaVersion: 1, suiteId: sgrRgbSuiteId, runId, contractsVersion,
        adapter, scope: 'Pinned terminfo.dev@4.0.0 named-color termless criteria replayed byte-exactly on a disposable App '
            + 'and graded from parent-journaled readScreen resolved RGB bound to one recorded palette/theme state. '
            + 'Stored-index conclusions remain the SGR-colors suite\'s; no glyph appearance or stock grades.',
        initialSnapshot: 0, restoredSnapshot: frames.length - 1, cases: caseResults, snapshots: frames };
}

/** The binding facts a reader needs beside every case conclusion; the digest pins
 * the whole binding (palette included) as journaled in the frames. */
function bindingBrief(binding) {
    return { stage: binding.stage, themeName: binding.themeName, reverseVideo: binding.reverseVideo,
        foregroundArgb: binding.foregroundArgb, backgroundArgb: binding.backgroundArgb,
        minimumContrast: binding.minimumContrast, paletteOverrideIndices: [...binding.paletteOverrideIndices] };
}
