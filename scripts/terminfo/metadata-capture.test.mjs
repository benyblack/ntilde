import test from 'node:test';
import assert from 'node:assert/strict';
import { OwnedMetadataObserver, initialMetadataRefusal, metadataFailure } from './metadata-capture.mjs';
import { syntheticMetadataJournal, metadataPaneId } from './fixtures/metadata-journal.mjs';
const runId = 'a'.repeat(32);
function fixture(inject = () => {}) {
    const frames = syntheticMetadataJournal(), reads = []; let frame = frames[0], corrupt = false, metadataReads = 0;
    const observer = new OwnedMetadataObserver({ runId, profileName: 'fixture', expectedPaneId: metadataPaneId,
        endpoint: () => 'private', rpc: async (_, method) => {
            reads.push(method); const value = structuredClone(frame.before);
            inject(method, value, reads);
            if (method === 'listSessions') return { sessions: [{ paneId: metadataPaneId, kind: 'local', profileName: 'fixture', rows: value.screen.rows, cols: value.screen.cols }] };
            if (method === 'readScreen') return value.screen;
            if (method === 'readScrollback') return value.screen.history;
            if (method === 'getSessionStatus') return value.rawStatus;
            assert.equal(method, 'readPaneMetadata');
            if (corrupt && ++metadataReads % 2 === 0) value.metadata.oscTitle = 'changed between reads';
            return value.metadata;
        } });
    return { observer, frames, reads, set: value => { frame = value; }, corrupt: () => { corrupt = true; metadataReads = 0; },
        call: value => observer.handle({ kind: value.kind, barrier: value.barrier,
            ...(value.caseId ? { caseId: value.caseId, stageId: value.stageId } : {}) }, () => 1000) };
}
test('all 46 staged observations require two complete viewport, host-control and raw-status reads', async () => {
    const f = fixture(); for (const frame of f.frames) { f.set(frame); await f.call(frame); }
    assert.equal(f.observer.restored, true); assert.deepEqual(f.observer.frames, f.frames);
    assert.equal(f.reads.filter(value => value === 'readPaneMetadata').length, 92);
    assert.equal(f.reads.filter(value => value === 'getSessionStatus').length, 92);
    assert.equal(f.reads.filter(value => value === 'readScrollback').length, 92);
    await assert.rejects(f.call(f.frames[0]));
});
test('unbound, duplicate or reordered requests refuse before transport', async () => {
    const f = fixture(); await assert.rejects(f.call(f.frames[2])); assert.equal(f.reads.length, 0);
    await f.call(f.frames[0]); f.set(f.frames[1]); await f.call(f.frames[1]); const count = f.reads.length;
    await assert.rejects(f.call(f.frames[1])); await assert.rejects(f.call(f.frames[3])); await assert.rejects(f.call(f.frames.at(-1)));
    assert.equal(f.reads.length, count);
});
test('actual paired host mutation cannot be journaled as a stable observation', async () => {
    const f = fixture(); await f.call(f.frames[0]); f.set(f.frames[1]); f.corrupt();
    await assert.rejects(f.call(f.frames[1])); assert.equal(f.observer.frames.length, 1); assert.equal(f.observer.restored, false);
});
test('wrong nonce and failed prepared baseline refuse fixture binding', async () => {
    const f = fixture(); await f.call(f.frames[0]); const frame = structuredClone(f.frames[1]);
    frame.before.screen.lines[0] = 'wrong'; f.set(frame);
    await assert.rejects(f.call(frame)); assert.equal(f.observer.frames.length, 1);
    assert.equal(f.observer.lastAttempt.operation, 'bind');
    assert.equal(f.observer.lastAttempt.before.screen.lines[0], 'wrong');
});

