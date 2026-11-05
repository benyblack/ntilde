import test from 'node:test';
import assert from 'node:assert/strict';
import { privateClipboardFixture } from './fixtures/private-clipboard-owned.mjs';
import { buildPrivateClipboardReport, privateClipboardData, privateClipboardEvidenceData, privateClipboardEvidenceJson } from './private-clipboard.mjs';
import { queryScreen } from './queries.mjs';
import { privateClipboardScreen } from './private-clipboard-capture.mjs';

test('private clipboard observes wide lead and continuation columns from full cell details', async () => {
    const base = await privateClipboardFixture(), screen = structuredClone(base.screen);
    delete screen.attributeRuns;
    screen.lines[0] = '界';
    const detail = screen.cellDetails[0], cells = screen.rows * screen.cols;
    screen.cellDetails = [{ ...detail, count: 1, flags: detail.flags | 2048 },
        { ...detail, count: 1, flags: detail.flags | 4096 }, { ...detail, count: cells - 2 }];
    const captured = privateClipboardScreen(screen);
    assert.deepEqual(captured.attributeRuns, screen.cellDetails.map(({ count, flags, fg, bg }) => ({ count, flags, fg, bg })));
    const api = await privateClipboardFixture({ initialScreen: captured });
    const report = await api.run();
    assert.equal(report.frames.length, 22);
    assert.deepEqual(report.frames[21].after.screen, captured);
    assert.deepEqual(report.callbackResults.map(value => value.result.pass), [false, true, false]);
    const incomplete = structuredClone(screen); incomplete.cellDetails.at(-1).count--;
    assert.throws(() => privateClipboardScreen(incomplete));
    assert.throws(() => privateClipboardScreen(captured));
});

test('private clipboard coalesces identical attribute projections without losing differing detail fields', async () => {
    const base = await privateClipboardFixture(), screen = structuredClone(base.screen);
    delete screen.attributeRuns;
    const detail = screen.cellDetails[0], cells = screen.rows * screen.cols;
    screen.cellDetails = [{ ...detail, count: 1, overline: !detail.overline }, { ...detail, count: cells - 1 }];
    const captured = privateClipboardScreen(screen);
    assert.equal(captured.attributeRuns.length, 1);
    assert.equal(captured.attributeRuns[0].count, cells);
    assert.deepEqual(captured.cellDetails, screen.cellDetails);
});

test('44 valid maximum-detail screens complete and retain the independent full journal', async () => {
    const base = await privateClipboardFixture(), screen = structuredClone(base.screen), cells = screen.rows * screen.cols;
    screen.cellDetails = Array.from({ length: 1024 }, (_, index) => ({ ...screen.cellDetails[0],
        count: index === 1023 ? cells - 1023 : 1, flags: 512 | 16384, fg: index % 2, foregroundArgb: null }));
    screen.attributeRuns = screen.cellDetails.map(({ count, flags, fg, bg }) => ({ count, flags, fg, bg }));
    assert.deepEqual(queryScreen(screen), screen, 'the dense source screen must satisfy the existing App-state contract');
    const api = await privateClipboardFixture({ initialScreen: screen }), report = await api.run();
    assert.equal(report.frames.length, 22); assert.equal(api.observer.journal.frames.length, 22);
    assert.equal(api.observer.report.frames[21].after.screen.cellDetails.length, 1024);
    assert.equal(api.observer.journal.frames[0].before.screen.cellDetails.length, 1024);
    const retained = privateClipboardEvidenceJson(report);
    assert.ok(Buffer.byteLength(retained) <= 16 * 1024 * 1024);
    assert.deepEqual(JSON.parse(retained), report);
    assert.equal(privateClipboardEvidenceJson(report), retained);
});

test('evidence admission keeps exact frame, plain-data and actual serialized-byte bounds', () => {
    assert.throws(() => privateClipboardEvidenceData({ frames: Array(23).fill({}) }));
    assert.throws(() => privateClipboardEvidenceData({ frames: {}, note: 'not an array' }));
    assert.throws(() => privateClipboardEvidenceData({ frames: [], note: '\\'.repeat(9 * 1024 * 1024) }));
    assert.throws(() => privateClipboardEvidenceData({ frames: [], metadata: Array(32768).fill({ a: 1, b: 2, c: 3, d: 4, e: 5, f: 6, g: 7, h: 8 }) }));
    let read = false;
    assert.throws(() => privateClipboardEvidenceData({ frames: [{ before: { get screen() { read = true; return {}; } } }] }));
    assert.equal(read, false);
});

