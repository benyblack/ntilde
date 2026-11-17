import assert from 'node:assert/strict';
import test from 'node:test';
import { collectOsc22, replayOsc22Pinned } from './osc22-collector.mjs';
import { syntheticOsc22Evidence } from './fixtures/osc22-evidence.mjs';
import { makeOsc22Experiments, osc22CaseId, osc22CaseIds, osc22PinnedWrites, osc22Prefix, replayOsc22PinnedStatic } from './osc22-contracts.mjs';
import { osc22AdapterId } from './osc22-grading-collector.mjs';
import { buildOsc22GradingReport } from './osc22-grading.mjs';

const runId = 'a'.repeat(32), barrier = '\x1b[?1;2c';
/** The shape of the derived module: the pinned code as a stand-in that performs exactly the pinned calls (the real bundle is replayed in the layer test). */
const fakeGradingModule = {
    ntildeOsc22Adapter: Object.freeze({ id: osc22AdapterId, probeIds: Object.freeze([...osc22CaseIds]) }),
    ntildeOsc22HasTermless: () => false,
    async ntildeRunOsc22Terminal(probeId, ctx) {
        for (const write of osc22PinnedWrites) ctx.write(write);
        const position = await ctx.queryCursorPosition();
        return { probeId, ...replayOsc22PinnedStatic([position.row - 1, position.col - 1]).terminal };
    },
};

/** A fence and owner pair that replay the synthetic App: every write is ordered, every read serves the synthetic frame. */
function harness({ failAt = null, startError = null } = {}) {
    const { frames } = syntheticOsc22Evidence(), writes = [], requests = [];
    let next = 0;
    const fence = { write: async bytes => { writes.push(bytes); return barrier; } };
    const owner = async (kind, at = null) => {
        requests.push({ kind, barrier: at });
        if (kind === `${osc22Prefix}-start` && startError) throw startError;
        if (failAt === requests.length) throw new Error('lost observation');
        const frame = frames[next++];
        assert.equal(frame.kind, kind, 'the journal advances in lockstep with the driver');
        return { screen: frame.before.screen, paneId: frame.paneId };
    };
    return { fence, owner, writes, requests, frames };
}

test('the driver executes every experiment\'s exact plan serially through the fence and journals one read per plan read', async () => {
    const h = harness();
    const result = await collectOsc22({ config: { runId }, fence: h.fence, owner: h.owner, gradingModule: fakeGradingModule });
    assert.equal(result.status, 'complete');
    const contracts = makeOsc22Experiments(runId);
    assert.deepEqual(result.run.experiments.map(item => item.caseId), contracts.map(item => item.caseId));
    for (const [index, contract] of contracts.entries()) assert.deepEqual(result.run.experiments[index].feeds, contract.plan.filter(step => step.feed).map(step => step.feed));
    assert.equal(h.requests.filter(item => item.kind === `${osc22Prefix}-read`).length, contracts.reduce((sum, item) => sum + item.plan.filter(step => step.read).length, 0));
    assert.equal(h.requests.at(-1).kind, `${osc22Prefix}-finish`);
    const report = buildOsc22GradingReport(h.frames, result.run, runId, { ...syntheticOsc22Evidence().adapter });
    assert.equal(report.cases[0].effectContract.outcome, 'supported');
});

test('the pinned terminal path is replayed over the journaled cursor and refuses a changed probe', async () => {
    const replay = await replayOsc22Pinned(fakeGradingModule, [0, 0]);
    assert.deepEqual(replay, replayOsc22PinnedStatic([0, 0]));
    assert.equal(replay.caseId, osc22CaseId);
    await assert.rejects(replayOsc22Pinned(fakeGradingModule, [0]), /Invalid OSC 22 pinned cursor/);
    await assert.rejects(replayOsc22Pinned({ ...fakeGradingModule, ntildeOsc22HasTermless: () => true }, [0, 0]), /termless presence/);
    await assert.rejects(replayOsc22Pinned({ ...fakeGradingModule, ntildeRunOsc22Terminal: async probeId => ({ probeId }) }, [0, 0]), /replay changed/);
    await assert.rejects(replayOsc22Pinned({ ...fakeGradingModule, ntildeRunOsc22Terminal: async (probeId, ctx) => { ctx.write('other'); return { probeId }; } }, [0, 0]), /replay changed/);
    await assert.rejects(replayOsc22Pinned({ ...fakeGradingModule, ntildeRunOsc22Terminal: async (probeId, ctx) => ({ probeId, text: ctx.getCell }) }, [0, 0]), /Unexpected pinned context access/);
});

test('only a typed refusal of the very first observation is an optional outcome', async () => {
    const refused = harness({ startError: Object.assign(new Error('unavailable'), { code: 'paneMetadataUnavailable' }) });
    assert.deepEqual(await collectOsc22({ config: { runId }, fence: refused.fence, owner: refused.owner, gradingModule: fakeGradingModule }), { status: 'unavailable', reason: 'paneMetadataUnavailable' });
    const unexpected = harness({ failAt: 4 });
    await assert.rejects(collectOsc22({ config: { runId }, fence: unexpected.fence, owner: unexpected.owner, gradingModule: fakeGradingModule }),
        error => [error, ...(error.errors ?? [])].some(item => /lost observation/.test(item.message)));
    await assert.rejects(collectOsc22({ config: { runId }, fence: harness().fence, owner: harness().owner, gradingModule: { ...fakeGradingModule, ntildeOsc22Adapter: { id: 'x', probeIds: [] } } }), /does not name the contracted probe/);
});
