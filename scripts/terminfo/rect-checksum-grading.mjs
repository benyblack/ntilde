// Parent-side report builder for the rectangle-checksum qualification. Re-derives every conclusion from the retained parent
// journal (screens and the raw replies bound to their barriers) and the independent contract grader, refuses any tampering, and
// requires the pinned probe's own grading to agree: the pinned result is a pure function of the journaled reply of its own
// fixture. The contract may never be more permissive than the pinned probe; any difference in verdict semantics is recorded with
// both verdicts.
import { validateGradedJournal, crossCheckGradedCapture, requirePinnedAgreement, requireReplayAgreement, validateGradedCaptureRecord,
    planReads } from './graded-plan.mjs';
import { colorBindingHash } from './resolved-colors.mjs';
import { screenSnapshot } from './agent-observer.mjs';
import { reconcile } from './sgr-color-grading-core.mjs';
import { validateChecksumReport } from './checksum-report.mjs';
import { rectChecksumContracts, rectChecksumCaseIds, rectChecksumContractsVersion, rectChecksumNoncePrefix, rectChecksumSuiteId,
    gradeRectChecksumCase, rectChecksumObservation, validateRectChecksumScreen, pinned as pinnedFixture } from './rect-checksum-contracts.mjs';
import { rectChecksumAdapterId } from './rect-checksum-grading-collector.mjs';
import { rectChecksumRecordingContext } from './rect-checksum-collector.mjs';

const label = 'Rectangle-checksum';
const derivedFrom = 'ntilde-owned-column-editing-grading-v1';
const scope = 'Pinned terminfo.dev@4.0.0 rectangle-checksum termless criterion (editing.decrqcra: ESC[1;1;1;1;1;5*y after the seed ABCDE ESC[1;1H answers a framed '
    + 'DCS 1 ! ~ hhhh ST) replayed byte-exactly on a disposable App whose run turns on AllowRectangleChecksumReport (off by default), and graded from the raw replies '
    + 'bound to their ordering barriers and the parent-journaled stored cells of the screen each request saw: an independent checksum model demands the exact reply '
    + '(character sum with the bold, blink, reverse and underline weights, negated modulo 2^16, the echoed request id) of the pinned and terminal-path spellings and of '
    + 'controls for each weighted attribute, attributes that must add nothing, omitted, zero, partly omitted and oversized borders, an ignored page parameter, the '
    + 'largest and an omitted request id, inverted rectangles, DECOM row offsets with page clipping, and six non-admitted spellings that must stay unanswered; no '
    + 'request may change the screen, cursor, margins or modes. Stored ASCII cells only: no wide, combining or supplementary character, persistent-session, '
    + 'default-off-gate or rendered-appearance claim; no stock grades.';

/** Checksum screens carry attributes, stored details (with overline and protection), resolved colors and the App's stored buffer state. */
const screenOf = value => {
    const screen = screenSnapshot(value, true, true, false, true, true, true, false, true);
    validateRectChecksumScreen(screen);
    return screen;
};

/** Total read groups across the contracts, plus start, bind and finish frames. */
const frameCount = () => 3 + rectChecksumContracts.reduce((sum, contract) => sum + planReads(contract), 0);

/** Validate journal, run identity, binding stability and restoration. */
export function validateRectChecksumEvidence(frames, run, runId, adapter) {
    validateGradedJournal(frames, { frameCount: frameCount(), prefix: 'csum', noncePrefix: rectChecksumNoncePrefix, runId, label, screenOf });
    if (!adapter || adapter.id !== rectChecksumAdapterId || run.adapterId !== rectChecksumAdapterId
        || !/^[a-f\d]{64}$/.test(adapter.bundleSha256 ?? '') || !/^[a-f\d]{64}$/.test(adapter.upstreamBundleSha256 ?? '')
        || adapter.derivedFromAdapterId !== derivedFrom) {
        throw new Error(`Invalid ${label} adapter identity.`);
    }
    if (JSON.stringify(run.probeIds) !== JSON.stringify([...rectChecksumCaseIds]) || !Array.isArray(run.cases) || run.cases.length !== rectChecksumContracts.length) {
        throw new Error(`${label} run does not cover the contracted cases.`);
    }
    // One palette/theme/render state for the whole suite.
    const binding = colorBindingHash(frames[0].screen.colorBinding);
    if (frames.some(frame => colorBindingHash(frame.screen.colorBinding) !== binding)) throw new Error(`${label} observation saw the palette or theme change.`);
    // Only report reads carry a reply; start, bind and finish never do.
    if ([frames[0], frames[1], frames.at(-1)].some(frame => frame.report !== undefined)) throw new Error(`Unexpected ${label} report frame.`);
}

