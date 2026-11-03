import assert from 'node:assert/strict';
import { test } from 'node:test';
import { OwnedCellObserver, screenSnapshot } from './agent-observer.mjs';
const paneId = '11111111-1111-1111-1111-111111111111';
const state = () => ({ scrollTop: 0, scrollBottom: 9, originMode: false, autoWrap: true, insertMode: false, pendingWrap: false, alternateScreen: false, scrollbackLines: 2 });
function raw() { return { rows: 10, cols: 48, lines: new Array(10).fill(''), cursorRow: 0, cursorCol: 0, cursorVisible: true,
    bufferState: state(), cursorMode: { style: 2, blink: true, isDefault: true }, cellDetails: [{ count: 480, overline: false,
        flags: 768, fg: -1, bg: -1, underlineStyle: 0, underlineColorIndex: -1 }] }; }
function fixture() {
    let value = raw(), history = { startLine: 0, totalLines: 2, lines: ['HISTORY0', 'HISTORY1'] };
    const calls = [];
    const observer = new OwnedCellObserver({ expectedPaneId: paneId, profileName: 'fixture', nonce: 'nonce', endpoint: () => 'private',
        includeCellDetails: true, includeCursorMode: true, includeOverline: true, includeBufferState: true,
        rpc: async (_, method, params) => {
            calls.push({ method, params });
            if (method === 'listSessions') return { sessions: [{ paneId, profileName: 'fixture', kind: 'local', rows: 10, cols: 48 }] };
            if (method === 'readScrollback') return history;
            return value;
        } });
    return { observer, calls, value, setHistory: v => { history = v; } };
}
test('prototype capture binds actual history text and mode facts to the same pane and canonical hash input', async () => {
    const f = fixture(), screen = await f.observer.capture(() => 1000);
    assert.equal(f.calls[1].params.includeBufferState, true);
    assert.deepEqual(f.calls[2], { method: 'readScrollback', params: { paneId, startLine: 0, maxLines: 64 } });
    assert.deepEqual(screen.history.lines, ['HISTORY0', 'HISTORY1']); assert.deepEqual(screen.bufferState, state());
    f.value.bufferState.insertMode = true; assert.equal(screen.bufferState.insertMode, false);
    assert.deepEqual(screenSnapshot(screen, false, true, true, true, true), screen);
});
test('an older endpoint missing requested facts fails before any history read', async () => {
    const f = fixture(); delete f.value.bufferState;
    await assert.rejects(f.observer.capture(() => 1000), /editing state/);
    assert.equal(f.calls.some(value => value.method === 'readScrollback'), false);
});
for (const history of [{ startLine: 1, totalLines: 2, lines: ['one','two'] }, { startLine: 0, totalLines: 2, lines: ['partial'] }, { startLine: 0, totalLines: 3, lines: ['one','two','three'] }]) {
    test('partial or concurrently changed history cannot qualify a capture ' + JSON.stringify(history), async () => {
        const f = fixture(); f.setHistory(history); await assert.rejects(f.observer.capture(() => 1000), /history/);
    });
}
test('legacy captures neither request nor project the new facts', async () => {
    const f = fixture(); f.observer.includeBufferState = false;
    const screen = await f.observer.capture(() => 1000);
    assert.equal(f.calls.some(value => value.method === 'readScrollback'), false);
    assert.equal(Object.hasOwn(f.calls[1].params, 'includeBufferState'), false); assert.equal(Object.hasOwn(screen, 'bufferState'), false);
});
