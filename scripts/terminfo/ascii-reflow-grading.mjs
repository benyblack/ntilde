// Parent-side report builder for the ASCII reflow qualification (extensions.reflow). Re-derives every conclusion from the
// retained parent journals (frames, parent-owned resize actions and the worker run record), refuses any tampering, and
// requires the pinned probe's own result to agree with its replay over the journaled declared capability. The pinned verdict
// is inconclusive by design and is preserved as the conclusion; the separately identified effect contract carries the graded
// verdict from the independent reflow model.
import { screenSnapshot } from './agent-observer.mjs';
import { validateJournalFrames } from './resize.mjs';
import { pristineEditing } from './editing-capture.mjs';
import { requirePinnedAgreement, requireReplayAgreement } from './graded-plan.mjs';
import { asciiReflowCaseIds, asciiReflowContractsVersion, asciiReflowEffectContractId, asciiReflowNoncePrefix, asciiReflowPinned, asciiReflowSuiteId,
    asciiReflowFinalRestore, gradeReflowCase, makeReflowCases, matchesReflowState, briefReflowObservation } from './ascii-reflow-contracts.mjs';
import { asciiReflowAdapterId } from './ascii-reflow-grading-collector.mjs';
import { reflowRecordingContext } from './ascii-reflow-collector.mjs';

const label = 'ASCII reflow';
const derivedFrom = 'ntilde-owned-osc-title-grading-v1';
export const asciiReflowScope = 'Pinned terminfo.dev@4.0.0 extensions.reflow code (no fixture, no resize request, no cell readback; both arms always '
    + 'inconclusive by design) replayed byte-exactly over the journaled declared capability and preserved, beside a separately identified effect contract '
    + 'graded from paired complete stored cells, cursor, modes, attributes and complete ordered history at three actual geometries per experiment, proven by '
    + 'parent-selected App resize actions with independent kernel geometry and output-processing proof: an independent character-slicing model must reproduce '
    + 'the width round trip (wrapped hard lines redistribute, short lines, authored single spaces and a bold+underline probe stay intact, the cursor follows '
    + 'its logical offset, restoring rejoins every line) and the history round trip (complete ordered history displacement, cursor at content end, '
    + 'top-anchored restoration with bottom padding) in a disposable main screen buffer. Stored ASCII state only: the alternate screen is observed once as '
    + 'the nonce-bind control; no Unicode, wide-cell, grapheme or history-attribute claim, no images, pixels or rendered appearance, no saved banks, remote '
    + 'delivery or other platforms, and no stock grades.';

const same = (left, right) => JSON.stringify(left) === JSON.stringify(right);
const check = value => { if (!value) throw new Error('Invalid or incomplete owned ASCII reflow evidence.'); };
const geometry = screen => ({ rows: screen.rows, cols: screen.cols });

/** The pinned parserStateResult-free result the probe always returns, key order included, over the undeclared capability. */
export function replayReflowPinned() {
    const note = asciiReflowPinned.note;
    return { pass: false, response: '{"declaredReflow":false}', note,
        observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'legacy', note } };
}

/** Journal shape, run identity and adapter derivation. */
function checkReflowIdentity(frames, actions, run, runId, adapter) {
    check(/^[a-f\d]{32}$/.test(runId ?? '') && Array.isArray(frames) && frames.length === 9
        && Array.isArray(actions) && actions.length === 4 && adapter?.id === asciiReflowAdapterId
        && run?.adapterId === asciiReflowAdapterId && run.suiteId === asciiReflowSuiteId && run.runId === runId
        && same(run.probeIds, [...asciiReflowCaseIds]) && /^[a-f\d]{64}$/.test(adapter.bundleSha256 ?? '')
        && /^[a-f\d]{64}$/.test(adapter.upstreamBundleSha256 ?? '') && adapter.derivedFromAdapterId === derivedFrom);
}

