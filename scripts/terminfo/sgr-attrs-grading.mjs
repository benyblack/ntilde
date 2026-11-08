// Parent-side report builder for the SGR attribute qualification. Re-derives
// every conclusion from the retained parent journal and the independent
// contract grader, refuses any tampering, and requires the pinned probe's own
// grading to agree: the pinned result is a pure function of the observed
// screen of its own fixture, the contract may only be at least as strict as the
// pinned probe (never more permissive), and any strictness is recorded.
import { validateGradedJournal, crossCheckGradedCapture, requirePinnedAgreement,
    requireReplayAgreement, validateGradedCaptureRecord, planReads } from './graded-plan.mjs';
import { sgrAttrsContracts, sgrAttrsCaseIds, sgrAttrsContractsVersion, sgrAttrsNoncePrefix, sgrAttrsSuiteId,
    gradeSgrAttrsCase, sgrAttrsCellAt, sgrAttrsObservation, validateSgrAttrsScreen,
    pinnedNoRenderedCellNote, pinnedUnreportedNote } from './sgr-attrs-contracts.mjs';
import { colorBindingHash } from './resolved-colors.mjs';
import { screenSnapshot } from './agent-observer.mjs';
import { sgrAttrsAdapterId } from './sgr-attrs-grading-collector.mjs';
import { sgrAttrsRecordingContext } from './sgr-attrs-collector.mjs';

/** Total read groups across the suite's contracts. */
const readCount = sgrAttrsContracts.reduce((sum, contract) => sum + planReads(contract), 0);
/** Start, bind, every read group, finish. */
const frameCount = 3 + readCount;

/** Attribute screens carry attributes, stored details and resolved colors together. */
const attributeScreen = value => {
    const screen = screenSnapshot(value, true, true, false, false, false, true);
    validateSgrAttrsScreen(screen);
    return screen;
};

/** Validate journal, run identity, binding stability and restoration. */
export function validateSgrAttrsEvidence(frames, run, runId, adapter) {
    validateGradedJournal(frames, { frameCount, prefix: 'sgattr', noncePrefix: sgrAttrsNoncePrefix,
        runId, label: 'SGR-attributes', screenOf: attributeScreen });
    if (!adapter || adapter.id !== sgrAttrsAdapterId || run.adapterId !== sgrAttrsAdapterId
        || !/^[a-f\d]{64}$/.test(adapter.bundleSha256 ?? '') || !/^[a-f\d]{64}$/.test(adapter.upstreamBundleSha256 ?? '')
        || adapter.derivedFromAdapterId !== 'ntilde-owned-sgr-rgb-grading-v1') {
        throw new Error('Invalid SGR-attributes adapter identity.');
    }
    if (JSON.stringify(run.probeIds) !== JSON.stringify([...sgrAttrsCaseIds])
        || !Array.isArray(run.cases) || run.cases.length !== sgrAttrsContracts.length) {
        throw new Error('SGR-attributes run does not cover the contracted cases.');
    }
    // One theme/render state for the whole suite: reverse video or palette drift
    // between reads would change what the controls mean and voids the run.
    const binding = colorBindingHash(frames[0].screen.colorBinding);
    if (frames.some(frame => colorBindingHash(frame.screen.colorBinding) !== binding)) {
        throw new Error('SGR-attributes observation saw the palette or theme change.');
    }
}

/** The pinned parserStateResult shape of a null verdict. */
function incomplete(response, note) {
    return { pass: false, response, note, assertions: [],
        observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'parser-state', note } };
}

/** Re-derive the complete pinned sgrProbe termless result from the observed
 * screen of the pinned fixture, mirroring the pinned code's shapes exactly:
 * the pinned probe grades supported or inconclusive, never unsupported. */
export function replaySgrAttrsPinned(contract, screens) {
    const cell = sgrAttrsCellAt(screens[contract.pinnedRead], 0, 0);
    const response = JSON.stringify(cell);
    if (cell.char !== 'X') return incomplete(response, pinnedNoRenderedCellNote);
    if (contract.pinnedCheck(cell) !== true) return incomplete(response, pinnedUnreportedNote);
    return { pass: true, response, observation: { outcome: 'supported', evidence: 'parser-state' },
        assertions: [{ kind: 'positive', expected: contract.pinnedExpected, observed: response }] };
}

