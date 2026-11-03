import assert from 'node:assert/strict';
import { test } from 'node:test';
import { collectErase, collectEditing, makeEditingCases } from './editing-cases.mjs';
import { initialEditingScreen } from './fixtures/editing-journal.mjs';
const runId = 'a'.repeat(32), barrier = '\x1b[?1;2c';
for (const [prefix, collect] of [['erase', collectErase], ['editing', collectEditing]]) {
    test(prefix + ' collector executes only the ordered known plan and restores last', async () => {
        const writes = [], calls = [];
        await collect({ config: { runId }, fence: { write: async value => { writes.push(value); return barrier; } },
            owner: async (kind, ordering, extra) => { calls.push({ kind, ordering, extra }); return { screen: initialEditingScreen() }; } });
        const cases = makeEditingCases(10, 48).filter(value => value.id.startsWith(prefix + '.'));
        assert.deepEqual(writes.slice(1, -1), cases.flatMap(value => [value.setup, value.sequence]));
        assert.equal(calls.length, cases.length * 2 + 3); assert.equal(calls.at(-1).kind, prefix + '-finish');
        assert.ok(writes.at(-1).includes('\x1b[3J')); assert.ok(writes.at(-1).endsWith('\x1b[?25h'));
        assert.deepEqual(calls.slice(2, -1).map(value => value.extra), cases.flatMap(value => ['control', 'after'].map(stageId => ({ caseId: value.id, stageId }))));
    });
    test(prefix + ' initial optional refusal writes nothing; missing required facts stay fatal', async () => {
        let writes = 0;
        const error = Object.assign(new Error('budget'), { code: 'screenDetailsUnavailable' });
        const options = { config: { runId }, fence: { write: async () => { writes++; return barrier; } }, owner: async () => { throw error; } };
        assert.deepEqual(await collect(options), { status: 'unavailable', reason: error.code }); assert.equal(writes, 0);
        options.owner = async () => { const screen = initialEditingScreen(); delete screen.bufferState; return { screen }; };
        await assert.rejects(collect(options)); assert.equal(writes, 0);
    });
    for (const fatal of [false, true]) test(prefix + ' failure restores with ' + (fatal ? 'teardown-only output' : 'healthy fence'), async () => {
        const writes = [], calls = [], failure = new Error('target failed'); let failed = false;
        const fence = { get failed() { return failed; }, write: async value => { writes.push(value); return barrier; },
            writeCleanup: async value => { writes.push(value); calls.push('cleanup'); } };
        await assert.rejects(collect({ config: { runId }, fence, owner: async kind => {
            calls.push(kind); if (kind === prefix + '-read') { failed = fatal; throw failure; }
            return { screen: initialEditingScreen() };
        } }), value => value === failure);
        assert.ok(writes.at(-1).includes('\x1b[?1049l')); assert.ok(writes.at(-1).includes('\x1b[3J'));
        assert.equal(calls.at(-1), fatal ? 'cleanup' : prefix + '-finish');
        if (fatal) assert.ok(!calls.includes(prefix + '-finish'));
    });
}
