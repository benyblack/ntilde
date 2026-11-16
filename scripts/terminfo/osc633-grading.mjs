// Parent-side rederivation of OSC 633 observations. No result is accepted
// without a complete owned metadata journal, exact feeds and restoration.
import { pristineEditing } from './editing-capture.mjs';
import { validateFrame, validateMetadataSnapshot } from './metadata.mjs';
import { planFeeds, planReads, requireReplayAgreement, validateExactFeeds } from './graded-plan.mjs';
import { osc133Fixture, osc133Minimum, simulateOsc133 } from './osc133-contracts.mjs';
import { evaluateRead, matchesOsc133Baseline } from './osc133-grading.mjs';
import { osc633AdapterId, osc633CaseIds } from './osc633-grading-collector.mjs';
import { makeOsc633Experiments, osc633ContractsVersion, osc633EffectContractId, osc633Pinned, osc633Prefix, osc633SuiteId,
    osc633TermlessContext, replayOsc633Pinned } from './osc633-contracts.mjs';

const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const check = (condition, message) => { if (!condition) throw new Error(message); };
const targets = [
    (prior, post) => prior.status === 'running' && post.status === 'prompt' && post.command === null,
    (prior, post) => !prior.accepting && post.accepting && Array.isArray(post.commandStart),
    (prior, post) => prior.status === 'prompt' && post.status === 'running' && !post.accepting,
    (prior, post) => prior.status === 'running' && post.status === 'prompt' && !post.accepting && post.commandStart === null && post.outputStart === null,
];

// For expected screen and control state, the target marker has no visible text.
// For the lifecycle effect use the independently specified A–D transitions;
// OSC 133 supplies the same abstract transitions, never the observed result.
const withoutTarget = feed => feed.replace(/\x1b\]633;[^\x07\x1b]*(?:\x07|\x1b\\)/g, '');
const abstractTarget = feed => feed.replaceAll('\x1b]633;', '\x1b]133;');
const stateFor = (feeds, fixture, transform) => simulateOsc133(feeds.map(transform), fixture);

export function osc633ObserverPlan(snapshot, runId) {
    check(snapshot.screen.rows >= osc133Minimum.rows && snapshot.screen.cols >= osc133Minimum.cols, 'OSC 633 fixture needs a larger grid.');
    return makeOsc633Experiments(runId).flatMap(item => item.plan.filter(step => step.read).map(() => ({})));
}

function validateJournal(frames, contracts, runId) {
    const count = 3 + contracts.reduce((sum, item) => sum + planReads(item), 0);
    check(/^[a-f\d]{32}$/.test(runId ?? '') && Array.isArray(frames) && frames.length === count && count <= 64, 'Incomplete OSC 633 journal.');
    const first = validateMetadataSnapshot(frames[0]?.before, undefined, true);
    check(pristineEditing(first.screen) && first.screen.rows >= osc133Minimum.rows && first.screen.cols >= osc133Minimum.cols, 'OSC 633 fixture needs a pristine private main screen.');
    for (const [index, frame] of frames.entries()) validateFrame(frame, index, count - 1, first, { caseId: null, stageId: null }, osc633Prefix, true);
    check(matchesOsc133Baseline(frames[1].before, first, runId, false) && matchesOsc133Baseline(frames.at(-1).before, first, runId, true), 'OSC 633 binding or restoration failed.');
    for (const frame of frames) check(frame.before.screen.colorBinding !== undefined, 'OSC 633 needs color bindings.');
}

