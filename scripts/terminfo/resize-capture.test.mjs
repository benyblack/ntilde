import test from 'node:test';
import assert from 'node:assert/strict';
import { OwnedResizeObserver } from './resize-capture.mjs';
import { initialResizeScreen, resizeWire, resizePaneId, resizeActionProof } from './fixtures/resize-journal.mjs';
const nonce = 'NTILDE_RZ_' + 'a'.repeat(32), barrier = '\x1b[?1;2c';
function fixture() {
    let value = initialResizeScreen(), result = resizeActionProof, corrupt = false;
    const reads = [], actions = [];
    const observer = new OwnedResizeObserver({ nonce, profileName: 'fixture', expectedPaneId: resizePaneId,
        endpoint: () => 'private', rpc: async (_, method, params) => {
            reads.push({ method, params });
            if (method === 'listSessions') return { sessions: [{ paneId: resizePaneId, kind: 'local', profileName: 'fixture', rows: value.rows, cols: value.cols }] };
            if (method === 'readScrollback') { if (corrupt) value.cursorCol++; return structuredClone(value.history); }
            return structuredClone(value);
        }, performResize: async (rows, cols) => { actions.push({ rows, cols }); return result(rows, cols); } });
    const call = (kind, extra = {}) => observer.handle({ kind: 'resize-' + kind, barrier: kind === 'start' ? null : barrier, ...extra }, () => 1000);
    return { observer, reads, actions, call, set: next => { value = next; }, proof: next => { result = next; }, corrupt: () => { corrupt = true; } };
}
async function bind(f) {
    await f.call('start'); const screen = initialResizeScreen(); screen.lines[0] = nonce;
    screen.cursorCol = nonce.length; screen.bufferState.alternateScreen = true;
    f.set(screen); await f.call('bind');
}
async function control(f) {
    const item = f.observer.cases[0]; f.set(resizeWire(item.before));
    await f.call('read', { caseId: item.id, stageId: 'control' }); return item;
}
test('exact known changes retain 12 paired full frames and six App/kernel action proofs', async () => {
    const f = fixture(); await bind(f);
    for (const item of f.observer.cases) {
        f.set(resizeWire(item.before)); await f.call('read', { caseId: item.id, stageId: 'control' });
        for (const stageId of ['after', 'restored']) {
            await f.call('apply', { caseId: item.id, stageId });
            f.set(resizeWire(item[stageId])); await f.call('read', { caseId: item.id, stageId });
        }
    }
    f.set(initialResizeScreen()); await f.call('finish');
    assert.equal(f.observer.restored, true); assert.equal(f.observer.frames.length, 12);
    assert.equal(f.observer.actions.length, 6);
    assert.equal(f.reads.filter(value => value.method === 'readScrollback').length, 24);
    assert.equal(f.observer.before.snapshots, 12); assert.equal(f.observer.after.snapshots, 12);
    assert.deepEqual(f.actions[0], { rows: 12, cols: 53 }); assert.deepEqual(f.actions[1], { rows: 12, cols: 60 });
    assert.equal(f.observer.frames[3].actionIndex, 0); assert.equal(f.observer.frames[4].actionIndex, 1);
    await assert.rejects(f.call('start'));
});
test('an unbound, reordered or caller-dimensioned action never reaches resize transport', async () => {
    const f = fixture(); await assert.rejects(f.call('apply')); assert.equal(f.actions.length, 0);
    await bind(f); await assert.rejects(f.call('apply', { caseId: f.observer.cases[0].id, stageId: 'after' }));
    const item = await control(f);
    await assert.rejects(f.call('apply', { caseId: item.id, stageId: 'after', rows: 1, cols: 1 }));
    await assert.rejects(f.call('apply', { caseId: 'invented', stageId: 'after' })); assert.equal(f.actions.length, 0);
});
test('a read cannot substitute for its required action and a duplicate action cannot resize twice', async () => {
    const f = fixture(); await bind(f); const item = await control(f), reads = f.reads.length;
    await assert.rejects(f.call('read', { caseId: item.id, stageId: 'after' })); assert.equal(f.reads.length, reads);
    await f.call('apply', { caseId: item.id, stageId: 'after' });
    await assert.rejects(f.call('apply', { caseId: item.id, stageId: 'after' })); assert.equal(f.actions.length, 1);
});
for (const [name, mutate] of [
    ['another action pane', proof => { proof.paneId = '22222222-2222-2222-2222-222222222222'; }],
    ['another launch', proof => { proof.runId = 'b'.repeat(32); }],
    ['missing parent timestamp', proof => { delete proof.observedAt; }],
    ['partial App facts', proof => { proof.app.layoutSettled = false; proof.app.exact = false; }],
    ['another actual App grid', proof => { proof.app.cols++; }],
    ['another kernel grid', proof => { proof.terminalOutput.description = '11 rows; 53 columns;\n-opost'; }],
    ['unverified output translation', proof => { proof.terminalOutput.description += ' opost'; }],
]) test(name + ' cannot advance the plan but remains retained', async () => {
    const f = fixture(); await bind(f); const item = await control(f);
    f.proof((rows, cols) => { const proof = resizeActionProof(rows, cols); mutate(proof); return proof; });
    await assert.rejects(f.call('apply', { caseId: item.id, stageId: 'after' }));
    assert.equal(f.observer.actions.length, 1); assert.equal(f.observer.frames.length, 3); assert.equal(f.observer.next, 1);
    await assert.rejects(f.call('finish')); assert.equal(f.observer.restored, false);
});
test('a changing history read cannot qualify a paired resize frame', async () => {
    const f = fixture(); await bind(f); f.corrupt();
    f.set(resizeWire(f.observer.cases[0].before));
    await assert.rejects(f.call('read', { caseId: f.observer.cases[0].id, stageId: 'control' }));
    assert.equal(f.observer.frames.length, 2);
});
test('a helper failure after its App request retains the attempted target and error without advancing', async () => {
    const f = fixture(); await bind(f); const item = await control(f);
    const failure = new Error('Post-request kernel proof failed.');
    f.proof(() => { throw failure; });
    await assert.rejects(f.call('apply', { caseId: item.id, stageId: 'after' }), error => error === failure);
    assert.deepEqual(f.observer.actions, [{ index: 0, caseId: item.id, stageId: 'after',
        target: item.target, observed: null, error: failure.message }]);
    assert.equal(f.observer.next, 1); assert.equal(f.observer.frames.length, 3);
    await assert.rejects(f.call('finish')); assert.equal(f.observer.restored, false);
});
test('abort can request only the original geometry and permanently prevents completion', async () => {
    const f = fixture(); await bind(f); await control(f);
    await f.call('abort'); assert.deepEqual(f.actions, [{ rows: 12, cols: 60 }]);
    assert.equal(f.observer.aborted, true); await assert.rejects(f.call('finish')); assert.equal(f.observer.restored, false);
});
test('the resize observer refuses a request kind or nonce belonging to another resize-driven suite', async () => {
    const f = fixture();
    await assert.rejects(f.observer.handle({ kind: 'reflow-start', barrier: null }, () => 1000), /Invalid owned resize plan/);
    assert.equal(f.reads.length, 0);
    const options = { profileName: 'fixture', expectedPaneId: resizePaneId, endpoint: () => 'private', rpc: async () => ({}), performResize: async () => ({}) };
    assert.throws(() => new OwnedResizeObserver({ ...options, nonce: 'NTILDE_RF_' + 'a'.repeat(32) }), /Invalid owned resize plan/);
});