/** The run record: byte-exact fixture feeds, barriers aligned with their reads, plan order and read cursors. */
function checkReflowRunRecord(run, cases, plan, frames) {
    check(same(run.feeds, [cases[0].setup, cases[1].setup, asciiReflowFinalRestore])
        && Array.isArray(run.barriers) && run.barriers.length === 3
        && run.barriers.every(barrier => typeof barrier === 'string' && /^\x1b\[\?[\d;]+c$/.test(barrier))
        && run.barriers[0] === frames[2].barrier && run.barriers[1] === frames[5].barrier && run.barriers[2] === frames[8].barrier
        && same(run.stages, plan.map(stage => ({ caseId: stage.caseId, stageId: stage.stageId })))
        && Array.isArray(run.reads) && run.reads.length === 6
        && run.reads.every((read, index) => same(read.cursor, [frames[index + 2].screen.cursorRow, frames[index + 2].screen.cursorCol])));
}

/** Validate journal, actions, run identity, binding stability and restoration; return the derived cases. */
export function validateReflowEvidence(frames, actions, run, runId, adapter) {
    checkReflowIdentity(frames, actions, run, runId, adapter);
    const first = frames[0], last = frames.at(-1), original = geometry(first.screen ?? {});
    const cases = makeReflowCases(original.rows, original.cols);
    const plan = cases.flatMap(item => ['control', 'after', 'restored'].map(stageId => ({
        caseId: item.id, stageId, target: stageId === 'after' ? item.target : original,
    })));
    validateJournalFrames(frames, actions, { plan, original, runId, kindPrefix: 'reflow', ensure: check });
    check(pristineEditing(first.screen) && same(first.screen, last.screen));
    const bound = structuredClone(first.screen);
    bound.lines[0] = asciiReflowNoncePrefix + runId; bound.cursorCol = (asciiReflowNoncePrefix + runId).length;
    bound.bufferState.alternateScreen = true;
    check(same(bound, frames[1].screen));
    checkReflowRunRecord(run, cases, plan, frames);
    return cases;
}

/** One graded stage record: actual geometry, cursor, history count and the exact-model comparison. */
function stageOf(screen, expected) {
    const projection = screenSnapshot(screen, false, true, true, true, true);
    return { screen: projection, geometry: geometry(projection), cursor: [projection.cursorRow, projection.cursorCol],
        historyCount: projection.history.lines.length, matches: matchesReflowState(projection, expected) };
}

/** Re-derive and cross-check the case; throw on any disagreement. */
export function buildReflowReport(frames, actions, run, runId, adapter, { contractsVersion = asciiReflowContractsVersion, replayModule = null } = {}) {
    const cases = validateReflowEvidence(frames, actions, run, runId, adapter);
    const stages = {
        width: { initial: stageOf(frames[2].screen, cases[0].before), shrunk: stageOf(frames[3].screen, cases[0].after),
            restored: stageOf(frames[4].screen, cases[0].restored) },
        history: { initial: stageOf(frames[5].screen, cases[1].before), shrunk: stageOf(frames[6].screen, cases[1].after),
            restored: stageOf(frames[7].screen, cases[1].restored) },
    };
    const effect = gradeReflowCase(stages, contractsVersion);
    const pinned = run.pinned;
    const expectedPinned = replayReflowPinned();
    check(pinned?.probeId === 'extensions.reflow' && pinned.observation?.evidence === 'legacy'
        && pinned.observation?.outcome === 'inconclusive' && pinned.pass === false
        && typeof pinned.response === 'string' && pinned.note === asciiReflowPinned.note);
    requirePinnedAgreement({ caseId: 'extensions.reflow' }, pinned, expectedPinned, label);
    if (replayModule) {
        const context = reflowRecordingContext();
        requireReplayAgreement({ caseId: 'extensions.reflow' }, replayModule.ntildeRunReflowTermless('extensions.reflow', context), pinned);
        if (JSON.stringify(context.readCapabilities) !== JSON.stringify(['reflow'])) throw new Error(`Pinned ${label} replay read an uncontracted capability.`);
    }
    const divergence = effect.outcome === 'inconclusive' ? null
        : { pinnedOutcome: pinned.observation.outcome, contractOutcome: effect.outcome, kind: 'pinned-inconclusive-by-design' };
    const caseRecord = {
        caseId: 'extensions.reflow', pinnedCriterion: asciiReflowPinned.pinnedCriterion, pinnedFeeds: asciiReflowPinned.feeds,
        terminalFixture: asciiReflowPinned.terminalFixture, feeds: run.feeds, barriers: run.barriers,
        reads: run.reads.map(read => read.cursor), observed: briefReflowObservation(stages),
        pinned: { pass: pinned.pass, response: pinned.response, note: pinned.note ?? null, observation: pinned.observation, assertions: pinned.assertions ?? null },
        effectContract: { id: asciiReflowEffectContractId, version: contractsVersion, outcome: effect.outcome, ...(effect.note ? { note: effect.note } : {}),
            attribution: 'separately identified Ntilde effect contract; not the pinned probe' },
        contractResult: effect, ...(divergence ? { divergence } : {}),
        conclusion: { outcome: pinned.observation.outcome, evidence: 'owned-stored-cells-and-scrollback', grading: 'pinned-termless-preserved-by-design',
            note: pinned.note ?? null, scope: asciiReflowScope },
    };
    return { schemaVersion: 1, suiteId: asciiReflowSuiteId, runId, contractsVersion, adapter, scope: asciiReflowScope,
        initialSnapshot: 0, restoredSnapshot: frames.length - 1, cases: [caseRecord], snapshots: frames, actions };
}

/** The receipt/inventory outcome counts of a graded report. */
export function reflowCounts(report) {
    const counts = {};
    for (const item of report.cases) counts[item.contractResult.outcome] = (counts[item.contractResult.outcome] ?? 0) + 1;
    return counts;
}
