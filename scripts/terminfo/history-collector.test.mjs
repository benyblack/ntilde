import assert from 'node:assert/strict';
import { test } from 'node:test';
import { collectHistory, makeHistoryCases } from './history-cases.mjs';
import { initialEditingScreen } from './fixtures/editing-journal.mjs';
const runId = 'a'.repeat(32), barrier = '\x1b[?1;2c';
test('history collector executes only literal ordered cases and restores through a final fenced read', async () => {
    const writes = [], calls = [];
    await collectHistory({ config: { runId }, fence: { write: async value => { writes.push(value); return barrier; } },
        owner: async (kind, ordering, extra) => { calls.push({ kind, ordering, extra }); return { screen: initialEditingScreen() }; } });
    const cases = makeHistoryCases(10, 48); assert.deepEqual(writes.slice(1, -1), cases.flatMap(value => [value.setup, value.sequence]));
    assert.equal(calls.length, 63); assert.equal(calls.at(-1).kind, 'history-finish'); assert.ok(writes.at(-1).includes('\x1b[3J'));
    assert.deepEqual(calls.slice(2, -1).map(value => value.extra), cases.flatMap(value => ['control', 'after'].map(stageId => ({ caseId: value.id, stageId }))));
});
test('initial optional refusal writes nothing; ordinary errors stay fatal', async () => {
    let writes = 0; const error = Object.assign(new Error('budget'), { code: 'screenDetailsUnavailable' });
    const options = { config: { runId }, fence: { write: async () => { writes++; return barrier; } }, owner: async () => { throw error; } };
    assert.deepEqual(await collectHistory(options), { status: 'unavailable', reason: error.code }); assert.equal(writes, 0);
    options.owner = async () => { throw new Error('bad endpoint'); }; await assert.rejects(collectHistory(options), /bad endpoint/); assert.equal(writes, 0);
});
for (const fatal of [false, true]) test('history failure restores using ' + (fatal ? 'bounded teardown without completion' : 'healthy ordering fence'), async () => {
    const writes = [], calls = [], error = new Error('bad sample'); let failed = false;
    const fence = { get failed() { return failed; }, write: async value => { writes.push(value); return barrier; },
        writeCleanup: async value => { writes.push(value); calls.push('cleanup'); } };
    await assert.rejects(collectHistory({ config: { runId }, fence, owner: async kind => {
        calls.push(kind); if (kind === 'history-read') { failed = fatal; throw error; } return { screen: initialEditingScreen() };
    } }), value => value === error);
    assert.ok(writes.at(-1).includes('\x1b[?1049l')); assert.ok(writes.at(-1).includes('\x1b[3J'));
    assert.equal(calls.at(-1), fatal ? 'cleanup' : 'history-finish');
});