function evaluateExperiments(frames, run, contracts, runId) {
    const fixture = osc133Fixture(runId), bound = frames[1], evaluated = new Map(osc633CaseIds.map(id => [id, []]));
    let frameIndex = 2;
    for (const [position, contract] of contracts.entries()) {
        const record = run.experiments[position];
        check(record?.caseId === contract.caseId, 'OSC 633 experiment order differs.');
        validateExactFeeds(contract, record.feeds, 'contract');
        check(Array.isArray(record.barriers) && record.barriers.length === planFeeds(contract).length && record.barriers.every(value => /^\x1b\[\?[\d;]+c$/.test(value ?? ''))
            && Array.isArray(record.reads) && record.reads.length === planReads(contract), 'Invalid OSC 633 capture record.');
        const feeds = [], reads = [];
        for (const step of contract.plan) {
            if (step.feed !== undefined) { feeds.push(step.feed); continue; }
            const frame = frames[frameIndex++], recorded = record.reads[reads.length];
            check(recorded.kind === step.read && frame.barrier === record.barriers[feeds.length - 1]
                && same(recorded.cursor, [frame.before.screen.cursorRow, frame.before.screen.cursorCol]), 'OSC 633 capture differs from its journal.');
            const baseline = evaluateRead(frame, stateFor(feeds, fixture, withoutTarget), bound, fixture);
            const effect = evaluateRead(frame, stateFor(feeds, fixture, abstractTarget), bound, fixture);
            reads.push({ label: step.read, snapshot: frame.index, baseline, effect });
        }
        evaluated.get(contract.owner).push({ id: contract.caseId, name: contract.name, reads });
    }
    return evaluated;
}

function grade(index, experiments) {
    const get = name => experiments.find(item => item.name === name);
    const effect = get('effect').reads, positive = get('positive-control').reads, stock = get('stock').reads[0];
    const target = targets[index], prior = effect[0].baseline.observed, observed = effect[2].effect.observed;
    const valid = [
        { id: 'stock-screen', held: stock.baseline.screenMatches && stock.baseline.preserved },
        { id: 'prior-state', held: effect[0].baseline.passed },
        { id: 'unmarked-control', held: effect[1].baseline.passed && !target(prior, effect[1].baseline.observed) },
        { id: 'positive-control-prior', held: positive[0].baseline.passed },
        { id: 'positive-control', held: positive[1].effect.passed && target(positive[0].baseline.observed, positive[1].effect.observed) },
        ...experiments.filter(item => item.name.startsWith('gate-')).map(item => ({ id: item.name, held: item.reads[0].baseline.passed
            && item.reads[1].baseline.screenMatches && item.reads[1].baseline.preserved && !target(item.reads[0].baseline.observed, item.reads[1].baseline.observed) })),
    ];
    const required = [{ id: 'pinned-effect', held: effect[2].effect.passed && target(prior, observed) }];
    return { outcome: valid.some(item => !item.held) ? 'inconclusive' : required.every(item => item.held) ? 'supported' : 'unsupported',
        validity: valid, required, prior, observed };
}

export function buildOsc633GradingReport(frames, run, runId, adapter, { replayModule = null } = {}) {
    const contracts = makeOsc633Experiments(runId);
    validateJournal(frames, contracts, runId);
    check(adapter?.id === osc633AdapterId && adapter.derivedFromAdapterId === 'ntilde-owned-osc133-grading-v1'
        && /^[a-f\d]{64}$/.test(adapter.bundleSha256 ?? '') && /^[a-f\d]{64}$/.test(adapter.upstreamBundleSha256 ?? ''), 'Invalid OSC 633 adapter.');
    check(run?.schemaVersion === 1 && run.adapterId === adapter.id && same(run.probeIds, [...osc633CaseIds])
        && run.experiments?.length === contracts.length && run.pinned?.length === osc633CaseIds.length, 'Invalid OSC 633 run.');
    const evaluated = evaluateExperiments(frames, run, contracts, runId);
    const cases = osc633CaseIds.map((id, index) => {
        const experiments = evaluated.get(id), screen = frames[experiments.find(item => item.name === 'stock').reads[0].snapshot].before.screen;
        const pinned = run.pinned[index], replay = replayOsc633Pinned(id, screen);
        check(pinned?.caseId === id && same(pinned.termless.feeds, osc633Pinned[id].termlessFeeds) && same(pinned.terminal.writes, osc633Pinned[id].terminalWrites)
            && same(pinned.termless.result, replay.termless) && same(pinned.terminal.result, replay.terminal), 'OSC 633 pinned result differs from the journal.');
        if (replayModule) requireReplayAgreement({ caseId: id }, replayModule.ntildeRunOsc633Termless(id, osc633TermlessContext(id, screen)), pinned.termless.result);
        const result = grade(index, experiments);
        return { caseId: id, pinned, conclusion: { outcome: 'inconclusive', grading: 'pinned-consumption-preserved-by-design' },
            contractResult: { outcome: result.outcome },
            effectContract: { id: osc633EffectContractId, version: osc633ContractsVersion, ...result, attribution: 'separately identified Ntilde effect contract; not the pinned probe' },
            ...(result.outcome === 'inconclusive' ? {} : { divergence: { pinnedOutcome: 'inconclusive', contractOutcome: result.outcome, kind: 'pinned-inconclusive-by-design' } }), experiments };
    });
    return { schemaVersion: 1, suiteId: osc633SuiteId, runId, contractsVersion: osc633ContractsVersion, adapter, cases, snapshots: frames };
}

export function osc633Counts(report) {
    const counts = {};
    for (const item of report.cases) {
        const key = `${item.conclusion.outcome}/${item.effectContract.outcome}`;
        counts[key] = (counts[key] ?? 0) + 1;
    }
    return counts;
}
