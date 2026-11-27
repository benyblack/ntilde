import assert from 'node:assert/strict';
import test from 'node:test';
import { classify } from './osc-flash-contract.mjs';
import { effectVerdict, publish } from './osc-flash-publication.mjs';

test('all three owned receipts, the exact pinned callback and every published frame rederive', async () => {
    const { cases: [result], runs } = await publish(true);
    assert.equal(result.termless, null);
    assert.equal(result.pinned.observation.outcome, 'inconclusive');
    assert.equal(result.contractResult.outcome, 'supported');
    assert.equal(result.stock.outcome, 'inconclusive');
    const states = name => runs[name].captures.map(c => c.state);
    assert.deepEqual(states('baseline'), ['flashed', 'inconclusive', 'steady']);
    assert.deepEqual(states('enabled'), ['flashed', 'flashed', 'flashed']);
    assert.deepEqual(states('disabled'), ['steady', 'steady', 'steady']);
});

test('a late or sparse capture, an unrelated pixel change, and a missing control cannot qualify the flash', () => {
    const frames = Array.from({ length: 24 }, (_, i) => ({
        start: 10 * i, end: 10 * i + 3, pixels: 1000, changed: 0, visible: 0,
    }));
    assert.equal(classify(frames, 31, 35).state, 'steady');
    assert.equal(classify(frames.filter(f => f.start <= 20 || f.start >= 120), 31, 35).state, 'inconclusive');
    frames[5].changed = frames[5].visible = 2;
    assert.equal(classify(frames, 31, 35).state, 'inconclusive');
    frames[5].changed = frames[5].visible = 1000;
    assert.equal(classify(frames, 31, 35).state, 'flashed');
    frames.at(-1).changed = 1;
    assert.equal(classify(frames, 31, 35).state, 'unrestored');
    assert.equal(effectVerdict(['steady', 'flashed', 'flashed'], ['steady', 'steady', 'steady']), 'inconclusive');
    assert.equal(effectVerdict(['flashed', 'flashed', 'flashed'], ['steady', 'flashed', 'steady']), 'inconclusive');
});

