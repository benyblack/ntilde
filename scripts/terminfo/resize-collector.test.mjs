import test from 'node:test';
import assert from 'node:assert/strict';
import { collectResize, makeResizeCases } from './resize-cases.mjs';
import { initialResizeScreen } from './fixtures/resize-journal.mjs';
const config = { runId: 'a'.repeat(32) }, barrier = '\x1b[?1;2c';
test('worker requests only known stages, six parent-selected actions and fenced smaller/restored observations', async () => {
    const calls = [], writes = [];
    await collectResize({ config, fence: { write: async bytes => { writes.push(bytes); return barrier; } },
        owner: async (kind, fence, extra) => { calls.push({ kind, fence, extra }); return { screen: initialResizeScreen() }; } });
    assert.equal(calls.length, 18); assert.equal(calls[0].kind, 'resize-start'); assert.equal(calls.at(-1).kind, 'resize-finish');
    const actions = calls.filter(value => value.kind === 'resize-apply'); assert.equal(actions.length, 6);
    assert.ok(actions.every(value => value.fence === null && Object.keys(value.extra).sort().join() === 'caseId,stageId'));
    const plan = makeResizeCases(12, 60).flatMap(item => ['control', 'after', 'restored'].map(stageId => ({ caseId: item.id, stageId })));
    assert.deepEqual(calls.filter(value => value.kind === 'resize-read').map(value => value.extra), plan);
    assert.ok(calls.filter(value => value.kind === 'resize-read').every(value => value.fence === barrier));
    assert.equal(writes.filter(value => value === '').length, 6);
    assert.ok(writes.at(-1).includes('\x1b[3J'));
});
test('missing metadata or an unbounded initial grid refuses before writing fixture output', async () => {
    for (const screen of [{ ...initialResizeScreen(), bufferState: undefined }, { ...initialResizeScreen(), rows: 53 }]) {
        const writes = [];
        await assert.rejects(collectResize({ config, fence: { write: async bytes => writes.push(bytes) }, owner: async () => ({ screen }) }));
        assert.equal(writes.length, 0);
    }
});
for (const fatal of [false, true]) test(`an action failure aborts and resets without completion (poisoned fence=${fatal})`, async () => {
    const calls = [], writes = [], error = new Error('action failed');
    const fence = { failed: false, write: async bytes => { writes.push(bytes); return barrier; },
        writeCleanup: async bytes => { calls.push('cleanup'); writes.push(bytes); } };
    await assert.rejects(collectResize({ config, fence, owner: async kind => {
        calls.push(kind); if (kind === 'resize-apply') { fence.failed = fatal; throw error; }
        return { screen: initialResizeScreen() };
    } }), value => value === error);
    assert.ok(calls.includes('resize-abort')); assert.ok(!calls.includes('resize-finish'));
    assert.equal(calls.at(-1), fatal ? 'cleanup' : 'resize-abort');
    assert.ok(writes.at(-1).includes('\x1b[3J'));
});
test('a cleanup error preserves the original failure and still attempts output restoration', async () => {
    const original = new Error('action failed'), cleanup = new Error('abort failed'), writes = [];
    await assert.rejects(collectResize({ config, fence: { write: async bytes => { writes.push(bytes); return barrier; } },
        owner: async kind => {
            if (kind === 'resize-apply') throw original;
            if (kind === 'resize-abort') throw cleanup;
            return { screen: initialResizeScreen() };
        } }), value => value instanceof AggregateError && value.errors[0] === original && value.errors[1] === cleanup);
    assert.ok(writes.at(-1).includes('\x1b[3J'));
});

for (const code of ['screenDetailsUnavailable', 'resizeGeometryUnavailable']) test('only initial typed ' + code + ' can be retained as unavailable', async () => {
    const writes = [];
    const result = await collectResize({ config, fence: { write: async bytes => writes.push(bytes) },
        owner: async () => { throw Object.assign(new Error('initial budget'), { code }); } });
    assert.deepEqual(result, { status: 'unavailable', reason: code }); assert.equal(writes.length, 0);
});
