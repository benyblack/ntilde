// Parent-side rederivation of the OSC 3008 qualification. No result is accepted without a complete owned metadata journal (paired complete screen, pane metadata and session status),
// exact feeds, a clean bound baseline and restoration. The pinned verdict is inconclusive by design and is preserved as the conclusion; the separately identified effect contract carries
// the graded verdict, derived here from the independent oracle in osc3008-contracts.mjs, never from the production implementation.
import { pristineEditing } from './editing-capture.mjs';
import { validateFrame, validateMetadataSnapshot } from './metadata.mjs';
import { planFeeds, planReads, requireReplayAgreement, validateExactFeeds } from './graded-plan.mjs';
import { osc133Fixture, osc133Minimum, simulateOsc133 } from './osc133-contracts.mjs';
import { evaluateRead, matchesOsc133Baseline } from './osc133-grading.mjs';
import { osc3008AdapterId, osc3008DerivedFromAdapterId } from './osc3008-grading-collector.mjs';
import { makeOsc3008Experiments, osc3008CaseId, osc3008CaseIds, osc3008ContractsVersion, osc3008EffectContractId, osc3008Minimum, osc3008PinnedWrites, osc3008Prefix,
    osc3008SuiteId, replayOsc3008PinnedStatic, simulateContexts, withoutContextSequences } from './osc3008-contracts.mjs';

const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
/** A value with every object's keys in sorted order: the oracle builds a record in wire order, the copier in field-table order, and neither order is a fact. */
const canonical = value => {
    if (Array.isArray(value)) return value.map(canonical);
    if (value === null || typeof value !== 'object') return value;
    return Object.fromEntries(Object.keys(value).sort().map(key => [key, canonical(value[key])]));
};
const sameContexts = (a, b) => same(canonical(a), canonical(b));
const check = (condition, message) => { if (!condition) throw new Error(message); };
const digest = value => /^[a-f\d]{64}$/.test(value ?? '');

/** The baseline the OSC 133 fixture establishes plus a readable, empty context stack; at the end the stack must be empty again (ended records cannot be cleared by any sequence). */
export function matchesOsc3008Baseline(snapshot, first, runId, finish) {
    const contexts = snapshot.metadata.buffer.contexts;
    const clean = contexts?.untrusted === true && contexts.stack.length === 0 && (finish || contexts.ended.length === 0);
    return clean && matchesOsc133Baseline(snapshot, first, runId, finish);
}

/** The plan the observer accepts: one entry per read. */
export function osc3008ObserverPlan(snapshot, runId) {
    check(snapshot.screen.rows >= Math.max(osc133Minimum.rows, osc3008Minimum.rows) && snapshot.screen.cols >= Math.max(osc133Minimum.cols, osc3008Minimum.cols), 'OSC 3008 fixture needs a larger grid.');
    return makeOsc3008Experiments(runId).flatMap(item => item.plan.filter(step => step.read).map(() => ({})));
}

function validateJournal(frames, contracts, runId) {
    const count = 3 + contracts.reduce((sum, item) => sum + planReads(item), 0);
    check(/^[a-f\d]{32}$/.test(runId ?? '') && Array.isArray(frames) && frames.length === count && count <= 64, 'Incomplete OSC 3008 journal.');
    const first = validateMetadataSnapshot(frames[0]?.before, undefined, true);
    check(pristineEditing(first.screen) && first.screen.rows >= osc133Minimum.rows && first.screen.cols >= osc133Minimum.cols, 'OSC 3008 fixture needs a pristine private main screen.');
    for (const [index, frame] of frames.entries()) validateFrame(frame, index, count - 1, first, { caseId: null, stageId: null }, osc3008Prefix, true);
    check(matchesOsc3008Baseline(frames[1].before, first, runId, false) && matchesOsc3008Baseline(frames.at(-1).before, first, runId, true), 'OSC 3008 binding or restoration failed.');
    for (const frame of frames) check(frame.before.screen.colorBinding !== undefined, 'OSC 3008 needs color bindings.');
}

/** The contexts one read reports against the oracle's prediction. `ended` compares the whole ring; `lastEnded` only its newest record (the field validity an end sequence carries). */
function compareContexts(observed, expected) {
    if (observed === undefined) return { observable: false, stackMatches: false, endedMatches: false, lastEndedMatches: false, expected, observed: null };
    return { observable: observed.untrusted === true, stackMatches: sameContexts(observed.stack, expected.stack), endedMatches: sameContexts(observed.ended, expected.ended),
        lastEndedMatches: sameContexts(observed.ended.at(-1) ?? null, expected.ended.at(-1) ?? null), expected, observed: { stack: observed.stack, ended: observed.ended } };
}

