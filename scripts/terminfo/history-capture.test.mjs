import assert from 'node:assert/strict';
import { test } from 'node:test';
import { OwnedHistoryObserver } from './history-capture.mjs';
import { makeHistoryCases } from './history-cases.mjs';
import { initialEditingScreen, editingWire, editingPaneId } from './fixtures/editing-journal.mjs';
const runId = 'a'.repeat(32), nonce = 'NTILDE_HS_' + runId, barrier = '\x1b[?1;2c';
function fixture() {
    let value = initialEditingScreen(), corrupt = false;
    const calls = [], observer = new OwnedHistoryObserver({ nonce, profileName: 'fixture', expectedPaneId: editingPaneId,
        endpoint: () => 'private', rpc: async (_, method, params) => {
            calls.push({ method, params });
            if (method === 'listSessions') return { sessions: [{ paneId: editingPaneId, profileName: 'fixture', kind: 'local', rows: 10, cols: 48 }] };
            if (method === 'readScrollback') { if (corrupt) value.cursorCol++; return structuredClone(value.history); }
            return structuredClone(value);
        } });
    const call = (kind, extra = {}) => observer.handle({ kind: 'history-' + kind, barrier: kind === 'start' ? null : barrier, ...extra }, () => 1000);
    return { observer, calls, call, set: next => { value = next; }, corrupt: () => { corrupt = true; } };
}
async function bind(f) {
    await f.call('start'); const value = initialEditingScreen(); value.lines[0] = nonce; value.cursorCol = 42; value.bufferState.alternateScreen = true;
    f.set(value); await f.call('bind');
}
test('paired history observer completes exactly 63 frames with two actual history reads per frame', async () => {
    const f = fixture(); await bind(f); await assert.rejects(f.call('finish'));
    for (const item of makeHistoryCases(10, 48)) for (const stageId of ['control', 'after']) {
        f.set(editingWire(stageId === 'control' ? item.before : item.after)); await f.call('read', { caseId: item.id, stageId });
    }
    f.set(initialEditingScreen()); await f.call('finish'); assert.equal(f.observer.restored, true); assert.equal(f.observer.frames.length, 63);
    assert.equal(f.observer.before.snapshots, 63); assert.equal(f.observer.after.snapshots, 63);
    const history = f.calls.filter(value => value.method === 'readScrollback'); assert.equal(history.length, 126);
    assert.ok(history.every(value => value.params.paneId === editingPaneId && value.params.startLine === 0 && value.params.maxLines === 64));
    assert.ok(f.calls.filter(value => value.method === 'readScreen').every(value => value.params.includeBufferState === true));
    const count = f.calls.length; await assert.rejects(f.call('start')); assert.equal(f.calls.length, count);
});
test('invented targets and reordered stages cannot reach a history or screen read', async () => {
    const f = fixture(); await bind(f); const count = f.calls.length;
    await assert.rejects(f.call('read', { caseId: 'invented', stageId: 'control' }));
    await assert.rejects(f.call('read', { caseId: f.observer.cases[0].id, stageId: 'after' })); assert.equal(f.calls.length, count);
});
test('state changing during a history read cannot qualify a paired frame', async () => {
    const f = fixture(); await bind(f); f.corrupt(); const item = f.observer.cases[0]; f.set(editingWire(item.before));
    await assert.rejects(f.call('read', { caseId: item.id, stageId: 'control' })); assert.equal(f.observer.frames.length, 2);
});
test('older endpoints missing requested modes fail before any history request or fixture writes', async () => {
    const f = fixture(), value = initialEditingScreen(); delete value.bufferState; f.set(value);
    await assert.rejects(f.call('start')); assert.equal(f.calls.filter(value => value.method === 'readScrollback').length, 0);
    assert.equal(f.observer.frames.length, 0);
});
