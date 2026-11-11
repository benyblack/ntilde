import test from 'node:test';
import assert from 'node:assert/strict';
import { OwnedMetadataObserver, initialMetadataRefusal, metadataFailure } from './metadata-capture.mjs';
import { copyPaneMetadata } from './metadata-state.mjs';
import { exposeTitleStackFacts } from './title-stack-collector.mjs';
import { syntheticTitleStackEvidence, titleStackPaneId } from './fixtures/title-stack-evidence.mjs';
import { matchesTitleStackBaseline, titleStackObserverPlan } from './title-stack-grading.mjs';
import { titleStackPrefix } from './title-stack-contracts.mjs';

const runId = 'a'.repeat(32);
/** The metadata observer wired exactly as the runner wires the title-stack suite, over a fake endpoint serving the synthetic frames. */
function fixture(inject = () => {}, overrides = {}) {
    const { frames } = syntheticTitleStackEvidence(), reads = []; let frame = frames[0];
    const observer = new OwnedMetadataObserver({ runId, profileName: 'fixture', expectedPaneId: titleStackPaneId, prefix: titleStackPrefix, includeResolvedColors: true,
        planOf: titleStackObserverPlan, matchesBaseline: matchesTitleStackBaseline, expose: exposeTitleStackFacts, monotonicHistory: true, endpoint: () => 'private', ...overrides,
        rpc: async (_, method) => {
            reads.push(method); const value = structuredClone(frame.before);
            inject(method, value, reads);
            if (method === 'listSessions') return { sessions: [{ paneId: titleStackPaneId, kind: 'local', profileName: 'fixture', rows: value.screen.rows, cols: value.screen.cols }] };
            if (method === 'readScreen') return value.screen;
            if (method === 'readScrollback') return value.screen.history;
            if (method === 'getSessionStatus') return value.rawStatus;
            assert.equal(method, 'readPaneMetadata');
            return value.metadata;
        } });
    return { observer, frames, reads, set: value => { frame = value; }, call: value => observer.handle({ kind: value.kind, barrier: value.barrier }, () => 1000) };
}

test('all 61 observations (start, bind, 58 reads, finish) require two complete viewport, host-control, raw-status and resolved-color reads, and each read exposes its raw title and icon', async () => {
    const f = fixture(); const exposed = [];
    for (const frame of f.frames) { f.set(frame); exposed.push(await f.call(frame)); }
    assert.equal(f.frames.length, 61);
    assert.equal(f.observer.restored, true); assert.deepEqual(f.observer.frames, f.frames);
    for (const method of ['readPaneMetadata', 'getSessionStatus', 'readScrollback']) assert.equal(f.reads.filter(value => value === method).length, 122, method);
    for (const [index, frame] of f.observer.frames.entries()) {
        assert.ok(frame.before.screen.colorBinding && frame.before.screen.resolvedColors, 'every capture keeps its resolved color binding');
        assert.deepEqual(exposed[index].facts, { title: frame.before.metadata.oscTitle, icon: frame.before.metadata.buffer.requestedIconName ?? null });
    }
    await assert.rejects(f.call(f.frames[0]));
});

test('the raw icon name is retained from the wire when the App reports it, and a frame without it stays valid for the frozen suites', () => {
    const wire = { paneId: titleStackPaneId, displayTitle: 'x', buffer: { rows: 12, cols: 48, cursorRow: 0, cursorCol: 0, alternateScreen: false, scrollbackLines: 0, historyGeneration: 1,
        rowsEvicted: 0, acceptingCommandInput: false, links: [], requestedIconName: 'icon' },
    presentation: { windowActive: false, windowVisible: true, paneActive: true, focusMode: false, notificationsEnabled: true, progressEnabled: true, tabText: 'x', tabTextVisible: true, toastVisible: false, toastTitle: '', toastMessage: '' } };
    assert.equal(copyPaneMetadata(wire).buffer.requestedIconName, 'icon');
    const { requestedIconName, ...rest } = wire.buffer;
    assert.equal(Object.hasOwn(copyPaneMetadata({ ...wire, buffer: rest }).buffer, 'requestedIconName'), false);
    assert.equal(Object.hasOwn(copyPaneMetadata({ ...wire, buffer: { ...rest, requestedIconName: null } }).buffer, 'requestedIconName'), false);
    assert.throws(() => copyPaneMetadata({ ...wire, buffer: { ...rest, requestedIconName: 'x'.repeat(5000) } }));
    assert.throws(() => copyPaneMetadata({ ...wire, buffer: { ...rest, requestedIconName: 7 } }));
});

