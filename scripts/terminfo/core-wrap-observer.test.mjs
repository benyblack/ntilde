import test from 'node:test';
import assert from 'node:assert/strict';
import { OwnedCellObserver } from './agent-observer.mjs';
import { WrapObserver } from './core-wrap-observer.mjs';

const runId = 'a'.repeat(32), paneId = '11111111-1111-1111-1111-111111111111', barrier = '\x1b[?1;2c';
const screen = (cols, text = '') => ({ rows: 40, cols, lines: [text], cursorRow: 0, cursorCol: text.length });
function create(performResize) {
    return new WrapObserver({ runId, performResize, expectedOriginal: { rows: 40, cols: 140 }, expectedPaneId: paneId });
}
function action(rows, cols) {
    return { runId, paneId, app: { rows, cols, exact: true, layoutSettled: true },
        terminalOutput: { tool: '/bin/stty', outputProcessing: false, description: `${rows} rows; ${cols} columns; -opost` } };
}
test('only the parent plan can resize and a complete sequence restores the original screen', async () => {
    const originalCapture = OwnedCellObserver.prototype.capture, values = [screen(140), screen(140), screen(80), screen(80),
        screen(80, 'NTILDE_CW_' + runId), screen(80, 'NTILDE_CW_' + runId), screen(80), screen(80), screen(140), screen(140)];
    OwnedCellObserver.prototype.capture = async function () { return structuredClone(values.shift()); };
    const calls = [], observer = create(async (rows, cols) => { calls.push([rows, cols]); return action(rows, cols); });
    try {
        await observer.handle({ kind: 'cw-geometry-start' }, () => 100);
        await assert.rejects(observer.handle({ kind: 'cw-narrow', cols: 90 }, () => 100), /worker supplied/);
        await observer.handle({ kind: 'cw-narrow' }, () => 100);
        await observer.handle({ kind: 'cw-start' }, () => 100);
        await observer.handle({ kind: 'cw-bind', barrier }, () => 100);
        await observer.handle({ kind: 'cw-finish', barrier }, () => 100);
        await observer.handle({ kind: 'cw-restore' }, () => 100);
        await observer.handle({ kind: 'cw-seal' }, () => 100);
        assert.equal(observer.restored, true); assert.deepEqual(calls, [[40, 80], [40, 140]]);
    } finally { OwnedCellObserver.prototype.capture = originalCapture; }
});
test('a partial resize is retained and may only abort to original dimensions', async () => {
    const originalCapture = OwnedCellObserver.prototype.capture;
    OwnedCellObserver.prototype.capture = async () => screen(140);
    const calls = [], observer = create(async (rows, cols) => { calls.push([rows, cols]); const result = action(rows, cols);
        if (cols === 80) result.app.exact = false; return result; });
    try {
        await observer.handle({ kind: 'cw-geometry-start' }, () => 100);
        await assert.rejects(observer.handle({ kind: 'cw-narrow' }, () => 100), /mismatched resize/);
        await observer.handle({ kind: 'cw-abort' }, () => 100);
        assert.deepEqual(calls, [[40, 80], [40, 140]]); assert.equal(observer.restored, false);
        assert.equal(observer.actions[0].observed.app.exact, false);
    } finally { OwnedCellObserver.prototype.capture = originalCapture; }
});
