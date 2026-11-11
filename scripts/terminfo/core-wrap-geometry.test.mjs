import test from 'node:test';
import assert from 'node:assert/strict';
import { validateWrapGeometry } from './core-wrap-geometry.mjs';

const id = 'a'.repeat(32), paneId = '11111111-1111-1111-1111-111111111111';
function fixture() {
    const original = { rows: 40, cols: 140 }, before = { ...original, state: 'unchanged' };
    const action = (index, cols) => ({ index, target: { rows: 40, cols }, observed: { runId: id, paneId,
        observedAt: '2026-11-11T12:00:00.000Z', app: { rows: 40, cols, exact: true, layoutSettled: true },
        terminalOutput: { tool: '/bin/stty', outputProcessing: false, description: `40 rows; ${cols} columns; -opost` } } });
    return { runId: id, paneId, original, before, after: structuredClone(before), actions: [action(0, 80), action(1, 140)],
        frames: [{ paneId, screen: { rows: 40, cols: 80 } }] };
}
test('App and kernel geometry must agree on the target and restoration', () => {
    assert.doesNotThrow(() => validateWrapGeometry(fixture()));
    for (const mutate of [
        f => { f.actions[0].observed.app.exact = false; },
        f => { f.actions[0].observed.app.layoutSettled = false; },
        f => { f.actions[0].observed.terminalOutput.description = '40 rows; 140 columns; -opost'; },
        f => { f.actions[1].observed.terminalOutput.description = '40 rows; 80 columns; -opost'; },
        f => { f.actions[0].observed.paneId = '22222222-2222-2222-2222-222222222222'; },
        f => { f.frames[0].screen.cols = 140; },
        f => { f.after.state = 'changed'; },
        f => { f.actions.pop(); },
    ]) { const value = fixture(); mutate(value); assert.throws(() => validateWrapGeometry(value)); }
});