/** The replies of one case's reads: each report read's journaled reply must equal the worker's record and its own barrier. */
function repliesOf(contract, recorded, readFrames) {
    return contract.plan.filter(step => step.read).map((step, index) => {
        const frame = readFrames[index], read = recorded.reads[index];
        if (step.read !== 'report') {
            if (frame.report !== undefined || read.report !== undefined) throw new Error(`Unexpected ${contract.caseId} report on a cells read.`);
            return null;
        }
        const journaled = validateChecksumReport(frame.report), worker = validateChecksumReport(read.report);
        if (JSON.stringify(journaled) !== JSON.stringify(worker) || journaled.barrier !== frame.barrier) {
            throw new Error(`${label} ${contract.caseId} reply differs from its read frame.`);
        }
        return journaled.reply;
    });
}

/** The pinned checksumResult, key order included. */
export function replayRectChecksumPinned(contract, replies) {
    const response = replies[contract.pinnedRead]?.raw ?? '';
    const supported = pinnedFixture.pattern.test(response);
    return { pass: supported, response, note: pinnedFixture.note,
        observation: { outcome: supported ? 'supported' : 'inconclusive', evidence: 'query', ...(!supported && { reason: response ? 'invalid-reply' : 'no-response' }) },
        ...(supported && { assertions: [{ kind: 'positive', expected: pinnedFixture.expected, observed: response }] }) };
}

/** Re-derive and cross-check every case; throw on any disagreement. */
export function buildRectChecksumGradingReport(frames, run, runId, adapter, { contractsVersion = rectChecksumContractsVersion, replayModule = null } = {}) {
    validateRectChecksumEvidence(frames, run, runId, adapter);
    let frameIndex = 2;
    const cases = [];
    for (const [position, contract] of rectChecksumContracts.entries()) {
        const recorded = run.cases[position];
        if (recorded.caseId !== contract.caseId) throw new Error(`${label} case order differs from the contracts.`);
        validateGradedCaptureRecord(contract, recorded, label);
        const readFrames = recorded.reads.map(() => frames[frameIndex++]);
        crossCheckGradedCapture(contract, recorded, readFrames, label);
        const replies = repliesOf(contract, recorded, readFrames);
        const screens = readFrames.map(frame => frame.screen);
        const observation = rectChecksumObservation(contract, screens, replies.map(reply => reply?.raw ?? null));
        const contractResult = gradeRectChecksumCase(contract, observation, contractsVersion);
        const pinned = recorded.pinned;
        if (pinned?.probeId !== contract.caseId || pinned.observation?.evidence !== 'query') throw new Error(`Missing pinned ${contract.caseId} grading result.`);
        requirePinnedAgreement(contract, pinned, replayRectChecksumPinned(contract, replies), label);
        const divergence = reconcile(contract, pinned.observation.outcome, contractResult);
        if (replayModule) {
            const replay = replayModule.ntildeRunRectChecksumTermless(contract.caseId, rectChecksumRecordingContext(contract, replies));
            requireReplayAgreement(contract, replay, pinned);
        }
        const theme = screens[0].colorBinding;
        cases.push({ caseId: contract.caseId, pinnedCriterion: contract.pinnedCriterion, pinnedFeeds: contract.pinnedFeeds,
            terminalFixture: contract.terminalFixture, feeds: recorded.feeds, barriers: recorded.barriers,
            reads: recorded.reads.map((read, index) => ({ cursor: read.cursor, ...(read.kind === 'report' ? { reply: replies[index]?.raw ?? null } : {}) })),
            observed: contract.observed(observation),
            colorBinding: { reverseVideo: theme.reverseVideo, themeName: theme.themeName, foregroundArgb: theme.foregroundArgb, backgroundArgb: theme.backgroundArgb,
                minimumContrast: theme.minimumContrast, paletteOverrideIndices: [...theme.paletteOverrideIndices], bindingSha256: colorBindingHash(theme) },
            pinned: { pass: pinned.pass, response: pinned.response, note: pinned.note ?? null, observation: pinned.observation, assertions: pinned.assertions ?? null },
            contractResult, ...(divergence ? { divergence } : {}),
            conclusion: { outcome: contractResult.outcome, evidence: contract.evidence, grading: 'pinned-termless-plus-contract-v' + contractsVersion,
                ...(contractResult.note ? { note: contractResult.note } : {}), scope: contract.appearanceScope } });
    }
    return { schemaVersion: 1, suiteId: rectChecksumSuiteId, runId, contractsVersion, adapter, scope,
        initialSnapshot: 0, restoredSnapshot: frames.length - 1, cases, snapshots: frames };
}
