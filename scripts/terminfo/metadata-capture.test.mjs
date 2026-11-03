import test from 'node:test';
import assert from 'node:assert/strict';
import { OwnedMetadataObserver } from './metadata-capture.mjs';
import { syntheticMetadataJournal, metadataPaneId } from './fixtures/metadata-journal.mjs';
const runId = 'a'.repeat(32);
function fixture() {
    const frames = syntheticMetadataJournal(), reads = []; let frame = frames[0], corrupt = false, metadataReads = 0;
    const observer = new OwnedMetadataObserver({ runId, profileName: 'fixture', expectedPaneId: metadataPaneId,
        endpoint: () => 'private', rpc: async (_, method) => {
            reads.push(method); const value = structuredClone(frame.before);
            if (method === 'listSessions') return { sessions: [{ paneId: metadataPaneId, kind: 'local', profileName: 'fixture', rows: value.screen.rows, cols: value.screen.cols }] };
            if (method === 'readScreen') return value.screen;
            if (method === 'readScrollback') return value.screen.history;
            if (method === 'getSessionStatus') return value.rawStatus;
            assert.equal(method, 'readPaneMetadata');
            if (corrupt && ++metadataReads === 2) value.metadata.oscTitle = 'changed between reads';
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
});
