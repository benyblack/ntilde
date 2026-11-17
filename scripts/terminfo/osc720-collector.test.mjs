import test from 'node:test';
import assert from 'node:assert/strict';
import { collectOsc720, exposeOsc720Facts, osc720PrerequisiteReason, replayOsc720Pinned } from './osc720-collector.mjs';
import { osc720CaseId, osc720CaseIds, osc720Experiments, osc720Fixture, osc720Pinned } from './osc720-contracts.mjs';
import { planFeeds } from './graded-plan.mjs';

const facts = (offset, scrollback = 30) => ({ rows: 24, viewport: { scrollOffset: offset, maxScrollOffset: scrollback }, scrollbackLines: scrollback, cursor: [23, 0] });
const stubModule = overrides => ({ ntildeOsc720Adapter: { id: 'stub', probeIds: osc720CaseIds }, ntildeOsc720HasTermless: () => true,
    ntildeRunOsc720Termless: (id, ctx) => { for (const feed of osc720Pinned.termless.feeds) ctx.feed(feed); ctx.getScrollback(); ctx.getScrollback(); return { probeId: id, pass: false, response: '{}', observation: { outcome: 'unsupported' } }; },
    ntildeRunOsc720Terminal: async (id, ctx) => { for (const write of osc720Pinned.terminal.writes) ctx.write(write); await ctx.queryCursorPosition(); return { probeId: id, pass: false, note: 't', observation: { outcome: 'inconclusive' } }; }, ...overrides });

test('the pinned replay runs the unchanged code over parent-built contexts and refuses a different call pattern', async () => {
    const replay = await replayOsc720Pinned(stubModule(), [facts(0), facts(0)]);
    assert.equal(replay.caseId, osc720CaseId);
    assert.equal(replay.termless.observation.outcome, 'unsupported');
    await assert.rejects(replayOsc720Pinned(stubModule({ ntildeRunOsc720Termless: (id, ctx) => { ctx.feed('scroll-0\r\n'); ctx.getScrollback(); ctx.getScrollback(); return { probeId: id }; } }), [facts(0), facts(0)]), /different calls/);
    await assert.rejects(replayOsc720Pinned(stubModule({ ntildeRunOsc720Terminal: async (id, ctx) => { ctx.write('x'); return { probeId: id }; } }), [facts(0), facts(0)]), /different calls|Unexpected/);
    await assert.rejects(replayOsc720Pinned(stubModule({ ntildeRunOsc720Termless: (id, ctx) => ctx.read() }), [facts(0), facts(0)]), /Unexpected pinned context access/);
    await assert.rejects(replayOsc720Pinned(stubModule({ ntildeOsc720HasTermless: () => false }), [facts(0), facts(0)]), /presence changed/);
});

test('only typed initial unavailability can skip collection', async () => {
    const args = { config: { runId: 'a'.repeat(32) }, fence: { write() { throw new Error('unexpected write'); } }, gradingModule: stubModule() };
    for (const code of ['screenDetailsUnavailable', 'paneMetadataUnavailable', 'metadataGeometryUnavailable']) {
        const owner = async () => { throw Object.assign(new Error('unavailable'), { code }); };
        assert.deepEqual(await collectOsc720({ ...args, owner }), { status: 'unavailable', reason: code });
        await assert.rejects(collectOsc720({ ...args, owner: async () => { throw Object.assign(new Error('failure after partial responses'), { code }); } }), /after partial/);
    }
    await assert.rejects(collectOsc720({ ...args, owner: async () => { throw new Error('lost identity'); } }), /lost identity/);
    await assert.rejects(collectOsc720({ ...args, owner: async () => ({}), gradingModule: stubModule({ ntildeOsc720Adapter: { id: 'x', probeIds: [] } }) }), /Changed OSC 720 adapter/);
});

test('a complete collection journals every experiment, performs the wheel actions through the owner and replays the pinned paths', async () => {
    const runId = 'a'.repeat(32), kinds = [], reads = new Map();
    const owner = async (kind, barrier) => {
        kinds.push(kind);
        if (kind === 'ovu-scroll-up') return { action: 'scroll-up', notches: [] };
        const read = (reads.get(kind) ?? 0); reads.set(kind, read + 1);
        return { screen: { cursorRow: 23, cursorCol: 0 }, facts: facts(read % 2) };
    };
    const fence = { write: async bytes => `\x1b[?1;2c` };
    const result = await collectOsc720({ config: { runId }, fence, owner, gradingModule: stubModule() });
    assert.equal(result.status, 'complete');
    const contracts = osc720Experiments(runId);
    assert.deepEqual(result.run.experiments.map(item => item.caseId), contracts.map(item => item.caseId));
    assert.deepEqual(result.run.experiments.map(item => item.feeds), contracts.map(planFeeds));
    assert.equal(kinds.filter(kind => kind === 'ovu-scroll-up').length, contracts.filter(item => item.plan.some(step => step.action)).length);
    assert.equal(kinds[0], 'ovu-start'); assert.equal(kinds.at(-1), 'ovu-finish');
    assert.equal(result.run.pinned.caseId, osc720CaseId);
    assert.equal(osc720Fixture(runId).finish.includes('\x1b[3J'), true);
});

test('a restored prerequisite proceeds and a typed unavailable prerequisite is propagated', () => {
    assert.equal(osc720PrerequisiteReason(true, false, null), null);
    assert.equal(osc720PrerequisiteReason(false, true, 'paneMetadataUnavailable'), 'paneMetadataUnavailable');
    assert.throws(() => osc720PrerequisiteReason(false, false, null), /must restore/);
    assert.throws(() => osc720PrerequisiteReason(false, true, 'unexpected'), /refusal/);
});

test('observation facts expose the view position, stored history, cursor and screen mode', () => {
    const snapshot = { screen: { rows: 24 }, metadata: { viewport: { scrollOffset: 3, maxScrollOffset: 30 }, buffer: { scrollbackLines: 30, historyGeneration: 7, rowsEvicted: 0, cursorRow: 5, cursorCol: 6, alternateScreen: false } } };
    assert.deepEqual(exposeOsc720Facts(snapshot), { rows: 24, viewport: { scrollOffset: 3, maxScrollOffset: 30 }, scrollbackLines: 30, historyGeneration: 7, rowsEvicted: 0, cursor: [5, 6], alternateScreen: false });
});
