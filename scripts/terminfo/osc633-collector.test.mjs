import assert from 'node:assert/strict';
import test from 'node:test';
import { collectOsc633 } from './osc633-collector.mjs';
import { syntheticOsc633Evidence } from './fixtures/osc633-evidence.mjs';
import { makeOsc633Experiments, osc633Prefix, osc633Pinned } from './osc633-contracts.mjs';
import { osc133Fixture } from './osc133-contracts.mjs';
import { osc633CaseIds } from './osc633-grading-collector.mjs';
import { osc633AdapterId } from './osc633-grading-collector.mjs';
import { buildOsc633GradingReport } from './osc633-grading.mjs';

const runId = 'a'.repeat(32), barrier = '\x1b[?1;2c';
/** The shape of the derived module: the pinned code as a stand-in that performs exactly the pinned calls (the real bundle is replayed in the layer test). */
const fakeGradingModule = {
    ntildeOsc633Adapter: Object.freeze({ id: osc633AdapterId, probeIds: Object.freeze([...osc633CaseIds]) }),
    ntildeRunOsc633Termless(probeId, ctx) {
        const marker = osc633Pinned[probeId].termlessFeeds[0];
        ctx.feed(marker);
        const consumed = ctx.getCell(0, 0).char === 'X';
        const note = consumed ? 'Marker was consumed; shell prompt integration was not observed' : 'Marker consumption was not confirmed; shell prompt integration was not observed';
        return { probeId, pass: false, note, observation: { outcome: 'inconclusive', evidence: 'consumed', reason: 'insufficient-evidence', note } };
    },
    async ntildeRunOsc633Terminal(probeId, ctx) {
        const [home, marker] = osc633Pinned[probeId].terminalWrites;
        ctx.write(home); ctx.write(marker);
        const pos = await ctx.queryCursorPosition();
        const note = pos.col === 2 ? 'Marker was consumed; shell prompt integration was not observed' : 'Marker consumption was not confirmed; shell prompt integration was not observed';
        return { probeId, pass: false, note, observation: { outcome: 'inconclusive', evidence: 'consumed', reason: 'insufficient-evidence', note } };
    },
};

/** A fence and owner pair that replay the synthetic App: every write is ordered, every read serves the synthetic frame. */
function harness({ failAt = null, startError = null } = {}) {
    const { frames } = syntheticOsc633Evidence(), writes = [], requests = [];
    let next = 0;
    const fence = { write: async bytes => { writes.push(bytes); return barrier; } };
    const owner = async (kind, at = null) => {
        requests.push({ kind, barrier: at });
        if (kind === `${osc633Prefix}-start` && startError) throw startError;
        if (failAt === requests.length) throw new Error('lost observation');
        const frame = frames[next++];
        assert.equal(frame.kind, kind, 'the journal advances in lockstep with the driver');
        return { screen: frame.before.screen, paneId: frame.paneId };
    };
    return { fence, owner, writes, requests, frames };
}

test('the driver executes every experiment\'s exact plan serially through the fence, in order, and journals one read per plan read', async () => {
    const h = harness();
    const result = await collectOsc633({ config: { runId }, fence: h.fence, owner: h.owner, gradingModule: fakeGradingModule });
    assert.equal(result.status, 'complete');
    const experiments = makeOsc633Experiments(runId), fixture = osc133Fixture(runId);
    const expectedWrites = [fixture.bind, ...experiments.flatMap(item => item.plan.filter(step => step.feed !== undefined).map(step => step.feed)), fixture.finish];
    assert.deepEqual(h.writes, expectedWrites);
    assert.equal(h.requests.filter(request => request.kind === `${osc633Prefix}-read`).length, 60);
    assert.deepEqual(h.requests.map(request => request.kind).filter(kind => kind !== `${osc633Prefix}-read`), [`${osc633Prefix}-start`, `${osc633Prefix}-bind`, `${osc633Prefix}-finish`]);
    assert.equal(result.run.experiments.length, 30);
    assert.deepEqual(result.run.probeIds, [...osc633CaseIds]);
    assert.deepEqual(result.run.pinned.map(item => item.caseId), [...osc633CaseIds]);
});

test('the run record re-derives into a report with every pinned verdict preserved', async () => {
    const h = harness();
    const { run } = await collectOsc633({ config: { runId }, fence: h.fence, owner: h.owner, gradingModule: fakeGradingModule });
    const adapter = { id: osc633AdapterId, derivedFromAdapterId: 'ntilde-owned-osc133-grading-v1', bundleSha256: 'f'.repeat(64), upstreamBundleSha256: 'a'.repeat(64) };
    const report = buildOsc633GradingReport(h.frames, run, runId, adapter);
    assert.deepEqual(report.cases.map(item => [item.conclusion.outcome, item.effectContract.outcome]), osc633CaseIds.map(() => ['inconclusive', 'unsupported']));
});

test('a typed refusal of the first observation is the only optional outcome; any other failure is fatal and still attempts restoration', async () => {
    for (const code of ['paneMetadataUnavailable', 'screenDetailsUnavailable', 'metadataGeometryUnavailable']) {
        const h = harness({ startError: Object.assign(new Error('refused'), { code }) });
        assert.deepEqual(await collectOsc633({ config: { runId }, fence: h.fence, owner: h.owner, gradingModule: fakeGradingModule }), { status: 'unavailable', reason: code });
    }
    const untyped = harness({ startError: new Error('boom') });
    await assert.rejects(collectOsc633({ config: { runId }, fence: untyped.fence, owner: untyped.owner, gradingModule: fakeGradingModule }), /boom/);
    const lost = harness({ failAt: 9 });
    await assert.rejects(collectOsc633({ config: { runId }, fence: lost.fence, owner: lost.owner, gradingModule: fakeGradingModule }));
    assert.equal(lost.writes.at(-1), osc133Fixture(runId).finish, 'restoration is attempted after a failed observation');
});

test('an adapter that does not name the contracted probes, or a pinned probe that feeds anything else, is refused', async () => {
    const h = harness();
    await assert.rejects(collectOsc633({ config: { runId }, fence: h.fence, owner: h.owner, gradingModule: { ...fakeGradingModule, ntildeOsc633Adapter: { id: osc633AdapterId, probeIds: ['extensions.osc633-a'] } } }), /does not name the contracted probes/);
    const other = harness();
    const wrong = { ...fakeGradingModule, ntildeRunOsc633Termless(probeId, ctx) { ctx.feed('\x1b]133;A\x07Y'); return { probeId }; } };
    await assert.rejects(collectOsc633({ config: { runId }, fence: other.fence, owner: other.owner, gradingModule: wrong }), /feed differs/);
});
