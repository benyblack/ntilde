import assert from 'node:assert/strict';
import { test } from 'node:test';
import { collectDevqGrading, devqBind } from './devq-collector.mjs';
import { devqContracts, devqCaseIds, devqNoncePrefix, devqPlanSeed, devqFinish, replayDevqPinned } from './devq-contracts.mjs';
import { syntheticDevqEvidence } from './fixtures/devq-evidence.mjs';
import { adaptDevqCollector, devqAdapterId } from './devq-grading-collector.mjs';
import { buildDevqGradingReport } from './devq-grading.mjs';

const da = '\x1b[?62;4;22c';
const runId = 'a'.repeat(32);

/** The groups in plan order: the Nth captured exchange of a run is the Nth group (several groups share the same query bytes, so the bytes alone are ambiguous). */
const planned = devqContracts.flatMap(contract => contract.groups);

/** A fake fence that answers every exchange with the oracle's replies for its request (or the DA barrier alone). */
function oracleFence(drop = null) {
    return {
        writes: [], exchanges: [],
        async write(bytes) { this.writes.push(bytes); return da; },
        async exchange(bytes, parse) {
            const group = planned[this.exchanges.length];
            if (group?.request !== bytes) throw new Error('unexpected exchange bytes');
            this.exchanges.push(bytes);
            const replies = [...group.demand];
            return parse((drop === null ? replies : drop(replies)).join('') + da);
        },
    };
}

/** A fake owner that journals one synthetic seeded screen per read. */
const ownerOver = screen => async (kind, barrier, extras = {}) => ({ screen: structuredClone(screen), ...extras });

/** A grading module stand-in: the adapter identity plus pinned runners over the recording context (the real module is exercised by the layered-derivation test). */
const gradingModule = {
    ntildeDevqAdapter: { id: devqAdapterId, probeIds: [...devqCaseIds] },
    ntildeRunDevqTermless: (probeId, context) => {
        const contract = devqContracts.find(item => item.caseId === probeId);
        if (contract.pinnedSetup) context.feed(contract.pinnedFeeds[0]);
        return { probeId, ...replayDevqPinned(probeId, context.feedCapture(contract.pinnedFeeds.at(-1))) };
    },
    ntildeRunDevqTerminal: async (probeId, context) => {
        const contract = devqContracts.find(item => item.caseId === probeId);
        if (contract.pinnedSetup) context.write(contract.pinnedFeeds[0]);
        const raw = await context.queryWithSentinelOutcome(contract.pinnedFeeds.at(-1), /x^/);
        return { probeId, ...replayDevqPinned(probeId, raw.raw) };
    },
};

/** The journal mirrors the worker's reads: the synthetic frames with each read's report replaced by the collected one. */
function journalFor(run) {
    const evidence = syntheticDevqEvidence({ runId });
    let index = 2;
    const frames = evidence.frames.map(frame => frame);
    for (const item of run.cases) {
        for (const read of item.reads) {
            frames[index] = { ...frames[index], ...(read.report ? { report: read.report } : {}) };
            index++;
        }
    }
    return { frames, adapter: evidence.adapter };
}