test('actual pinned callbacks use22 App RPCs and44 independent paired state captures, preserve original grades and final close', async () => {
    const api = await privateClipboardFixture(), report = await api.run();
    assert.equal(api.requests.length, 22); assert.equal(api.captures, 44); assert.equal(api.attestations, 88);
    assert.equal(report.frames.length, 22); assert.equal(report.controller.complete, true);
    assert.deepEqual(api.callbackResults.map(value => value.pass), [false, true, false]);
    assert.deepEqual(report.observations.map(value => value.originalPass), [false, true, false]);
    assert.equal(report.controller.events.at(-1).request.operation, 'close');
    assert.equal(report.controller.events.at(-1).result.acceptedWrites, 2);
    assert.ok(report.observations.every(value => value.nativeInterchangeQualified === false));
    assert.equal(api.packets.length, 2); assert.deepEqual(api.queryTraces, ['\x1b]52;c;?\x07', '\x1b]52;c;?\x07']);
    const rebuilt = buildPrivateClipboardReport({ binding: report.binding, controller: report.controller, frames: report.frames, callbackResults: report.callbackResults });
    assert.deepEqual(rebuilt, report);
    assert.deepEqual(report.unobservedState, ['reverseVideo']);
});

for (const field of ['worker', 'birth', 'device', 'source', 'binary', 'flag']) test('independent ' + field + ' drift refuses the fixture before an App action', async () => {
    const api = await privateClipboardFixture();
    api.onAttest = fact => {
        if (field === 'worker') fact.worker.ppid++;
        if (field === 'birth') fact.worker.birth = 'Thu Nov  5 09:00:03 2026';
        if (field === 'device') fact.outputDevice.ino = '99';
        if (field === 'source') fact.sourceSnapshotManifestSha256 = 'e'.repeat(64);
        if (field === 'binary') fact.executableSha256 = 'e'.repeat(64);
        if (field === 'flag') fact.appCommand = fact.appCommand.replace('--ntilde-terminfo-private-clipboard=', '--other=');
    };
    await assert.rejects(api.run());
    assert.equal(api.requests.length, 0); assert.equal(api.observer.complete, false);
});

test('independent state mutation is retained but never qualified or retried', async () => {
    const api = await privateClipboardFixture();
    api.onCapture = () => { if (api.captures === 2) api.screen.cursorCol++; };
    await assert.rejects(api.run());
    assert.equal(api.observer.complete, false); assert.equal(api.requests.length, 1);
    assert.equal(api.observer.journal.frames[0].before.screen.cursorCol, 0);
});

test('invalid callback result is not retained, cleanup is attempted and final completion remains fatal', async () => {
    const api = await privateClipboardFixture();
    const original = api.worker.withClipboardFixture;
    // Send a forged completion directly after the real first callback's arm/read/seed boundaries.
    await api.observer.handle({ kind: 'start', probeId: 'extensions.osc52-clipboard' }, () => 4000);
    await assert.rejects(api.observer.handle({ kind: 'complete', callbackResult: { pass: true, foreign: 'unowned clipboard data' } }, () => 4000));
    assert.equal(api.observer.journal.callbackResults.length, 0);
    assert.equal(JSON.stringify(api.observer.journal).includes('unowned clipboard data'), false);
    assert.equal(api.observer.complete, false); assert.equal(typeof original, 'function');
});

test('plain-data admission rejects accessors without reading or retaining their values', () => {
    let read = false;
    const value = { get clipboard() { read = true; return 'personal data'; } };
    assert.throws(() => privateClipboardData(value)); assert.equal(read, false);
    assert.throws(() => privateClipboardData(new Date()));
    assert.throws(() => privateClipboardData({ foreign: Buffer.from('foreign') }));
});

for (const operation of ['begin', 'armWrite', 'seed', 'read', 'restore', 'close']) test('actual parent path cannot complete after App ' + operation + ' refusal', async () => {
    const api = await privateClipboardFixture(); let refused = false;
    api.onRpc = request => { if (!refused && request.operation === operation) { refused = true; throw new Error('Synthetic App refusal'); } };
    await assert.rejects(api.run()); assert.equal(refused, true); assert.equal(api.observer.complete, false);
    assert.throws(() => api.observer.report);
    await assert.rejects(api.observer.handle({ kind: 'finish' }, () => 4000));
});
test('foreign App read values are refused before parent journal retention', async () => {
    const api = await privateClipboardFixture();
    api.changeResult = (result, request) => request.operation === 'read' ? { ...result, value: 'PERSONAL VALUE MUST NOT BE RETAINED' } : result;
    await assert.rejects(api.run()); assert.equal(api.observer.complete, false);
    assert.equal(JSON.stringify(api.observer.journal).includes('PERSONAL VALUE'), false);
});
test('overlapping owner frames poison the active path without replacing its budget or starting another callback', async () => {
    const api = await privateClipboardFixture(); let release, entered;
    const ready = new Promise(resolve => { entered = resolve; }), gate = new Promise(resolve => { release = resolve; });
    api.onRpc = async request => { if (request.operation === 'begin') { entered(); await gate; } };
    const first = api.observer.handle({ kind: 'start', probeId: 'extensions.osc52-clipboard' }, () => 4000);
    const firstFailure = assert.rejects(first); await ready;
    const overlap = api.observer.handle({ kind: 'read' }, () => { throw Error('replacement budget must not run'); });
    const overlapFailure = assert.rejects(overlap, /Overlapping/);
    release(); await Promise.all([firstFailure, overlapFailure]);
    assert.equal(api.observer.complete, false); assert.equal(api.requests.filter(request => request.operation === 'begin').length, 1);
});
