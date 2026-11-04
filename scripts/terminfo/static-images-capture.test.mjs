import test from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { mkdtempSync, rmSync, readFileSync, existsSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { OwnedStaticImageObserver } from './static-images-capture.mjs';
import { collectStaticImages } from './static-images-collector.mjs';
import { imagePlan, imageBoundScreen, imageModeQueries as inputModeQueries, imageModeReplyHex, imageBackgroundReply } from './static-images.mjs';
import { staticImageCases } from './static-images-cases.mjs';
import { initialInputScreen, inputRunId, inputPaneId } from './fixtures/input-journal.mjs';
import { imageOwnership, syntheticImageCapture, syntheticImageReply } from './fixtures/static-images-journal.mjs';
class Input extends EventEmitter {
    isRaw = true; paused = false;
    isPaused() { return this.paused; } setRawMode(value) { this.isRaw = value; }
    pause() { this.paused = true; } resume() { this.paused = false; }
}
async function fixture(action, { late = false, changedPair = false, changedWorker = false, badModes = false, badReply = false } = {}) {
    const directory = mkdtempSync(join(tmpdir(), 'nt-image-capture-')), input = new Input(), calls = [], writes = [];
    let screen = initialInputScreen(10, 60), allocated = 99, reads = 0;
    const baseline = structuredClone(screen), bound = imageBoundScreen(baseline, inputRunId), ownership = imageOwnership();
    const observer = new OwnedStaticImageObserver({ output: directory, nonce: 'NTILDE_IMG_' + inputRunId,
        profileName: 'fixture', expectedPaneId: inputPaneId, priorChallenges: ['32768,32769'], endpoint: () => 'private',
        rpc: async (_, method) => {
            if (method === 'listSessions') return { sessions: [{ paneId: inputPaneId, kind: 'local', profileName: 'fixture', rows: screen.rows, cols: screen.cols }] };
            if (method === 'readScrollback') return structuredClone(screen.history);
            return structuredClone(screen);
        }, captureRpc: async () => {
            const entry = imagePlan[observer.next], spec = staticImageCases.find(value => value.id === entry?.caseId);
            const stage = observer.phase === 'capture' ? spec?.stages.find(value => value.id === entry?.stageId) : null;
            const value = syntheticImageCapture(screen, spec, stage);
            if (changedPair && ++reads === 4) value.renderDetails.font.primarySha256 = 'b'.repeat(64);
            return value;
        } });
    const output = { write(value, callback) {
        const text = Buffer.isBuffer(value) ? value.toString('ascii') : value; writes.push(text); callback();
        const challenge = /^\x1b\[\?(\d+)\$p\x1b\[\?(\d+)\$p$/.exec(text);
        if (challenge) queueMicrotask(() => input.emit('data', Buffer.from(challenge.slice(1).map(number => `\x1b[?${number};0$y`).join(''))));
        else if (text.endsWith(inputModeQueries)) {
            const entry = imagePlan[observer.next], spec = staticImageCases.find(value => value.id === entry.caseId);
            const stage = spec?.stages.find(value => value.id === entry.stageId), state = observer.states.get(entry.caseId);
            let reply = stage && !observer.actions.at(-1).skipReason ? syntheticImageReply(spec, stage, state, allocated) : '';
            if (stage?.reply === 'allocate') allocated++;
            if (badReply && reply) reply = 'unframed';
            screen = structuredClone(entry.kind === 'finish' ? baseline : bound);
            const background = imageBackgroundReply(entry.kind === 'finish' ? 0xff000000 : 0xff101010);
            queueMicrotask(() => input.emit('data', Buffer.concat([Buffer.from(reply + background), Buffer.from(badModes ? '' : imageModeReplyHex, 'hex')])));
        }
    } };
    const owner = async (kind, _, extra = {}) => {
        calls.push({ kind, extra });
        const fact = structuredClone(ownership); if (changedWorker && kind === 'images-admit') fact.worker.pid++;
        const result = await observer.handle({ kind, ...extra }, () => 1000, fact);
        if (late && kind === 'images-finish') input.emit('data', Buffer.from('late'));
        return result;
    };
    try { await action({ config: { output: directory, runId: inputRunId }, directory, input, output, owner, observer, calls, writes }); }
    finally { rmSync(directory, { recursive: true, force: true }); }
}
test('real collector/observer orchestration uses30 admitted bounded emits,31 paired state/PNG captures and a disposed raw seal', async () => fixture(async f => {
    await collectStaticImages(f);
    assert.equal(f.observer.restored, true); assert.equal(f.observer.frames.length, 31); assert.equal(f.observer.actions.length, 30);
    assert.equal(f.observer.before.snapshots, 31); assert.equal(f.observer.after.snapshots, 31);
    assert.ok(f.observer.report.observations.every(value => value.result === 'passed'));
    assert.equal(f.input.listenerCount('data'), 0); assert.equal(f.calls.at(-1).kind, 'images-seal');
    assert.equal(f.calls.filter(value => value.kind === 'images-admit').length, 30);
    assert.equal(f.observer.seal.attempts, 30); assert.equal(f.observer.used.size, 31);
    assert.equal(f.writes.filter(value => value.endsWith(inputModeQueries)).length, 30);
    assert.ok(f.observer.report.snapshots.slice(1).every(frame => frame.capture.nonces.join(',') !== '32768,32769'));
}));
for (const [name, options] of [['changing paired render context', { changedPair: true }], ['changed worker', { changedWorker: true }],
    ['missing mode controls', { badModes: true }], ['malformed reply', { badReply: true }], ['late raw bytes', { late: true }]])
    test(name + ' retains partial facts and cannot complete or retry', async () => fixture(async f => {
        await assert.rejects(collectStaticImages(f)); assert.equal(f.observer.restored, false);
        assert.ok(existsSync(join(f.directory, 'image-raw-failure.json')));
        const failure = JSON.parse(readFileSync(join(f.directory, 'image-raw-failure.json')));
        assert.ok(failure.error); assert.equal(f.input.listenerCount('data'), 0);
        assert.equal(f.calls.filter(value => value.kind === 'images-start').length, 1);
    }, options));
test('unplanned caller fields and wrong phase cannot admit even one image command', async () => fixture(async f => {
    await assert.rejects(f.owner('images-admit')); assert.equal(f.observer.actions.length, 0);
    await f.owner('images-start');
    await assert.rejects(f.owner('images-admit', null, { commandHex: '00' })); assert.equal(f.observer.actions.length, 0);
}));
test('only typed initial refusal can skip without installing a raw reader or emitting fixture bytes', async () => {
    const input = new Input(); let writes = 0;
    const result = await collectStaticImages({ config: {}, input, output: { write() { writes++; } },
        owner: async kind => { assert.equal(kind, 'images-start'); throw Object.assign(new Error('no details'), { code: 'screenDetailsUnavailable' }); } });
    assert.deepEqual(result, { status: 'unavailable', reason: 'screenDetailsUnavailable' });
    assert.equal(input.listenerCount('data'), 0); assert.equal(writes, 0);
});
