import test from 'node:test';
import assert from 'node:assert/strict';
import { collectMetadata, makeMetadataCases, metadataFixture } from './metadata-cases.mjs';
import { initialEditingScreen } from './fixtures/editing-journal.mjs';
const config = { runId: 'a'.repeat(32) }, barrier = '\x1b[?1;2c';
test('worker uses exact 43 source-defined stages and bounded auto-hide observation without any acting RPC', async () => {
    const calls = [], writes = [], waits = [];
    await collectMetadata({ config, fence: { write: async value => { writes.push(value); return barrier; } }, sleep: async value => waits.push(value),
        owner: async (kind, fence, extra) => { calls.push({ kind, fence, extra }); return { screen: initialEditingScreen(12, 60) }; } });
    assert.equal(calls.length, 46); assert.equal(calls[0].kind, 'metadata-start'); assert.equal(calls.at(-1).kind, 'metadata-finish');
    const plan = makeMetadataCases(12, 60, config.runId).flatMap(item => item.stages.map(stage => ({ caseId: item.id, stageId: stage.id })));
    assert.deepEqual(calls.filter(value => value.kind === 'metadata-read').map(value => value.extra), plan);
    assert.deepEqual(waits, [6500]); assert.equal(writes.at(-1), metadataFixture(config.runId).finish);
    assert.ok(calls.slice(1).every(value => value.fence === barrier));
});
for (const code of ['screenDetailsUnavailable', 'paneMetadataUnavailable', 'metadataGeometryUnavailable']) test('only initial typed ' + code + ' permits unavailable result', async () => {
    const writes = []; assert.deepEqual(await collectMetadata({ config, fence: { write: async value => writes.push(value) },
        owner: async () => { throw Object.assign(new Error('unavailable'), { code }); } }), { status: 'unavailable', reason: code });
    assert.equal(writes.length, 0);
});
for (const fatal of [false, true]) test('later refusal remains fatal and attempts cleanup (poisoned=' + fatal + ')', async () => {
    const writes = [], calls = [], error = Object.assign(new Error('lost actual UI read'), { code: 'paneMetadataUnavailable' });
    const fence = { failed: false, write: async value => { writes.push(value); return barrier; }, writeCleanup: async value => writes.push(value) };
    await assert.rejects(collectMetadata({ config, fence, owner: async kind => {
        calls.push(kind); if (kind === 'metadata-read') { fence.failed = fatal; throw error; } return { screen: initialEditingScreen(12, 60) };
    } }), value => value === error);
    assert.equal(writes.at(-1), metadataFixture(config.runId).finish); assert.ok(!calls.includes('metadata-finish'));
});
