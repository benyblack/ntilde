import test from 'node:test';
import assert from 'node:assert/strict';
import { OwnedReflowObserver } from './ascii-reflow-observer.mjs';
import { initialReflowScreen, reflowWire, reflowPaneId, reflowActionProof } from './fixtures/reflow-journal.mjs';
const nonce = 'NTILDE_RF_' + 'a'.repeat(32), barrier = '\x1b[?1;2c';
function fixture(rows = 40, cols = 140) {
    let value = initialReflowScreen(rows, cols), result = reflowActionProof, corrupt = false;
    const reads = [], actions = [];
    const observer = new OwnedReflowObserver({ nonce, profileName: 'fixture', expectedPaneId: reflowPaneId,
        endpoint: () => 'private', rpc: async (_, method, params) => {
            reads.push({ method, params });
            if (method === 'listSessions') return { sessions: [{ paneId: reflowPaneId, kind: 'local', profileName: 'fixture', rows: value.rows, cols: value.cols }] };
            if (method === 'readScrollback') { if (corrupt) value.cursorCol++; return structuredClone(value.history); }
            return structuredClone(value);
        }, performResize: async (r, c) => { actions.push({ rows: r, cols: c }); return result(r, c); } });
    const call = (kind, extra = {}) => observer.handle({ kind: 'reflow-' + kind, barrier: kind === 'start' ? null : barrier, ...extra }, () => 1000);
    return { observer, reads, actions, call, set: next => { value = next; }, proof: next => { result = next; }, corrupt: () => { corrupt = true; } };
}
async function bind(f) {
    await f.call('start');
    const screen = initialReflowScreen();
    screen.lines[0] = nonce; screen.cursorCol = nonce.length; screen.bufferState.alternateScreen = true;
    f.set(screen); await f.call('bind');
}
async function widthControl(f) {
    const item = f.observer.cases[0]; f.set(reflowWire(item.before));
    await f.call('read', { caseId: item.id, stageId: 'control' }); return item;
}
test('exact known changes retain 9 paired full frames and four App/kernel action proofs', async () => {
    const f = fixture(); await bind(f);
    for (const item of f.observer.cases) {
        f.set(reflowWire(item.before)); await f.call('read', { caseId: item.id, stageId: 'control' });
        for (const stageId of ['after', 'restored']) {
            await f.call('apply', { caseId: item.id, stageId });
            f.set(reflowWire(item[stageId])); await f.call('read', { caseId: item.id, stageId });
        }
    }
    f.set(initialReflowScreen()); await f.call('finish');
    assert.equal(f.observer.restored, true); assert.equal(f.observer.frames.length, 9);
    assert.equal(f.observer.actions.length, 4);
    assert.equal(f.reads.filter(value => value.method === 'readScrollback').length, 18);
    assert.equal(f.observer.before.snapshots, 9); assert.equal(f.observer.after.snapshots, 9);
    assert.deepEqual(f.actions[0], { rows: 40, cols: 133 }); assert.deepEqual(f.actions[1], { rows: 40, cols: 140 });
    assert.deepEqual(f.actions[2], { rows: 36, cols: 133 }); assert.deepEqual(f.actions[3], { rows: 40, cols: 140 });
    assert.equal(f.observer.frames[3].actionIndex, 0); assert.equal(f.observer.frames[4].actionIndex, 1);
    assert.equal(f.observer.frames[6].actionIndex, 2); assert.equal(f.observer.frames[7].actionIndex, 3);
    await assert.rejects(f.call('start'));
});
test('an unbound, reordered or caller-dimensioned action never reaches resize transport', async () => {
    const f = fixture(); await assert.rejects(f.call('apply')); assert.equal(f.actions.length, 0);
    await bind(f); await assert.rejects(f.call('apply', { caseId: f.observer.cases[0].id, stageId: 'after' }));
    const item = await widthControl(f);
    await assert.rejects(f.call('apply', { caseId: item.id, stageId: 'after', rows: 1, cols: 1 }));
    await assert.rejects(f.call('apply', { caseId: item.id, stageId: 'after', target: { rows: 1, cols: 1 } }));
    await assert.rejects(f.call('apply', { caseId: 'invented', stageId: 'after' })); assert.equal(f.actions.length, 0);
});
test('a read cannot substitute for its required action and a duplicate action cannot resize twice', async () => {
    const f = fixture(); await bind(f); const item = await widthControl(f), reads = f.reads.length;
    await assert.rejects(f.call('read', { caseId: item.id, stageId: 'after' })); assert.equal(f.reads.length, reads);
    await f.call('apply', { caseId: item.id, stageId: 'after' });
    await assert.rejects(f.call('apply', { caseId: item.id, stageId: 'after' })); assert.equal(f.actions.length, 1);
});
for (const [name, mutate] of [
    ['another action pane', proof => { proof.paneId = '22222222-2222-2222-2222-222222222222'; }],
    ['another launch', proof => { proof.runId = 'b'.repeat(32); }],
    ['missing parent timestamp', proof => { delete proof.observedAt; }],
    ['a no-op App result that never changed the grid', proof => { proof.app.cols = 140; }],
    ['partial App facts', proof => { proof.app.layoutSettled = false; proof.app.exact = false; }],
    ['another actual App grid', proof => { proof.app.cols++; }],
    ['another kernel grid', proof => { proof.terminalOutput.description = '40 rows; 134 columns;\n-opost'; }],
    ['unverified output translation', proof => { proof.terminalOutput.description += ' opost'; }],
]) test(name + ' cannot advance the plan but remains retained', async () => {
    const f = fixture(); await bind(f); const item = await widthControl(f);
    f.proof((rows, cols) => { const proof = reflowActionProof(rows, cols); mutate(proof); return proof; });
    await assert.rejects(f.call('apply', { caseId: item.id, stageId: 'after' }));
    assert.equal(f.observer.actions.length, 1); assert.equal(f.observer.frames.length, 3); assert.equal(f.observer.next, 1);
    assert.equal(f.observer.actions[0].error, undefined);
    await assert.rejects(f.call('finish')); assert.equal(f.observer.restored, false);
});
test('a changing history read cannot qualify a paired reflow frame', async () => {
    const f = fixture(); await bind(f); f.corrupt();
    f.set(reflowWire(f.observer.cases[0].before));
    await assert.rejects(f.call('read', { caseId: f.observer.cases[0].id, stageId: 'control' }));
    assert.equal(f.observer.frames.length, 2);
});
test('a helper failure after its App request retains the attempted target and error without advancing', async () => {
    const f = fixture(); await bind(f); const item = await widthControl(f);
    const failure = new Error('Post-request kernel proof failed.');
    f.proof(() => { throw failure; });
    await assert.rejects(f.call('apply', { caseId: item.id, stageId: 'after' }), error => error === failure);
    assert.deepEqual(f.observer.actions, [{ index: 0, caseId: item.id, stageId: 'after',
        target: item.target, observed: null, error: failure.message }]);
    assert.equal(f.observer.next, 1); assert.equal(f.observer.frames.length, 3);
    await assert.rejects(f.call('finish')); assert.equal(f.observer.restored, false);
});
test('abort can request only the original geometry and permanently prevents completion', async () => {
    const f = fixture(); await bind(f); await widthControl(f);
    await f.call('abort'); assert.deepEqual(f.actions, [{ rows: 40, cols: 140 }]);
    assert.equal(f.observer.aborted, true); await assert.rejects(f.call('finish')); assert.equal(f.observer.restored, false);
});
test('unsupported initial geometry is a typed refusal before any mutation', async () => {
    const f = fixture(12, 140);
    await assert.rejects(f.call('start'), error => error.code === 'asciiReflowGeometryUnavailable');
    assert.equal(f.observer.baseline, undefined); assert.equal(f.observer.frames.length, 0); assert.equal(f.actions.length, 0);
});