for (const [name, inject] of [
    ['timeout', (method, value, reads) => { if (method === 'readPaneMetadata' && reads.filter(v => v === method).length === 2) throw new Error('second capture timed out'); }],
    ['malformed metadata', (method, value, reads) => { if (method === 'readPaneMetadata' && reads.filter(v => v === method).length === 2) value.metadata.buffer.links = 'malformed'; }],
    ['malformed status', (method, value, reads) => { if (method === 'getSessionStatus' && reads.filter(v => v === method).length === 2) value.rawStatus.confidence = 'malformed'; }],
]) test('second capture ' + name + ' preserves the first snapshot and bounded original responses before validation', async () => {
    const f = fixture(inject); await assert.rejects(f.call(f.frames[0]));
    const attempt = f.observer.lastAttempt;
    assert.equal(attempt.operation, 'start'); assert.ok(attempt.before); assert.equal(attempt.after, undefined);
    assert.ok(attempt.error); assert.ok(attempt.responseBytes > 0); assert.ok(attempt.responseBytes <= 8 * 1024 * 1024);
    assert.ok(attempt.responses.some(value => value.response));
    if (name === 'timeout') assert.equal(attempt.responses.at(-1).error.message, 'second capture timed out');
    if (name === 'malformed metadata') assert.equal(attempt.responses.at(-1).response.buffer.links, 'malformed');
    if (name === 'malformed status') assert.equal(attempt.responses.at(-1).response.confidence, 'malformed');
    assert.equal(f.observer.frames.length, 0); assert.equal(f.observer.restored, false);
});

for (const code of ['paneMetadataUnavailable', 'screenDetailsUnavailable', 'metadataGeometryUnavailable']) {
    test('clean preflight ' + code + ' retains its initial optional meaning', () => {
        const request = { kind: 'metadata-start' }, error = Object.assign(new Error('preflight'), { code });
        const observer = { frames: [] };
        assert.equal(initialMetadataRefusal(request, error, observer), true);
        assert.equal(metadataFailure(request, error, observer), error);
    });
    test('typed second-capture ' + code + ' is fatal and retains the first capture/raw responses', async () => {
        const request = { kind: 'metadata-start' }, error = Object.assign(new Error('second read refused'), { code });
        const f = fixture((method, value, reads) => {
            if (method === 'readPaneMetadata' && reads.filter(v => v === method).length === 2) throw error;
        });
        await assert.rejects(f.call(f.frames[0]), value => value === error);
        assert.ok(f.observer.lastAttempt.before); assert.ok(f.observer.lastAttempt.responses.some(value => Object.hasOwn(value, 'response')));
        assert.equal(initialMetadataRefusal(request, error, f.observer), false);
        const fatal = metadataFailure(request, error, f.observer);
        assert.equal(fatal.code, undefined); assert.equal(fatal.cause, error); assert.equal(f.observer.frames.length, 0);
    });
}
test('even a successful session response before an initial metadata refusal prevents a clean skip', () => {
    const request = { kind: 'metadata-start' }, error = Object.assign(new Error('refused'), { code: 'paneMetadataUnavailable' });
    const observer = { frames: [], lastAttempt: { responses: [{ method: 'listSessions', response: { sessions: [] } }] } };
    assert.equal(initialMetadataRefusal(request, error, observer), false); assert.equal(metadataFailure(request, error, observer).code, undefined);
    assert.equal(initialMetadataRefusal({ kind: 'metadata-read' }, error, { frames: [] }), false);
});

test('a transient presentation change settles within the same deadline and retains the mismatched pair', async () => {
    const f = fixture((method, value, reads) => {
        if (method === 'readPaneMetadata' && reads.filter(v => v === method).length === 2) value.metadata.presentation.tabText = 'queued refresh';
    });
    await f.call(f.frames[0]);
    const frame = f.observer.frames[0]; assert.equal(frame.settling.length, 1);
    assert.equal(frame.settling[0].after.metadata.presentation.tabText, 'queued refresh');
    assert.deepEqual(frame.before, f.frames[0].before); assert.deepEqual(frame.after, f.frames[0].after);
    assert.equal(f.reads.filter(value => value === 'readPaneMetadata').length, 4);
    for (const next of f.frames.slice(1)) { f.set(next); await f.call(next); }
    const { buildMetadataReport } = await import('./metadata.mjs');
    assert.equal(buildMetadataReport(f.observer.frames, runId).snapshots[0].settling.length, 1);
});
test('perpetually changing presentation exhausts three pairs without accepting or discarding evidence', async () => {
    const f = fixture(); f.corrupt(); await assert.rejects(f.call(f.frames[0]));
    assert.equal(f.observer.frames.length, 0); assert.equal(f.observer.lastAttempt.settling.length, 3);
    assert.equal(f.reads.filter(value => value === 'readPaneMetadata').length, 6);
    assert.ok(f.observer.lastAttempt.responses.length > 0); assert.ok(f.observer.lastAttempt.error);
});
