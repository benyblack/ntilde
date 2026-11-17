// Parent-side rederivation of the OSC 22 qualification. No result is accepted without a complete owned metadata journal (paired complete screen, pane metadata and session status),
// exact feeds, a clean bound baseline and restoration. The pinned verdict is inconclusive by design and is preserved as the conclusion; the separately identified effect contract carries
// the graded verdict, derived here from the independent oracle in osc22-contracts.mjs, never from the production implementation.
import { pristineEditing } from './editing-capture.mjs';
import { validateFrame, validateMetadataSnapshot } from './metadata.mjs';
import { planFeeds, planReads, requireReplayAgreement, validateExactFeeds } from './graded-plan.mjs';
import { osc133Fixture, osc133Minimum, simulateOsc133 } from './osc133-contracts.mjs';
import { evaluateRead, matchesOsc133Baseline } from './osc133-grading.mjs';
import { osc22AdapterId, osc22DerivedFromAdapterId } from './osc22-grading-collector.mjs';
import { makeOsc22Experiments, osc22CaseId, osc22CaseIds, osc22ContractsVersion, osc22EffectContractId, osc22Minimum, osc22PinnedWrites, osc22Prefix,
    osc22SuiteId, replayOsc22PinnedStatic, osc22Baseline, withoutPointerSequences } from './osc22-contracts.mjs';

const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const check = (condition, message) => { if (!condition) throw new Error(message); };
const digest = value => /^[a-f\d]{64}$/.test(value ?? '');

/** The prepared OSC 133 fixture baseline plus withdrawn pointer override and actual Arrow control cursor. */
export function matchesOsc22Baseline(snapshot, first, runId, finish) {
    return same(snapshot.metadata.pointerShape, osc22Baseline) && matchesOsc133Baseline(snapshot, first, runId, finish);
}

/** The plan the observer accepts: one entry per read. */
export function osc22ObserverPlan(snapshot, runId) {
    check(snapshot.screen.rows >= Math.max(osc133Minimum.rows, osc22Minimum.rows) && snapshot.screen.cols >= Math.max(osc133Minimum.cols, osc22Minimum.cols), 'OSC 22 fixture needs a larger grid.');
    return makeOsc22Experiments(runId).flatMap(item => item.plan.filter(step => step.read).map(() => ({})));
}

function validateJournal(frames, contracts, runId) {
    const count = 3 + contracts.reduce((sum, item) => sum + planReads(item), 0);
    check(/^[a-f\d]{32}$/.test(runId ?? '') && Array.isArray(frames) && frames.length === count && count <= 64, 'Incomplete OSC 22 journal.');
    const first = validateMetadataSnapshot(frames[0]?.before, undefined, true);
    check(pristineEditing(first.screen) && first.screen.rows >= osc133Minimum.rows && first.screen.cols >= osc133Minimum.cols, 'OSC 22 fixture needs a pristine private main screen.');
    for (const [index, frame] of frames.entries()) validateFrame(frame, index, count - 1, first, { caseId: null, stageId: null }, osc22Prefix, true);
    check(matchesOsc22Baseline(frames[1].before, first, runId, false) && matchesOsc22Baseline(frames.at(-1).before, first, runId, true), 'OSC 22 binding or restoration failed.');
    for (const frame of frames) check(frame.before.screen.colorBinding !== undefined, 'OSC 22 needs color bindings.');
}