test('the collector replays every plan through the fence and journals every reply list', async () => {
    const fence = oracleFence();
    const seeded = syntheticDevqEvidence({ runId }).frames[2].screen;
    const run = await collectDevqGrading({ config: { runId, output: '/tmp/none' }, fence, owner: ownerOver(seeded), gradingModule });
    assert.equal(run.adapterId, devqAdapterId);
    assert.deepEqual(run.probeIds, [...devqCaseIds]);
    assert.deepEqual(run.cases.map(item => item.caseId), [...devqCaseIds]);
    for (const [position, item] of run.cases.entries()) {
        const contract = devqContracts[position];
        assert.equal(item.feeds.length, contract.plan.filter(step => step.feed).length);
        assert.deepEqual(item.feeds, contract.plan.filter(step => step.feed).map(step => step.feed));
        assert.equal(item.reads.length, contract.roles.length);
        for (const [index, read] of item.reads.entries()) {
            const role = contract.roles[index];
            if (role === 'initial') { assert.equal(read.report, undefined); continue; }
            assert.deepEqual(read.report.replies, [...contract.groups.find(group => group.role === role).demand], `${contract.caseId}/${role}`);
            assert.equal(read.report.barrier, da);
        }
    }
    // The captured exchanges are exactly the groups' requests, in plan order; every other feed (reseed, setup) is a plain fenced write.
    const expectedRequests = devqContracts.flatMap(contract => contract.groups.map(group => group.request));
    assert.deepEqual(fence.exchanges, expectedRequests);
    assert.equal(fence.writes[0], devqBind(runId));
    assert.equal(fence.writes.at(-1), devqFinish(runId));
    assert.equal(devqBind(runId), '\x1b[?1049h' + devqPlanSeed(runId));
    // Each pinned replay ran over its own journaled exchange.
    for (const item of run.cases) {
        assert.equal(item.pinned.termless.probeId, item.caseId);
        assert.equal(item.pinned.termless.observation.evidence, 'query');
    }
    const outcomes = Object.fromEntries(run.cases.map(item => [item.caseId, item.pinned.termless.observation.outcome]));
    assert.deepEqual(outcomes, { 'device.xtgetxres': 'supported', 'device.xtreportcolors': 'inconclusive', 'device.xtwinops-20': 'supported', 'device.xtwinops-21': 'supported' });
    // The complete run rederives through the parent report builder when the journal mirrors the reads.
    const { frames, adapter } = journalFor(run);
    const report = buildDevqGradingReport(frames, run, runId, adapter);
    assert.deepEqual(report.cases.map(item => item.effectContract.outcome), ['supported', 'supported', 'supported', 'supported']);
    assert.equal(report.cases[1].divergence.kind, 'pinned-inconclusive-effect-supported');
});

test('a dropped reply is journaled honestly and the report builder grades it unsupported, not silently', async () => {
    const fence = oracleFence(replies => replies.slice(0, Math.max(0, replies.length - 1)));
    const seeded = syntheticDevqEvidence({ runId }).frames[2].screen;
    const run = await collectDevqGrading({ config: { runId, output: '/tmp/none' }, fence, owner: ownerOver(seeded), gradingModule });
    const xres = run.cases[0];
    assert.equal(xres.reads[devqContracts[0].pinnedRead].report.replies.length, 0, 'the pinned reply was dropped');
    const { frames, adapter } = journalFor(run);
    const report = buildDevqGradingReport(frames, run, runId, adapter);
    assert.equal(report.cases[0].effectContract.outcome, 'unsupported');
    assert.match(report.cases[0].effectContract.note, /was not answered/);
    assert.equal(report.cases[0].conclusion.outcome, 'inconclusive', 'the pinned verdict follows the replayed pinned code over the dropped reply');
    assert.deepEqual(report.cases[0].divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'unsupported', kind: 'pinned-inconclusive-effect-unsupported' });
});

test('a failed exchange aborts the collection and still restores the screen, reporting both failures', async () => {
    const fence = oracleFence();
    fence.exchange = async () => { throw new Error('exchange failed'); };
    const seeded = syntheticDevqEvidence({ runId }).frames[2].screen;
    await assert.rejects(collectDevqGrading({ config: { runId, output: '/tmp/none' }, fence, owner: ownerOver(seeded), gradingModule }), /exchange failed/);
    assert.equal(fence.writes.at(-1), devqFinish(runId), 'the finish still ran');
    const failing = oracleFence();
    failing.exchange = async () => { throw new Error('exchange failed'); };
    let calls = 0;
    const owner = async kind => { if (kind.endsWith('-finish')) throw new Error('restore failed'); calls++; return { screen: structuredClone(seeded) }; };
    await assert.rejects(collectDevqGrading({ config: { runId, output: '/tmp/none' }, fence: failing, owner, gradingModule }), error => error instanceof AggregateError
        && /dvq collection and restoration failed/.test(error.message) && error.errors.length === 2);
    assert.ok(calls >= 2);
});

test('the nonce and prefix bind the suite', () => {
    assert.equal(devqNoncePrefix, 'NTILDE_DQ_');
    assert.equal(adaptDevqCollector.length, 1);
});
