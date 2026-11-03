import test from 'node:test';
import assert from 'node:assert/strict';
import { OwnedSavedObserver } from './saved-capture.mjs';
import { initialEditingScreen, editingPaneId } from './fixtures/editing-journal.mjs';
import { savedWire } from './fixtures/saved-journal.mjs';
import { makeSavedCases } from './saved-cases.mjs';
const nonce = 'NTILDE_SV_' + 'a'.repeat(32), barrier = '\x1b[?1;2c';
function fixture() {
    let value = initialEditingScreen(12,60), corrupt = false;
    const reads = [], observer = new OwnedSavedObserver({ nonce, profileName: 'fixture', expectedPaneId: editingPaneId,
        endpoint: () => 'private', rpc: async (_, method) => {
            reads.push(method);
            if (method === 'listSessions') return { sessions: [{ paneId: editingPaneId, kind: 'local', profileName: 'fixture', rows: value.rows, cols: value.cols }] };
            if (method === 'readScrollback') { if (corrupt) value.cursorCol++; return structuredClone(value.history); }
            return structuredClone(value);
        } });
    return { observer, reads, set: raw => { value = raw; }, corrupt: () => { corrupt = true; },
        call: (kind, extra = {}) => observer.handle({ kind: 'saved-' + kind, barrier: kind === 'start' ? null : barrier, ...extra }, () => 1000) };
}
async function bind(f) {
    await f.call('start'); const raw=initialEditingScreen(12,60); raw.lines[0]=nonce;
    raw.cursorCol=42;raw.bufferState.alternateScreen=true;f.set(raw);await f.call('bind');
}
test('all nine staged cases retain exactly 57 paired full frames with the fixed geometry', async () => {
    const f=fixture();await bind(f);
    for(const item of makeSavedCases(12,60))for(const stage of item.stages){f.set(savedWire(stage.expected));await f.call('read',{caseId:item.id,stageId:stage.id});}
    f.set(initialEditingScreen(12,60));await f.call('finish');
    assert.equal(f.observer.restored,true);assert.equal(f.observer.frames.length,57);
    assert.equal(f.observer.before.snapshots,57);assert.equal(f.observer.after.snapshots,57);
    assert.equal(f.reads.filter(value=>value==='readScrollback').length,114);
    await assert.rejects(f.call('start'));
});
test('an unbound, duplicated or reordered stage fails before read transport', async () => {
    const f=fixture();await assert.rejects(f.call('read'));assert.equal(f.reads.length,0);
    await bind(f);const count=f.reads.length;await assert.rejects(f.call('bind'));
    await assert.rejects(f.call('read',{caseId:'saved.alternate.47',stageId:'entry'}));await assert.rejects(f.call('finish'));
    assert.equal(f.reads.length,count);
});
test('nonce binding requires the complete unchanged baseline aside from its source-known marker', async () => {
    const f=fixture();await f.call('start');const raw=initialEditingScreen(12,60);raw.lines[0]=nonce;
    raw.cursorCol=42;raw.bufferState.alternateScreen=true;raw.cursorMode.blink=false;f.set(raw);
    await assert.rejects(f.call('bind'));assert.equal(f.observer.frames.length,1);
});
test('a later geometry change is rejected by both baseline-bound readers', async () => {
    const f=fixture();await bind(f);f.set(initialEditingScreen(12,61));
    await assert.rejects(f.call('read',{caseId:'saved.alternate.47',stageId:'control'}));
    assert.equal(f.observer.frames.length,2);assert.equal(f.observer.restored,false);
});
test('changing paired state cannot be journaled as a coherent stage', async () => {
    const f=fixture();await bind(f);f.set(savedWire(makeSavedCases(12,60)[0].stages[0].expected));f.corrupt();
    await assert.rejects(f.call('read',{caseId:'saved.alternate.47',stageId:'control'}));assert.equal(f.observer.frames.length,2);
});
test('unsupported initial geometry refuses before any baseline or frame can claim fixture writes', async () => {
    const f=fixture();f.set(initialEditingScreen(9,60));
    await assert.rejects(f.call('start'),{code:'savedGeometryUnavailable'});
    assert.equal(f.observer.baseline,undefined);assert.equal(f.observer.frames.length,0);
});
