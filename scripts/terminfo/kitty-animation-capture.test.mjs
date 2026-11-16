import test from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { mkdtempSync, rmSync, readFileSync, existsSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { OwnedAnimationObserver } from './kitty-animation-capture.mjs';
import { collectAnimation, animationWait } from './kitty-animation-collector.mjs';
import { animationPlan, animationBoundScreen } from './kitty-animation.mjs';
import { animationCases, animationExpectedReplies, animationHoldMs } from './kitty-animation-cases.mjs';
import { imageModeQueries, imageModeReplyHex, imageBackgroundReply } from './static-images.mjs';
import { initialInputScreen, inputRunId, inputPaneId } from './fixtures/input-journal.mjs';
import { imageOwnership } from './fixtures/static-images-journal.mjs';
import { syntheticAnimationCapture, animationEpoch } from './fixtures/kitty-animation-journal.mjs';

class Input extends EventEmitter {
    isRaw = true; paused = false;
    isPaused() { return this.paused; } setRawMode(value) { this.isRaw = value; }
    pause() { this.paused = true; } resume() { this.paused = false; }
}
/** A virtual wall clock: writes, captures and the collector's sleeps advance it, so waits are exercised without real delay. */
async function fixture(action, { badModes = false, badReply = false, changedWorker = false, late = false, captureCost = 450, mutateCapture } = {}) {
    const directory = mkdtempSync(join(tmpdir(), 'nt-kan-capture-')), input = new Input(), calls = [], writes = [];
    let clock = animationEpoch, screen = initialInputScreen(10, 60);
    const baseline = structuredClone(screen), bound = animationBoundScreen(baseline, inputRunId), now = () => clock;
    const observer = new OwnedAnimationObserver({ output: directory, nonce: 'NTILDE_IMG_' + inputRunId, profileName: 'fixture', expectedPaneId: inputPaneId,
        priorChallenges: ['32768,32769'], endpoint: () => 'private', now,
        rpc: async (_, method) => {
            if (method === 'listSessions') return { sessions: [{ paneId: inputPaneId, kind: 'local', profileName: 'fixture', rows: screen.rows, cols: screen.cols }] };
            if (method === 'readScrollback') return structuredClone(screen.history);
            return structuredClone(screen);
        }, captureRpc: async () => {
            const entry = animationPlan[observer.next], spec = animationCases.find(value => value.id === entry?.caseId);
            const stage = observer.phase === 'capture' ? spec?.stages.find(value => value.id === entry?.stageId) : null;
            clock += captureCost;
            const value = syntheticAnimationCapture(screen, stage?.appearance ?? 'absent');
            mutateCapture?.(value);
            return value;
        } });
    const output = { write(value, callback) {
        const text = Buffer.isBuffer(value) ? value.toString('ascii') : value; writes.push(text); clock += 30; callback();
        const challenge = /^\x1b\[\?(\d+)\$p\x1b\[\?(\d+)\$p$/.exec(text);
        if (challenge) queueMicrotask(() => input.emit('data', Buffer.from(challenge.slice(1).map(number => `\x1b[?${number};0$y`).join(''))));
        else if (text.endsWith(imageModeQueries)) {
            const entry = animationPlan[observer.next], spec = animationCases.find(value => value.id === entry.caseId);
            const stage = spec?.stages.find(value => value.id === entry.stageId), state = observer.states.get(entry.caseId);
            let reply = stage ? animationExpectedReplies(stage.id, state.imageId).join('') : '';
            if (badReply && reply) reply = 'unframed';
            screen = structuredClone(entry.kind === 'finish' ? baseline : bound);
            const background = imageBackgroundReply(entry.kind === 'finish' ? 0xff000000 : 0xff101010);
            queueMicrotask(() => input.emit('data', Buffer.concat([Buffer.from(reply + background), Buffer.from(badModes ? '' : imageModeReplyHex, 'hex')])));
        }
    } };
    const owner = async (kind, _, extra = {}) => {
        calls.push({ kind, extra }); clock += 5;
        const fact = { ...structuredClone(imageOwnership()), at: new Date(clock).toISOString() };
        if (changedWorker && kind === 'kan-admit') fact.worker.pid++;
        const result = await observer.handle({ kind, ...extra }, () => 1000, fact);
        if (late && kind === 'kan-finish') input.emit('data', Buffer.from('late'));
        return result;
    };
    const sleep = async ms => { clock += ms; };
    try { await action({ config: { output: directory, runId: inputRunId }, directory, input, output, owner, observer, calls, writes, sleep, now, clockOf: () => clock }); }
    finally { rmSync(directory, { recursive: true, force: true }); }
}

test('collector and observer run 23 admitted emits with timed waits that put every read in its planned window', async () => fixture(async f => {
    const result = await collectAnimation(f);
    assert.deepEqual(result, { status: 'complete' });
    assert.equal(f.observer.restored, true); assert.equal(f.observer.frames.length, 24); assert.equal(f.observer.actions.length, 23);
    assert.equal(f.observer.before.snapshots, 24); assert.equal(f.observer.after.snapshots, 24);
    assert.equal(f.input.listenerCount('data'), 0); assert.equal(f.calls.at(-1).kind, 'kan-seal');
    assert.equal(f.calls.filter(value => value.kind === 'kan-admit').length, 23);
    assert.equal(f.writes.filter(value => value.endsWith(imageModeQueries)).length, 23);
    const report = f.observer.report;
    assert.ok(report.observations.every(value => value.result === 'passed'), JSON.stringify(report.observations.filter(value => value.result !== 'passed').map(value => [value.featureId, value.inconclusiveReason])));
    assert.ok(report.snapshots.slice(1).every(frame => frame.capture.nonces.join(',') !== '32768,32769'));
    assert.ok(report.observations.filter(value => value.timing.clock === 'running').every(value => value.timing.valid));
    const second = report.observations.find(value => value.featureId === 'kitty.animation.playback.second');
    const start = report.observations.find(value => value.featureId === 'kitty.animation.playback.start');
    assert.ok(second.timing.facts.capture.start - start.timing.facts.start.latest >= 5000 && second.timing.facts.capture.end - start.timing.facts.start.earliest < 10000);
}));
test('a slower capture path still lands inside the windows because waits adapt to the measured read duration', async () => fixture(async f => {
    await collectAnimation(f);
    assert.ok(f.observer.report.observations.every(value => value.result === 'passed'));
}, { captureCost: 700 }));
test('a capture path too slow for the 5 s windows yields inconclusive timing, never failures', async () => fixture(async f => {
    await collectAnimation(f);
    const states = new Set(f.observer.report.observations.map(value => value.result));
    assert.ok(!states.has('failed')); assert.ok(states.has('inconclusive'));
}, { captureCost: 2600 }));

test('the wait planner holds out the gap, aims running reads at their targets and never waits for an unknown start', () => {
    const hold = animationCases[0].stages.find(stage => stage.id === 'hold'), second = animationCases[1].stages.find(stage => stage.id === 'second');
    assert.equal(animationWait(hold, { now: 1000, previousEnd: 900, startAdmitted: null, lastReadMs: 2000 }), 900 + animationHoldMs + 1000 - 1000);
    assert.equal(animationWait(hold, { now: 9000, previousEnd: 900, startAdmitted: null, lastReadMs: 2000 }), 0);
    assert.equal(animationWait(second, { now: 1000, previousEnd: 900, startAdmitted: 500, lastReadMs: 2000 }), 500 + 7500 - 1200 - 1000);
    assert.equal(animationWait(second, { now: 99999, previousEnd: 900, startAdmitted: 500, lastReadMs: 2000 }), 0);
    assert.equal(animationWait(second, { now: 1000, previousEnd: 900, startAdmitted: null, lastReadMs: 2000 }), 0);
    assert.equal(animationWait(null, { now: 1, previousEnd: 1, startAdmitted: 1, lastReadMs: 1 }), 0);
    assert.equal(animationWait(second, { now: 0, previousEnd: 0, startAdmitted: 10_000_000, lastReadMs: 0 }), 30000, 'bounded');
});

for (const [name, options] of [['missing mode controls', { badModes: true }], ['malformed reply', { badReply: true }], ['changed worker', { changedWorker: true }], ['late raw bytes', { late: true }]])
    test(name + ' retains partial facts and cannot complete or retry', async () => fixture(async f => {
        await assert.rejects(collectAnimation(f)); assert.equal(f.observer.restored, false);
        assert.ok(existsSync(join(f.directory, 'animation-raw-failure.json')));
        assert.ok(JSON.parse(readFileSync(join(f.directory, 'animation-raw-failure.json'))).error);
        assert.equal(f.input.listenerCount('data'), 0); assert.equal(f.calls.filter(value => value.kind === 'kan-start').length, 1);
    }, options));

test('unplanned caller fields and wrong phase cannot admit even one animation command', async () => fixture(async f => {
    await assert.rejects(f.owner('kan-admit')); assert.equal(f.observer.actions.length, 0);
    await f.owner('kan-start');
    await assert.rejects(f.owner('kan-admit', null, { commandHex: '00' })); assert.equal(f.observer.actions.length, 0);
}));
test('only typed initial refusal can skip without installing a raw reader or emitting fixture bytes', async () => {
    const input = new Input(); let writes = 0;
    const result = await collectAnimation({ config: {}, input, output: { write() { writes++; } },
        owner: async kind => { assert.equal(kind, 'kan-start'); throw Object.assign(new Error('no details'), { code: 'imageGeometryUnavailable' }); } });
    assert.deepEqual(result, { status: 'unavailable', reason: 'imageGeometryUnavailable' });
    assert.equal(input.listenerCount('data'), 0); assert.equal(writes, 0);
});
for (const [name, mutate] of [['missing requested envelope', value => delete value.imageDetails],
    ['incomplete envelope', value => value.imageDetails.complete = false]])
    test(name + ' cannot start or downgrade the animation suite', async () => fixture(async f => {
        await assert.rejects(collectAnimation(f)); assert.equal(f.observer.actions.length, 0);
        assert.equal(f.observer.restored, false); assert.equal(existsSync(join(f.directory, 'animation-inputs')), false);
    }, { mutateCapture: mutate }));
