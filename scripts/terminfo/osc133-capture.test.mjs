import test from 'node:test';
import assert from 'node:assert/strict';
import { OwnedMetadataObserver, initialMetadataRefusal, metadataFailure } from './metadata-capture.mjs';
import { syntheticOsc133Evidence, osc133PaneId } from './fixtures/osc133-evidence.mjs';
import { matchesOsc133Baseline, osc133ObserverPlan } from './osc133-grading.mjs';
import { osc133Prefix } from './osc133-contracts.mjs';

const runId = 'a'.repeat(32);
/** The metadata observer wired exactly as the runner wires the OSC 133 suite, over a fake endpoint serving the synthetic frames. */
function fixture(inject = () => {}, overrides = {}) {
    const { frames } = syntheticOsc133Evidence(), reads = []; let frame = frames[0];
    const observer = new OwnedMetadataObserver({ runId, profileName: 'fixture', expectedPaneId: osc133PaneId, prefix: osc133Prefix, includeResolvedColors: true,
        planOf: osc133ObserverPlan, matchesBaseline: matchesOsc133Baseline, endpoint: () => 'private', ...overrides,
        rpc: async (_, method) => {
            reads.push(method); const value = structuredClone(frame.before);
            inject(method, value, reads);
            if (method === 'listSessions') return { sessions: [{ paneId: osc133PaneId, kind: 'local', profileName: 'fixture', rows: value.screen.rows, cols: value.screen.cols }] };
            if (method === 'readScreen') return value.screen;
            if (method === 'readScrollback') return value.screen.history;
            if (method === 'getSessionStatus') return value.rawStatus;
            assert.equal(method, 'readPaneMetadata');
            return value.metadata;
        } });
    return { observer, frames, reads, set: value => { frame = value; }, call: value => observer.handle({ kind: value.kind, barrier: value.barrier }, () => 1000) };
}

test('all 49 observations (start, bind, 46 reads, finish) require two complete viewport, host-control, raw-status and resolved-color reads', async () => {
    const f = fixture(); for (const frame of f.frames) { f.set(frame); await f.call(frame); }
    assert.equal(f.frames.length, 49);
    assert.equal(f.observer.restored, true); assert.deepEqual(f.observer.frames, f.frames);
    for (const method of ['readPaneMetadata', 'getSessionStatus', 'readScrollback']) assert.equal(f.reads.filter(value => value === method).length, 98, method);
    for (const frame of f.observer.frames) assert.ok(frame.before.screen.colorBinding && frame.before.screen.resolvedColors, 'every capture keeps its resolved color binding');
    await assert.rejects(f.call(f.frames[0]));
});

test('the suite uses its own observation prefix: a metadata-suite kind is refused before transport', async () => {
    const f = fixture();
    await assert.rejects(f.observer.handle({ kind: 'metadata-start', barrier: null }, () => 1000));
    assert.equal(f.reads.length, 0);
});

test('unbound, duplicate or reordered requests refuse before transport', async () => {
    const f = fixture(); await assert.rejects(f.call(f.frames[2])); assert.equal(f.reads.length, 0);
    await f.call(f.frames[0]); f.set(f.frames[1]); await f.call(f.frames[1]); const count = f.reads.length;
    await assert.rejects(f.call(f.frames[1]), 'a second bind');
    await assert.rejects(f.call(f.frames[0]), 'a second start');
    await assert.rejects(f.call(f.frames.at(-1)), 'a finish before every planned read');
    assert.equal(f.reads.length, count);
});

test('a read request that names a stage of the frozen metadata plan is refused: this plan has none', async () => {
    const f = fixture(); await f.call(f.frames[0]); f.set(f.frames[1]); await f.call(f.frames[1]); f.set(f.frames[2]);
    await assert.rejects(f.observer.handle({ kind: 'shi-read', barrier: f.frames[2].barrier, caseId: 'metadata.title.osc2', stageId: 'control' }, () => 1000));
    assert.equal(f.observer.frames.length, 2);
});

test('a wrong nonce, a failed prepared baseline and a missing runtime color refuse fixture binding', async () => {
    for (const mutate of [frame => { frame.before.screen.lines[9] = 'wrong'; }, frame => { frame.before.metadata.oscTitle = 'unprepared'; frame.before.metadata.displayTitle = 'unprepared'; },
        frame => { frame.before.screen.colorBinding = { ...frame.before.screen.colorBinding, paletteOverrideIndices: [] }; }]) {
        const f = fixture(); await f.call(f.frames[0]); const frame = structuredClone(f.frames[1]); mutate(frame); f.set(frame);
        await assert.rejects(f.call(frame)); assert.equal(f.observer.frames.length, 1);
        assert.equal(f.observer.lastAttempt.operation, 'bind');
    }
});

test('an incomplete restoration is refused at the finish observation', async () => {
    const f = fixture(); for (const frame of f.frames.slice(0, -1)) { f.set(frame); await f.call(frame); }
    const last = structuredClone(f.frames.at(-1)); last.before.metadata.buffer.acceptingCommandInput = true; f.set(last);
    await assert.rejects(f.call(last)); assert.equal(f.observer.restored, false);
});

test('a plan beyond the 64-capture ceiling and a grid below the fixture minimum are refused at start', async () => {
    const f = fixture(undefined, { planOf: () => Array.from({ length: 62 }, () => ({})) });
    await assert.rejects(f.call(f.frames[0]));
    const small = fixture((method, value) => { if (method === 'readScreen') { value.rows = 11; value.lines = value.lines.slice(0, 11); } });
    await assert.rejects(small.call(small.frames[0]));
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
    assert.equal(f.observer.frames.length, 0); assert.equal(f.observer.restored, false);
});

test('only an initial preflight refusal under this suite\'s own prefix is an optional unavailable marker', () => {
    for (const code of ['paneMetadataUnavailable', 'screenDetailsUnavailable', 'metadataGeometryUnavailable']) {
        const observer = { frames: [], prefix: osc133Prefix };
        assert.equal(initialMetadataRefusal({ kind: 'shi-start' }, Object.assign(new Error('preflight'), { code }), observer), true, code);
        assert.equal(initialMetadataRefusal({ kind: 'metadata-start' }, Object.assign(new Error('preflight'), { code }), observer), false, 'another suite\'s kind never qualifies');
        assert.equal(initialMetadataRefusal({ kind: 'shi-read' }, Object.assign(new Error('preflight'), { code }), observer), false);
        assert.equal(initialMetadataRefusal({ kind: 'shi-start' }, Object.assign(new Error('preflight'), { code }), { frames: [{}], prefix: osc133Prefix }), false);
    }
    assert.equal(initialMetadataRefusal({ kind: 'metadata-start' }, Object.assign(new Error('preflight'), { code: 'paneMetadataUnavailable' }), { frames: [] }), true, 'the frozen suite keeps its default prefix');
    const partial = metadataFailure({ kind: 'shi-read' }, Object.assign(new Error('x'), { code: 'paneMetadataUnavailable' }), { frames: [{}], prefix: osc133Prefix });
    assert.equal(partial.code, undefined, 'a partial failure never keeps a code a worker treats as a clean skip');
});
