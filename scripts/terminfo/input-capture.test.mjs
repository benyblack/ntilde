import test from 'node:test';
import assert from 'node:assert/strict';
import { OwnedInputObserver } from './input-capture.mjs';
import { boundInputScreen, inputModeReplyHex } from './input-state.mjs';
import { initialInputScreen, inputPaneId, inputRunId, inputCapture, inputActionProof, syntheticInputJournal } from './fixtures/input-journal.mjs';
function fixture() {
    let value = initialInputScreen(), perform = inputActionProof, corrupt = false, nonce = 0;
    const observer = new OwnedInputObserver({ nonce: 'NTILDE_IN_' + inputRunId, profileName: 'fixture', expectedPaneId: inputPaneId,
        endpoint: () => 'private', rpc: async (_, method) => {
            if (method === 'listSessions') return { sessions: [{ paneId: inputPaneId, kind: 'local', profileName: 'fixture', rows: value.rows, cols: value.cols }] };
            if (method === 'readScrollback') { if (corrupt) value.cursorCol++; return structuredClone(value.history); }
            return structuredClone(value);
        }, performInput: async event => perform(event, observer.cases[observer.next].modes) });
    return { observer, set: v => { value = v; }, perform: v => { perform = v; }, corrupt: () => { corrupt = true; },
        call: (kind, extra = {}) => observer.handle({ kind: 'input-' + kind, ...extra }, () => 1000),
        sample: hex => inputCapture(hex, nonce++) };
}
async function bind(f) {
    await f.call('start'); f.set(boundInputScreen(initialInputScreen(), inputRunId));
    await f.call('bind', { capture: f.sample('') });
}
test('39 fixed actions require paired pre/post controls and restoration, then a healthy disposed final seal', async () => {
    const f = fixture(); await bind(f);
    for (const item of f.observer.cases) {
        await f.call('control', { caseId: item.id, capture: f.sample(inputModeReplyHex(item.modes)) });
        await f.call('apply', { caseId: item.id });
        await f.call('read', { caseId: item.id, capture: f.sample(item.expectedHex) });
    }
    f.set(initialInputScreen());
    const { frames, seal } = syntheticInputJournal();
    await f.call('finish', { capture: frames.at(-1).capture }); assert.equal(f.observer.restored, false);
    await f.call('seal', { captureStatus: seal }); assert.equal(f.observer.restored, true);
    assert.equal(f.observer.report.observations.length, 39);
    assert.deepEqual(f.observer.readers.map(x => x.snapshots), [42, 42, 39, 39]);
    await assert.rejects(f.call('seal', { captureStatus: seal }));
});
test('no control, caller-supplied event, wrong order or duplicate action can dispatch', async () => {
    const f = fixture(); await assert.rejects(f.call('apply')); await bind(f);
    const item = f.observer.cases[0]; await assert.rejects(f.call('apply', { caseId: item.id }));
    await f.call('control', { caseId: item.id, capture: f.sample(inputModeReplyHex(item.modes)) });
    await assert.rejects(f.call('apply', { caseId: item.id, event: { kind: 'text', text: 'x' } }));
    assert.equal(f.observer.actions.length, 0);
    await f.call('apply', { caseId: item.id }); await assert.rejects(f.call('apply', { caseId: item.id }));
    assert.equal(f.observer.actions.length, 1);
});
test('mode mismatch or changing paired state prevents any action and permanently fails the suite', async () => {
    for (const corrupt of [false, true]) {
        const f = fixture(); await bind(f); const item = f.observer.cases[0];
        if (corrupt) f.corrupt();
        await assert.rejects(f.call('control', { caseId: item.id, capture: f.sample(corrupt ? inputModeReplyHex(item.modes) : '') }));
        await assert.rejects(f.call('apply', { caseId: item.id })); assert.equal(f.observer.actions.length, 0);
        assert.equal(f.observer.restored, false);
    }
});
test('post-dispatch failure retains exact original result, request and error without an unavailable downgrade', async () => {
    const f = fixture(); await bind(f); const item = f.observer.cases[0];
    await f.call('control', { caseId: item.id, capture: f.sample(inputModeReplyHex(item.modes)) });
    const partial = inputActionProof(item.event, item.modes); partial.postVerified = false;
    f.perform(() => { const error = new Error('post proof lost'); error.inputAttempt = partial; throw error; });
    await assert.rejects(f.call('apply', { caseId: item.id }));
    assert.deepEqual(f.observer.actions[0].observed, partial); assert.equal(f.observer.actions[0].error, 'post proof lost');
    await assert.rejects(f.call('finish')); assert.equal(f.observer.restored, false);
});
