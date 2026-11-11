import test from 'node:test';
import assert from 'node:assert/strict';
import { collectWrap } from './core-wrap-collector.mjs';
import { wrapPlan } from './core-wrap-contract.mjs';

function harness(failAt) {
    const events = [], barrier = '\x1b[?1;2c';
    const fence = { write: async bytes => { events.push(['write', bytes]); return barrier; },
        cursorReport: async () => { events.push(['cpr']); return { barrier, reply: { raw: '\x1b[1;1R', row: 1, col: 1 } }; } };
    const owner = async (kind, ...args) => { events.push([kind, ...args]); if (kind === failAt) throw new Error('refused');
        return { screen: { bufferState: { autoWrap: false } } }; };
    return { events, fence, owner };
}
test('collection keeps each text contiguous and restores the original mode and geometry', async () => {
    const h = harness(), run = await collectWrap({ ...h, config: { runId: 'a'.repeat(32) } });
    assert.deepEqual(run.feeds, wrapPlan().filter(s => s.feed).map(s => s.feed));
    assert.deepEqual(h.events.slice(-2).map(x => x[0]), ['cw-restore', 'cw-seal']);
    assert.ok(h.events.find(e => e[0] === 'write' && e[1].endsWith('\x1b[?7l\x1b7\x1b[?47l')));
});
test('failed observations and partial resize attempt restoration without a seal', async () => {
    for (const failAt of ['cw-narrow', 'cw-read', 'cw-restore']) {
        const h = harness(failAt);
        await assert.rejects(collectWrap({ ...h, config: { runId: 'a'.repeat(32) } }), /refused/);
        assert.ok(h.events.some(e => e[0] === 'cw-abort'));
        assert.ok(!h.events.some(e => e[0] === 'cw-seal'));
    }
});