function evaluateExperiments(frames, run, contracts, runId) {
    const fixture = osc133Fixture(runId), bound = frames[1];
    const evaluated = [];
    let frameIndex = 2;
    for (const [position, contract] of contracts.entries()) {
        const record = run.experiments[position];
        check(record?.caseId === contract.caseId, 'OSC 22 experiment order differs.');
        validateExactFeeds(contract, record.feeds, 'contract');
        check(Array.isArray(record.barriers) && record.barriers.length === planFeeds(contract).length && record.barriers.every(value => /^\x1b\[\?[\d;]+c$/.test(value ?? ''))
            && Array.isArray(record.reads) && record.reads.length === planReads(contract), 'Invalid OSC 22 capture record.');
        const feeds = [], reads = [];
        for (const step of contract.plan) {
            if (step.feed !== undefined) { feeds.push(step.feed); continue; }
            const frame = frames[frameIndex++], recorded = record.reads[reads.length];
            check(recorded.kind === step.read && frame.barrier === record.barriers[feeds.length - 1]
                && same(recorded.cursor, [frame.before.screen.cursorRow, frame.before.screen.cursorCol]), 'OSC 22 capture differs from its journal.');
            const modeled = feeds.map(feed => (feed === fixture.stage ? feed : withoutPointerSequences(feed)));
            const stored = evaluateRead(frame, simulateOsc133(modeled, fixture), bound, fixture);
            reads.push({ label: step.read, snapshot: frame.index, stored, pointer: frame.before.metadata.pointerShape ?? null, expected: contract.expected });
        }
        evaluated.push({ id: contract.caseId, name: contract.name, role: contract.role, compareEnded: contract.compareEnded ?? null, reads });
    }
    return evaluated;
}

/** Missing readback, active hover override or broken stored-state controls cannot establish failure. */
function grade(experiments) {
    const reads = experiments.flatMap(experiment => experiment.reads.map(read => ({ experiment, read })));
    const matched = read => same(read.pointer, read.expected);
    const validity = [
        { id: 'independent-cursor-observable', held: reads.every(({ read }) => read.pointer !== null && read.pointer.controlCursor !== 'other' && read.pointer.linkOverride === false) },
        { id: 'stored-state-unaffected', held: reads.every(({ read }) => read.stored.passed) },
        ...reads.filter(({ experiment }) => experiment.role === 'control').map(({ experiment, read }) => ({ id: experiment.name, held: matched(read) })),
    ];
    const required = reads.filter(({ experiment }) => experiment.role !== 'control')
        .map(({ experiment, read }) => ({ id: experiment.name, held: matched(read) }));
    return { outcome: !validity.every(item => item.held) ? 'inconclusive' : required.every(item => item.held) ? 'supported' : 'unsupported', validity, required };
}

/** The pinned terminal path rebuilt from its journaled cursor alone: the verdict depends on no observation. */
function checkPinned(run) {
    const stock = run.experiments.find(item => item.caseId === `${osc22CaseId}/stock`);
    check(stock?.reads?.length === 1 && same(run.pinned, [replayOsc22PinnedStatic(stock.reads[0].cursor)]), 'OSC 22 pinned result differs from its replay.');
}

/** Run the PINNED terminal-path code (from the retained derived bundle) over the journaled cursor; it must reproduce the retained result byte for byte. */
export async function verifyOsc22PinnedCode(run, replayModule) {
    const [recorded] = run.pinned, writes = [];
    check(replayModule.ntildeOsc22HasTermless(osc22CaseId) === false, 'Pinned OSC 22 termless presence changed.');
    const served = { write: text => { writes.push(text); }, queryCursorPosition: () => Promise.resolve({ row: recorded.cursor[0], col: recorded.cursor[1] }) };
    const { probeId, ...terminal } = await replayModule.ntildeRunOsc22Terminal(osc22CaseId, served);
    check(probeId === osc22CaseId && same(writes, [...osc22PinnedWrites]), 'Pinned OSC 22 terminal replay changed.');
    requireReplayAgreement({ caseId: osc22CaseId }, terminal, recorded.terminal);
}

/** Build the whole report from the retained journal and run log. */
export function buildOsc22GradingReport(frames, run, runId, adapter) {
    const contracts = makeOsc22Experiments(runId);
    validateJournal(frames, contracts, runId);
    check(adapter?.id === osc22AdapterId && adapter.derivedFromAdapterId === osc22DerivedFromAdapterId && digest(adapter.bundleSha256) && digest(adapter.upstreamBundleSha256), 'Invalid OSC 22 adapter.');
    check(run?.schemaVersion === 1 && run.adapterId === adapter.id && same(run.probeIds, [...osc22CaseIds])
        && run.experiments?.length === contracts.length && run.pinned?.length === osc22CaseIds.length, 'Invalid OSC 22 run.');
    const experiments = evaluateExperiments(frames, run, contracts, runId);
    checkPinned(run);
    const result = grade(experiments);
    const effect = { id: osc22EffectContractId, version: osc22ContractsVersion, ...result, attribution: 'separately identified Ntilde effect contract; not the pinned probe' };
    const item = { caseId: osc22CaseId, pinned: run.pinned[0], conclusion: { outcome: 'inconclusive', grading: 'pinned-terminal-unmeasured-preserved-by-design' },
        contractResult: { outcome: result.outcome }, effectContract: effect,
        ...(result.outcome === 'inconclusive' ? {} : { divergence: { pinnedOutcome: 'inconclusive', contractOutcome: result.outcome, kind: 'pinned-inconclusive-by-design' } }), experiments };
    return { schemaVersion: 1, scope: 'Direct main-screen requested shape and actual Avalonia Cursor kind; no native pointer-image or stock-score claim.', suiteId: osc22SuiteId, runId, contractsVersion: osc22ContractsVersion, adapter, cases: [item], snapshots: frames };
}

/** The receipt counts: the preserved pinned verdict over the effect verdict. */
export function osc22Counts(report) {
    const counts = {};
    for (const item of report.cases) {
        const key = `${item.conclusion.outcome}/${item.effectContract.outcome}`;
        counts[key] = (counts[key] ?? 0) + 1;
    }
    return counts;
}