/** Compare the pinned and contract verdicts. The contract is the finer grader:
 * it may grade unsupported where the pinned probe is inconclusive, and may be
 * stricter than a pinned supported (recorded as a divergence), but it must
 * never grade supported where the pinned probe did not. */
function reconcile(contract, pinnedOutcome, contractResult) {
    if (pinnedOutcome === contractResult.outcome) return null;
    if (contractResult.outcome === 'supported') {
        throw new Error(`Contract ${contract.caseId} grading is more permissive than the pinned probe.`);
    }
    if (pinnedOutcome === 'inconclusive' && contractResult.outcome === 'unsupported') {
        return { pinnedOutcome, contractOutcome: contractResult.outcome, kind: 'contract-measured-mismatch' };
    }
    if (pinnedOutcome === 'supported') {
        return { pinnedOutcome, contractOutcome: contractResult.outcome, kind: 'contract-stricter-than-pinned' };
    }
    throw new Error(`Pinned and contract ${contract.caseId} grading disagree.`);
}

/** Re-derive and cross-check every case; throw on any disagreement. */
export function buildSgrAttrsGradingReport(frames, run, runId, adapter, { contractsVersion = sgrAttrsContractsVersion, replayModule = null } = {}) {
    validateSgrAttrsEvidence(frames, run, runId, adapter);
    let frameIndex = 2;
    const caseResults = [];
    for (const [position, contract] of sgrAttrsContracts.entries()) {
        const recorded = run.cases[position];
        if (recorded.caseId !== contract.caseId) throw new Error('SGR-attributes case order differs from the contracts.');
        validateGradedCaptureRecord(contract, recorded, 'SGR-attributes');
        const readFrames = recorded.reads.map(() => frames[frameIndex++]);
        crossCheckGradedCapture(contract, recorded, readFrames, 'SGR-attributes');
        const screens = readFrames.map(frame => frame.screen);
        const observation = sgrAttrsObservation(screens);
        const contractResult = gradeSgrAttrsCase(contract, observation, contractsVersion);
        const pinned = recorded.pinned;
        if (pinned?.probeId !== contract.caseId || pinned.observation?.evidence !== 'parser-state') {
            throw new Error(`Missing pinned ${contract.caseId} grading result.`);
        }
        requirePinnedAgreement(contract, pinned, replaySgrAttrsPinned(contract, screens), 'SGR-attributes');
        const divergence = reconcile(contract, pinned.observation.outcome, contractResult);
        if (replayModule) {
            const replay = replayModule.ntildeRunSgrAttrsTermless(contract.caseId, sgrAttrsRecordingContext(contract, screens));
            requireReplayAgreement(contract, replay, pinned);
        }
        caseResults.push({ caseId: contract.caseId, pinnedCriterion: contract.pinnedCriterion,
            pinnedFeeds: contract.pinnedFeeds,
            terminalFixture: contract.terminalFixture, feeds: recorded.feeds, barriers: recorded.barriers,
            reads: recorded.reads.map(read => read.cursor), observed: contract.observed(observation),
            colorBinding: { reverseVideo: screens[0].colorBinding.reverseVideo,
                bindingSha256: colorBindingHash(screens[0].colorBinding) },
            pinned: { pass: pinned.pass, response: pinned.response, note: pinned.note ?? null,
                observation: pinned.observation, assertions: pinned.assertions ?? null },
            contractResult, ...(divergence ? { divergence } : {}),
            conclusion: { outcome: contractResult.outcome, evidence: 'owned-stored-cells',
                grading: 'pinned-termless-plus-contract-v' + contractsVersion,
                ...(contractResult.note ? { note: contractResult.note } : {}),
                scope: contract.appearanceScope } });
    }
    return { schemaVersion: 1, suiteId: sgrAttrsSuiteId, runId, contractsVersion,
        adapter, scope: 'Pinned terminfo.dev@4.0.0 sgrProbe termless criteria replayed byte-exactly on a disposable App '
            + 'and graded from parent-journaled readScreen stored attribute flags and underline style, with controls for '
            + 'initially inactive attributes, unrelated-attribute drift, DECSCNM and reset restoration. '
            + 'Stored state only: no rendered appearance, no stock grades.',
        initialSnapshot: 0, restoredSnapshot: frames.length - 1, cases: caseResults, snapshots: frames };
}
