import test from 'node:test';
import assert from 'node:assert/strict';
import { privateClipboardFixture } from './fixtures/private-clipboard-owned.mjs';
import { buildPrivateClipboardReport, privateClipboardData } from './private-clipboard.mjs';

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
