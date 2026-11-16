import assert from 'node:assert/strict';
import { test } from 'node:test';
import { makeOsc633Experiments, osc633Pinned } from './osc633-contracts.mjs';

test('the exact pinned fixtures and separately prepared positive controls cover each lifecycle marker', () => {
    const plans = makeOsc633Experiments('a'.repeat(32));
    assert.equal(plans.length, 30);
    for (const [index, marker] of ['A', 'B', 'C', 'D;0'].entries()) {
        const id = `extensions.osc633-${'abcd'[index]}`;
        const selected = plans.filter(item => item.owner === id);
        const exact = `\x1b]633;${marker}\x07X`;
        assert.deepEqual(osc633Pinned[id].termlessFeeds, [exact]);
        assert.deepEqual(osc633Pinned[id].terminalWrites, ['\x1b[1;1H\x1b[2K', exact]);
        for (const name of ['stock', 'effect']) {
            const feeds = selected.find(item => item.name === name).plan.filter(step => step.feed).map(step => step.feed);
            assert.deepEqual(feeds.slice(-2), osc633Pinned[id].terminalWrites);
        }
        const control = selected.find(item => item.name === 'positive-control').plan;
        assert.equal(control.at(-2).feed, `\x1b]133;${marker}\x07X`);
        assert.equal(control.at(-1).read, 'positive');
        assert.equal(selected.find(item => item.name === 'st-terminator').plan.at(-2).feed, `\x1b]633;${marker}\x1b\\X`);
        const effect = selected.find(item => item.name === 'effect').plan;
        assert.deepEqual(effect.filter(step => step.read).map(step => step.read), ['prior', 'ordinary', 'pinned']);
        assert.ok(effect.some(step => step.feed === 'X'));
        const gates = selected.filter(item => item.name.startsWith('gate-'));
        assert.equal(gates.length, index === 0 ? 2 : 3);
        for (const gate of gates) {
            assert.deepEqual(gate.plan.filter(step => step.read).map(step => step.read), ['prior', 'post']);
            assert.notEqual(gate.plan.at(-2).feed, exact);
        }
    }
    for (const [name, marker] of [['no-exit-code', 'D'], ['nonzero-exit-code', 'D;7']]) {
        const plan = plans.find(item => item.name === name).plan;
        assert.equal(plan.at(-2).feed, `\x1b]633;${marker}\x07`);
    }
    assert.throws(() => makeOsc633Experiments('bad'), /identity/);
});
