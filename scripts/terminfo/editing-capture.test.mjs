import assert from 'node:assert/strict';
import { test } from 'node:test';
import { OwnedEditingObserver } from './editing-capture.mjs';
import { makeEditingCases } from './editing-cases.mjs';
import { compressExpected } from './editing-expectations.mjs';
const paneId = '11111111-1111-1111-1111-111111111111', runId = 'a'.repeat(32), barrier = '\x1b[?1;2c';
function wire(stage) {
    const expected = compressExpected(stage), cellDetails = [];
    for (const run of expected.expectedAttributeRuns) {
        const { flagsMask, flagsValue, ...values } = run.values;
        const value = { overline: false, flags: 256, fg: -1, foregroundArgb: null, underlineStyle: 0, underlineColorIndex: -1, underlineArgb: null, ...values };
        const last = cellDetails.at(-1);
        if (last && JSON.stringify(last.value) === JSON.stringify(value)) last.count += run.count;
        else cellDetails.push({ count: run.count, value });
    }
    return { rows: 10, cols: 48, lines: expected.lines, cursorRow: expected.cursorRow, cursorCol: expected.cursorCol, cursorVisible: true,
        cursorMode: { style: 2, blink: true, isDefault: true }, bufferState: expected.bufferState,
        history: expected.history, cellDetails: cellDetails.map(({ count, value }) => ({ count, ...value })) };
}
function initial() {
    return wire({ grid: Array.from({ length: 10 }, () => Array.from({ length: 48 }, () => ({ text: ' ', overline: false,
        flags: 768, fg: -1, bg: -1, foregroundArgb: null, backgroundArgb: null, underlineStyle: 0, underlineColorIndex: -1, underlineArgb: null }))),
        row: 0, col: 0, top: 0, bottom: 9, alternate: false });
}
function fixture(prefix = 'erase') {
    let value = initial(), changedDuringHistory = false;
    const nonce = `NTILDE_${prefix === 'erase' ? 'ER' : 'EC'}_${runId}`;
    const observer = new OwnedEditingObserver({ prefix, nonce, profileName: 'fixture', expectedPaneId: paneId, endpoint: () => 'private',
        rpc: async (_, method) => {
            if (method === 'listSessions') return { sessions: [{ paneId, profileName: 'fixture', kind: 'local', rows: 10, cols: 48 }] };
            if (method === 'readScrollback') { if (changedDuringHistory) value.cursorCol++; return structuredClone(value.history); }
            return structuredClone(value);
        } });
    const call = (kind, extra = {}) => observer.handle({ kind: `${prefix}-${kind}`, barrier: kind === 'start' ? null : barrier, ...extra }, () => 1000);
    return { observer, call, nonce, set: v => { value = v; }, mutate: () => { changedDuringHistory = true; } };
}
async function bind(f) {
    await f.call('start'); const value = initial(); value.lines[0] = f.nonce; value.cursorCol = f.nonce.length; value.bufferState.alternateScreen = true;
    f.set(value); await f.call('bind');
}
for (const prefix of ['erase', 'editing']) test(`bounded ${prefix} observer enforces known plan and exact state/history restoration`, async () => {
    const f = fixture(prefix); await bind(f);
    await assert.rejects(f.call('finish'));
    const cases = makeEditingCases(10, 48).filter(value => value.id.startsWith(prefix + '.'));
    for (const item of cases) for (const stageId of ['control', 'after']) {
        f.set(wire(stageId === 'control' ? item.before : item.after));
        await f.call('read', { caseId: item.id, stageId });
    }
    f.set(initial()); await f.call('finish'); assert.equal(f.observer.restored, true);
    assert.equal(f.observer.frames.length, cases.length * 2 + 3);
    await assert.rejects(f.call('start'));
});
test('changed state around a history read is fatal', async () => {
    const f = fixture(); await bind(f); f.mutate();
    const item = f.observer.cases[0]; f.set(wire(item.before));
    await assert.rejects(f.call('read', { caseId: item.id, stageId: 'control' }));
});
test('invented/reordered case IDs are refused before a screen read', async () => {
    const f = fixture(); await bind(f);
    await assert.rejects(f.call('read', { caseId: 'invented', stageId: 'control' }));
    await assert.rejects(f.call('read', { caseId: f.observer.cases[0].id, stageId: 'after' }));
});