function evaluateExperiments(frames, run, contracts, runId) {
    const fixture = osc133Fixture(runId), bound = frames[1], history = [];
    const evaluated = [];
    let frameIndex = 2;
    for (const [position, contract] of contracts.entries()) {
        const record = run.experiments[position];
        check(record?.caseId === contract.caseId, 'OSC 3008 experiment order differs.');
        validateExactFeeds(contract, record.feeds, 'contract');
        check(Array.isArray(record.barriers) && record.barriers.length === planFeeds(contract).length && record.barriers.every(value => /^\x1b\[\?[\d;]+c$/.test(value ?? ''))
            && Array.isArray(record.reads) && record.reads.length === planReads(contract), 'Invalid OSC 3008 capture record.');
        const feeds = [], reads = [];
        for (const step of contract.plan) {
            if (step.feed !== undefined) { feeds.push(step.feed); history.push(step.feed); continue; }
            const frame = frames[frameIndex++], recorded = record.reads[reads.length];
            check(recorded.kind === step.read && frame.barrier === record.barriers[feeds.length - 1]
                && same(recorded.cursor, [frame.before.screen.cursorRow, frame.before.screen.cursorCol]), 'OSC 3008 capture differs from its journal.');
            const modeled = feeds.map(feed => (feed === fixture.stage ? feed : withoutContextSequences(feed)));
            const stored = evaluateRead(frame, simulateOsc133(modeled, fixture), bound, fixture);
            reads.push({ label: step.read, snapshot: frame.index, stored, contexts: compareContexts(frame.before.metadata.buffer.contexts, simulateContexts(history)) });
        }
        evaluated.push({ id: contract.caseId, name: contract.name, role: contract.role, compareEnded: contract.compareEnded ?? null, reads });
    }
    return evaluated;
}

/** A read's contexts decide when they match the prediction; an `end-fields` experiment also needs its newest ended record to match. */
const decided = (experiment, read) => read.contexts.stackMatches && (experiment.compareEnded === 'last' ? read.contexts.lastEndedMatches : true);

function grade(experiments) {
    const reads = experiments.flatMap(experiment => experiment.reads.map(read => ({ experiment, read })));
    const valid = [
        { id: 'observer-reads-contexts', held: reads.every(({ read }) => read.contexts.observable) },
        { id: 'stored-state-unaffected', held: reads.every(({ read }) => read.stored.passed) },
    ];
    const required = reads.filter(({ experiment }) => experiment.role !== 'policy')
        .map(({ experiment, read }) => ({ id: `${experiment.name}/${read.label}`, role: experiment.role, held: decided(experiment, read) }));
    const policy = reads.filter(({ experiment }) => experiment.role === 'policy')
        .map(({ experiment, read }) => ({ id: `${experiment.name}/${read.label}`, match: decided(experiment, read) }));
    let outcome = 'inconclusive';
    if (valid.every(item => item.held)) outcome = required.every(item => item.held) ? 'supported' : 'unsupported';
    return { outcome, validity: valid, required, policy };
}

/** The pinned terminal path rebuilt from its journaled cursor alone: the verdict depends on no observation. */
function checkPinned(run) {
    const stock = run.experiments.find(item => item.caseId === `${osc3008CaseId}/stock`);
    check(stock?.reads?.length === 1 && same(run.pinned, [replayOsc3008PinnedStatic(stock.reads[0].cursor)]), 'OSC 3008 pinned result differs from its replay.');
}

/** Run the PINNED terminal-path code (from the retained derived bundle) over the journaled cursor; it must reproduce the retained result byte for byte. */
export async function verifyOsc3008PinnedCode(run, replayModule) {
    const [recorded] = run.pinned, writes = [];
    check(replayModule.ntildeOsc3008HasTermless(osc3008CaseId) === false, 'Pinned OSC 3008 termless presence changed.');
    const served = { write: text => { writes.push(text); }, queryCursorPosition: () => Promise.resolve({ row: recorded.cursor[0], col: recorded.cursor[1] }) };
    const { probeId, ...terminal } = await replayModule.ntildeRunOsc3008Terminal(osc3008CaseId, served);
    check(probeId === osc3008CaseId && same(writes, [...osc3008PinnedWrites]), 'Pinned OSC 3008 terminal replay changed.');
    requireReplayAgreement({ caseId: osc3008CaseId }, terminal, recorded.terminal);
}

/** Build the whole report from the retained journal and run log. */
export function buildOsc3008GradingReport(frames, run, runId, adapter) {
    const contracts = makeOsc3008Experiments(runId);
    validateJournal(frames, contracts, runId);
    check(adapter?.id === osc3008AdapterId && adapter.derivedFromAdapterId === osc3008DerivedFromAdapterId && digest(adapter.bundleSha256) && digest(adapter.upstreamBundleSha256), 'Invalid OSC 3008 adapter.');
    check(run?.schemaVersion === 1 && run.adapterId === adapter.id && same(run.probeIds, [...osc3008CaseIds])
        && run.experiments?.length === contracts.length && run.pinned?.length === osc3008CaseIds.length, 'Invalid OSC 3008 run.');
    const experiments = evaluateExperiments(frames, run, contracts, runId);
    checkPinned(run);
    const result = grade(experiments);
    const effect = { id: osc3008EffectContractId, version: osc3008ContractsVersion, ...result, attribution: 'separately identified Ntilde effect contract; not the pinned probe' };
    const item = { caseId: osc3008CaseId, pinned: run.pinned[0], conclusion: { outcome: 'inconclusive', grading: 'pinned-terminal-unmeasured-preserved-by-design' },
        contractResult: { outcome: result.outcome }, effectContract: effect,
        ...(result.outcome === 'inconclusive' ? {} : { divergence: { pinnedOutcome: 'inconclusive', contractOutcome: result.outcome, kind: 'pinned-inconclusive-by-design' } }), experiments };
    return { schemaVersion: 1, suiteId: osc3008SuiteId, runId, contractsVersion: osc3008ContractsVersion, adapter, cases: [item], snapshots: frames };
}

/** The receipt counts: the preserved pinned verdict over the effect verdict. */
export function osc3008Counts(report) {
    const counts = {};
    for (const item of report.cases) {
        const key = `${item.conclusion.outcome}/${item.effectContract.outcome}`;
        counts[key] = (counts[key] ?? 0) + 1;
    }
    return counts;
}
