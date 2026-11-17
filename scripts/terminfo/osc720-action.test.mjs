import assert from 'node:assert/strict';
import test from 'node:test';
import { osc720WheelNotches, runViewAction } from './osc720-action.mjs';

const attempt = event => ({ request: { ...event, paneId: 'p' }, app: { eventDispatched: true, handled: true, boundary: 'avalonia-routed', observationAvailable: true },
    postVerified: true, observedAt: '2026-11-17T12:00:00.000Z', before: { big: 'x'.repeat(10) } });

test('one wheel notch is requested through the owned input path and recorded compactly', async () => {
    const seen = [];
    const record = await runViewAction({ action: 'scroll-up', config: { id: 1 }, budget: () => 4000, input: async (config, event, budget) => { seen.push([config, event, budget()]); return attempt(event); } });
    assert.equal(osc720WheelNotches, 1);
    assert.deepEqual(seen, [[{ id: 1 }, { kind: 'mouseWheel', wheelDelta: 1, column: 1, row: 1 }, 4000]]);
    assert.equal(record.notches.length, 1);
    assert.deepEqual(Object.keys(record.notches[0]), ['request', 'eventDispatched', 'handled', 'boundary', 'observationAvailable', 'postVerified', 'observedAt']);
    assert.equal(record.notches[0].request.wheelDelta, 1);
    assert.equal(JSON.stringify(record).includes('big'), false);
});

test('scroll-down sends the opposite notch', async () => {
    const seen = [];
    await runViewAction({ action: 'scroll-down', config: {}, budget: () => 1, input: async (config, event) => { seen.push(event); return attempt(event); } });
    assert.deepEqual(seen, [{ kind: 'mouseWheel', wheelDelta: -1, column: 1, row: 1 }]);
});

test('an unknown action or a failed notch is refused and never retried', async () => {
    await assert.rejects(runViewAction({ action: 'scroll-sideways', config: {}, budget: () => 1 }), /Invalid OSC 720 action/);
    let calls = 0;
    await assert.rejects(runViewAction({ action: 'scroll-up', config: {}, budget: () => 1, input: async () => { calls++; throw new Error('Owned generated-input action failed.'); } }), /failed/);
    assert.equal(calls, 1);
});
