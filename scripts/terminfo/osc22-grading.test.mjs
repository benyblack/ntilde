import assert from 'node:assert/strict';
import test from 'node:test';
import { syntheticOsc22Evidence, withPointer } from './fixtures/osc22-evidence.mjs';
import { buildOsc22GradingReport } from './osc22-grading.mjs';
const derive = data => buildOsc22GradingReport(data.frames, data.run, data.runId, data.adapter);

test('complete independent requested/control pairs grade beside a preserved pinned inconclusive', () => {
    const data = syntheticOsc22Evidence(), report = derive(data), item = report.cases[0];
    assert.equal(data.frames.length, 18);
    assert.equal(item.conclusion.outcome, 'inconclusive');
    assert.equal(item.effectContract.outcome, 'supported');
    assert.equal(item.divergence.kind, 'pinned-inconclusive-by-design');
    assert.ok(item.effectContract.validity.every(item => item.held));
});

for (const field of ['requested', 'controlCursor']) test('a wrong required ' + field + ' is unsupported only with valid controls', () => {
    const data = syntheticOsc22Evidence({ adjust(name, pointer) {
        if (name === 'stock') pointer[field] = field === 'requested' ? 'text' : 'Ibeam';
    } });
    const item = derive(data).cases[0];
    assert.equal(item.effectContract.outcome, 'unsupported');
    assert.equal(item.pinned.terminal.observation.outcome, 'inconclusive');
});

test('failed positive or negative delivery controls cannot establish unsupported', () => {
    for (const target of ['delivery-text', 'unknown-control']) {
        const data = syntheticOsc22Evidence({ adjust(name, pointer) {
            if (name === target) Object.assign(pointer, { requested: null, controlCursor: 'Arrow' });
        } });
        assert.equal(derive(data).cases[0].effectContract.outcome, 'inconclusive');
    }
});

test('missing independent readback or a link override is inconclusive', () => {
    for (const missing of [false, true]) {
        const data = syntheticOsc22Evidence();
        data.frames[3] = withPointer(data.frames[3], missing ? undefined : { requested: 'pointer', controlCursor: 'Hand', linkOverride: true });
        assert.equal(derive(data).cases[0].effectContract.outcome, 'inconclusive');
    }
});

test('changed cleanup, feed, pinned verdict or identities are refused', () => {
    for (const mutate of [
        d => d.frames[d.frames.length - 1] = withPointer(d.frames.at(-1), { requested: 'text', controlCursor: 'Ibeam', linkOverride: false }),
        d => d.run.experiments[1].feeds[1] = 'changed',
        d => d.run.pinned[0].terminal.pass = true,
        d => d.adapter.id = 'other',
        d => d.frames.pop(),
        d => d.frames[3].after.metadata.pointerShape.requested = 'text',
    ]) {
        const data = syntheticOsc22Evidence(); mutate(data);
        assert.throws(() => derive(data));
    }
});

test('a read without an observable standard cursor cannot establish failure', () => {
    const data = syntheticOsc22Evidence({ adjust(name, pointer) {
        if (name === 'stock') pointer.controlCursor = 'other';
    } });
    const item = derive(data).cases[0];
    assert.equal(item.effectContract.outcome, 'inconclusive');
    assert.equal(item.divergence, undefined);
});