test('RIS may raise the history generation for this suite only; a lower generation, or any change in the frozen suites\' default mode, is refused', async () => {
    const accepting = fixture(); for (const frame of accepting.frames.slice(0, 4)) { accepting.set(frame); await accepting.call(frame); }
    const bumped = structuredClone(accepting.frames[4]);
    bumped.before.metadata.buffer.historyGeneration += 1; bumped.after.metadata.buffer.historyGeneration += 1;
    accepting.set(bumped); await accepting.call(bumped);
    assert.equal(accepting.observer.frames.length, 5, 'the raised generation was accepted');
    const frozen = fixture(undefined, { monotonicHistory: false }); for (const frame of frozen.frames.slice(0, 4)) { frozen.set(frame); await frozen.call(frame); }
    frozen.set(bumped); await assert.rejects(frozen.call(bumped), 'the default observer demands the first generation');
    const falls = fixture(); for (const frame of falls.frames.slice(0, 4)) { falls.set(frame); await falls.call(frame); }
    const rose = structuredClone(falls.frames[4]); rose.before.metadata.buffer.historyGeneration += 1; rose.after.metadata.buffer.historyGeneration += 1;
    falls.set(rose); await falls.call(rose);
    const back = structuredClone(falls.frames[5]); falls.set(back);
    await assert.rejects(falls.call(back), 'a generation that rose after RIS and then fell back is refused between consecutive captures');
    const lower = fixture(); for (const frame of lower.frames.slice(0, 4)) { lower.set(frame); await lower.call(frame); }
    const down = structuredClone(lower.frames[4]); down.before.metadata.buffer.historyGeneration -= 1; down.after.metadata.buffer.historyGeneration -= 1;
    lower.set(down); await assert.rejects(lower.call(down), 'a generation below the first is refused even in monotonic mode');
});

test('the suite uses its own observation prefix: another suite\'s kind is refused before transport', async () => {
    const f = fixture();
    await assert.rejects(f.observer.handle({ kind: 'metadata-start', barrier: null }, () => 1000));
    await assert.rejects(f.observer.handle({ kind: 'shi-start', barrier: null }, () => 1000));
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

test('a wrong nonce, a failed prepared title or icon baseline and a missing runtime color refuse fixture binding', async () => {
    for (const mutate of [frame => { frame.before.screen.lines[9] = 'wrong'; }, frame => { frame.before.metadata.oscTitle = 'unprepared'; frame.before.metadata.displayTitle = 'unprepared'; },
        frame => { frame.before.metadata.buffer.requestedIconName = 'unprepared'; },
        frame => { frame.before.screen.colorBinding = { ...frame.before.screen.colorBinding, paletteOverrideIndices: [] }; }]) {
        const f = fixture(); await f.call(f.frames[0]); const frame = structuredClone(f.frames[1]); mutate(frame); f.set(frame);
        await assert.rejects(f.call(frame)); assert.equal(f.observer.frames.length, 1);
        assert.equal(f.observer.lastAttempt.operation, 'bind');
    }
});

test('an incomplete restoration (a residual title, icon or screen) is refused at the finish observation', async () => {
    for (const mutate of [last => { last.before.metadata.oscTitle = 'left-over'; }, last => { last.before.metadata.buffer.requestedIconName = 'left-over'; }, last => { last.before.screen.lines[3] = 'residue'; }]) {
        const f = fixture(); for (const frame of f.frames.slice(0, -1)) { f.set(frame); await f.call(frame); }
        const last = structuredClone(f.frames.at(-1)); mutate(last); f.set(last);
        await assert.rejects(f.call(last)); assert.equal(f.observer.restored, false);
    }
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
        const observer = { frames: [], prefix: titleStackPrefix };
        assert.equal(initialMetadataRefusal({ kind: 'tst-start' }, Object.assign(new Error('preflight'), { code }), observer), true, code);
        assert.equal(initialMetadataRefusal({ kind: 'shi-start' }, Object.assign(new Error('preflight'), { code }), observer), false, 'another suite\'s kind never qualifies');
        assert.equal(initialMetadataRefusal({ kind: 'tst-read' }, Object.assign(new Error('preflight'), { code }), observer), false);
        assert.equal(initialMetadataRefusal({ kind: 'tst-start' }, Object.assign(new Error('preflight'), { code }), { frames: [{}], prefix: titleStackPrefix }), false);
    }
    const partial = metadataFailure({ kind: 'tst-read' }, Object.assign(new Error('x'), { code: 'paneMetadataUnavailable' }), { frames: [{}], prefix: titleStackPrefix });
    assert.equal(partial.code, undefined, 'a partial failure never keeps a code a worker treats as a clean skip');
});
