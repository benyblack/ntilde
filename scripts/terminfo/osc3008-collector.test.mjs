import assert from 'node:assert/strict';
import test from 'node:test';
import { collectOsc3008, replayOsc3008Pinned } from './osc3008-collector.mjs';
import { syntheticOsc3008Evidence } from './fixtures/osc3008-evidence.mjs';
import { makeOsc3008Experiments, osc3008CaseId, osc3008CaseIds, osc3008PinnedWrites, osc3008Prefix, replayOsc3008PinnedStatic } from './osc3008-contracts.mjs';
import { osc3008AdapterId } from './osc3008-grading-collector.mjs';
import { buildOsc3008GradingReport } from './osc3008-grading.mjs';

const runId = 'a'.repeat(32), barrier = '\x1b[?1;2c';
/** The shape of the derived module: the pinned code as a stand-in that performs exactly the pinned calls (the real bundle is replayed in the layer test). */
const fakeGradingModule = {
    ntildeOsc3008Adapter: Object.freeze({ id: osc3008AdapterId, probeIds: Object.freeze([...osc3008CaseIds]) }),
    ntildeOsc3008HasTermless: () => false,
    async ntildeRunOsc3008Terminal(probeId, ctx) {
        for (const write of osc3008PinnedWrites) ctx.write(write);
        const position = await ctx.queryCursorPosition();
        return { probeId, ...replayOsc3008PinnedStatic([position.row - 1, position.col - 1]).terminal };
    },
};

/** A fence and owner pair that replay the synthetic App: every write is ordered, every read serves the synthetic frame. */
function harness({ failAt = null, startError = null } = {}) {
    const { frames } = syntheticOsc3008Evidence(), writes = [], requests = [];
    let next = 0;
    const fence = { write: async bytes => { writes.push(bytes); return barrier; } };
    const owner = async (kind, at = null) => {
        requests.push({ kind, barrier: at });
        if (kind === `${osc3008Prefix}-start` && startError) throw startError;
        if (failAt === requests.length) throw new Error('lost observation');
        const frame = frames[next++];
        assert.equal(frame.kind, kind, 'the journal advances in lockstep with the driver');
        return { screen: frame.before.screen, paneId: frame.paneId };
    };
    return { fence, owner, writes, requests, frames };
}

test('the driver executes every experiment\'s exact plan serially through the fence and journals one read per plan read', async () => {
    const h = harness();
    const result = await collectOsc3008({ config: { runId }, fence: h.fence, owner: h.owner, gradingModule: fakeGradingModule });
    assert.equal(result.status, 'complete');
    const contracts = makeOsc3008Experiments(runId);
    assert.deepEqual(result.run.experiments.map(item => item.caseId), contracts.map(item => item.caseId));
    for (const [index, contract] of contracts.entries()) assert.deepEqual(result.run.experiments[index].feeds, contract.plan.filter(step => step.feed).map(step => step.feed));
    assert.equal(h.requests.filter(item => item.kind === `${osc3008Prefix}-read`).length, contracts.reduce((sum, item) => sum + item.plan.filter(step => step.read).length, 0));
    assert.equal(h.requests.at(-1).kind, `${osc3008Prefix}-finish`);
    const report = buildOsc3008GradingReport(h.frames, result.run, runId, { ...syntheticOsc3008Evidence().adapter });
    assert.equal(report.cases[0].effectContract.outcome, 'supported');
});

test('the pinned terminal path is replayed over the journaled cursor and refuses a changed probe', async () => {
    const replay = await replayOsc3008Pinned(fakeGradingModule, [0, 0]);
    assert.deepEqual(replay, replayOsc3008PinnedStatic([0, 0]));
    assert.equal(replay.caseId, osc3008CaseId);
    await assert.rejects(replayOsc3008Pinned(fakeGradingModule, [0]), /Invalid OSC 3008 pinned cursor/);
    await assert.rejects(replayOsc3008Pinned({ ...fakeGradingModule, ntildeOsc3008HasTermless: () => true }, [0, 0]), /termless presence/);
    await assert.rejects(replayOsc3008Pinned({ ...fakeGradingModule, ntildeRunOsc3008Terminal: async probeId => ({ probeId }) }, [0, 0]), /replay changed/);
    await assert.rejects(replayOsc3008Pinned({ ...fakeGradingModule, ntildeRunOsc3008Terminal: async (probeId, ctx) => { ctx.write('other'); return { probeId }; } }, [0, 0]), /replay changed/);
    await assert.rejects(replayOsc3008Pinned({ ...fakeGradingModule, ntildeRunOsc3008Terminal: async (probeId, ctx) => ({ probeId, text: ctx.getCell }) }, [0, 0]), /Unexpected pinned context access/);
});

test('only a typed refusal of the very first observation is an optional outcome', async () => {
    const refused = harness({ startError: Object.assign(new Error('unavailable'), { code: 'paneMetadataUnavailable' }) });
    assert.deepEqual(await collectOsc3008({ config: { runId }, fence: refused.fence, owner: refused.owner, gradingModule: fakeGradingModule }), { status: 'unavailable', reason: 'paneMetadataUnavailable' });
    const unexpected = harness({ failAt: 4 });
    await assert.rejects(collectOsc3008({ config: { runId }, fence: unexpected.fence, owner: unexpected.owner, gradingModule: fakeGradingModule }),
        error => [error, ...(error.errors ?? [])].some(item => /lost observation/.test(item.message)));
    await assert.rejects(collectOsc3008({ config: { runId }, fence: harness().fence, owner: harness().owner, gradingModule: { ...fakeGradingModule, ntildeOsc3008Adapter: { id: 'x', probeIds: [] } } }), /does not name the contracted probe/);
});
