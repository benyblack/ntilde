import assert from 'node:assert/strict';
import { test } from 'node:test';
import { queryWorld } from './fixtures/query-world.mjs';
import { queryAction, queryPlan, queryPrior, queryPriorCounts, buildQueryReport } from './queries.mjs';
import { rejectQueryOwnerRequest } from './query-capture.mjs';
import { snapshotHash } from './agent-observer.mjs';

const reportInput = report => {
    const { binding, priorChallenges, initial, actions, frames, seal } = report;
    return structuredClone({ binding, priorChallenges, initial, actions, frames, seal });
};
test('real raw reader plus two actual finite parent observers completes 40 independent synthetic contracts', async () => {
    const world = queryWorld(); await world.collect(); const report = world.observer.report;
    assert.equal(world.observer.restored, true); assert.equal(world.observer.failed, false);
    assert.deepEqual(report.readerCounts, [48, 42]); assert.deepEqual(world.observer.journal.readerCounts, [48, 42]);
    assert.deepEqual(report.unobservedState, ['reverseVideo']);
    assert.equal(report.actions.length, 44); assert.equal(report.frames.length, 44); assert.equal(report.observations.length, 40);
    assert.equal(new Set(report.observations.map(item => item.id)).size, 40);
    assert.equal(report.observations.filter(item => item.expectedEvidence === 'positive-reply').length, 3);
    assert.equal(report.observations.filter(item => item.expectedEvidence === 'negative-reply').length, 21);
    assert.equal(report.observations.filter(item => item.expectedEvidence === 'silence').length, 16);
    assert.ok(report.observations.every(item => item.contractMatched));
    assert.equal(report.observations.find(item => item.id === 'q12.decrqss.vertical-margins').expectedHex, Buffer.from('\x1bP1$r2;6r\x1b\\').toString('hex'));
    assert.equal(world.input.listenerCount('data'), 0); assert.equal(world.input.isRaw, false); assert.equal(world.input.isPaused(), true);
    assert.equal(world.packets.length, 44); assert.ok(world.packets.every(packet => Buffer.byteLength(packet) <= 64));
    assert.ok(!Object.hasOwn(report, 'supported') && !Object.hasOwn(report, 'counts') && !Object.hasOwn(report, 'completedReceipt'));
    assert.deepEqual(buildQueryReport(reportInput(report)), report);
    report.frames[0].before.screen.lines[0] = 'mutated returned result';
    assert.notEqual(world.observer.report.frames[0].before.screen.lines[0], 'mutated returned result');
});
for (const [name, replacement] of [['silence', ''], ['wrong geometry', '\x1b[8;60;10t'], ['duplicate', '\x1b[8;10;60t\x1b[8;10;60t'],
    ['trailing newline', '\x1b[8;10;60t\n'], ['high bytes', Buffer.from([0xff])]]) {
    test('semantic ' + name + ' retains exact observed bytes without changing evidence into support', async () => {
        const world = queryWorld({ mutateReply: (id, reply) => id === 'q07.device.xtwinops-18' ? replacement : reply });
        await world.collect(); const report = world.observer.report;
        const observation = report.observations.find(item => item.id === 'q07.device.xtwinops-18');
        assert.equal(observation.contractMatched, false); assert.equal(observation.expectedEvidence, 'positive-reply');
        assert.equal(observation.receivedHex, Buffer.from(replacement).toString('hex'));
        assert.equal(observation.observedEvidence, replacement.length ? 'unexpected-bytes' : 'silence');
        assert.equal(world.observer.restored, true);
    });
}
test('a source response that relativizes the prepared absolute margins remains a mismatch', async () => {
    const world = queryWorld({ mutateReply: (id, reply) => id === 'q12.decrqss.vertical-margins' ? '\x1bP1$r1;5r\x1b\\' : reply });
    await world.collect(); assert.equal(world.observer.report.observations.find(item => item.id === 'q12.decrqss.vertical-margins').contractMatched, false);
});
test('new phase rejects a prior global nonce pair even when the local reader has never used it', async () => {
    const world = queryWorld({ priorChallenges: ['42000,42001', ...Array.from({ length: 109 }, (_, index) => [32768 + index * 2, 32769 + index * 2].join(','))], nextNonces: () => [42000, 42001] });
    await assert.rejects(world.collect(), error => error.queryFailure.attempts === 1);
    assert.equal(world.observer.restored, false); assert.equal(world.observer.journal.actions.length, 1);
    assert.equal(world.observer.journal.frames.length, 0); assert.equal(world.input.listenerCount('data'), 0);
});
test('full 110 earlier pairs are retained without expanding either read budget', async () => {
    const priorChallenges = Array.from({ length: 110 }, (_, index) => [32768 + index * 2, 32769 + index * 2].join(','));
    const world = queryWorld({ priorChallenges }); await world.collect();
    assert.deepEqual(world.observer.report.priorChallenges, priorChallenges);
    // 111 earlier challenges are a valid count for a larger input plan; the bound is the largest plan's 152, never an open list.
    const pair = index => [32768 + index * 2, 32769 + index * 2].join(',');
    assert.doesNotThrow(() => queryPrior([...priorChallenges, '43000,43001']));
    assert.deepEqual(queryPriorCounts, [110, 152]);
    assert.doesNotThrow(() => queryPrior(Array.from({ length: 152 }, (_, index) => pair(index))));
    assert.throws(() => queryPrior(Array.from({ length: 153 }, (_, index) => pair(index))));
    assert.throws(() => queryWorld({ priorChallenges: [...priorChallenges, '43000,43001'] }), 'only whole plans are admitted as the earlier-challenge list');
    assert.throws(() => queryPrior(['42000,42001', '42000,42001']));
});
test('prior list copies length and indexed getter values once without calling supplied array methods', () => {
    const prior = []; let reads = 0;
    Object.defineProperty(prior, 0, { enumerable: true, get() { reads++; return reads === 1 ? '42000,42001' : 'changed'; } });
    prior.map = () => { throw new Error('untrusted array method'); };
    const copied = queryPrior(prior); assert.deepEqual(copied, ['42000,42001']); assert.equal(reads, 1);
    let lengths = 0; const proxy = new Proxy(['43000,43001'], { get(target, key) { if (key === 'length') { lengths++; return lengths === 1 ? 1 : 111; } return Reflect.get(target, key); } });
    assert.deepEqual(queryPrior(proxy), ['43000,43001']); assert.equal(lengths, 1); assert.ok(Object.isFrozen(copied));
});
for (const [name, mutate] of [
    ['stored cell', screen => { screen.lines[2] = 'changed'; }],
    ['rendition', screen => { screen.cellDetails[0].overline = true; }],
    ['margin', screen => { screen.bufferState.scrollBottom--; }],
    ['cursor', screen => { screen.cursorCol++; }],
]) test('query ' + name + ' mutation is sticky fatal and retains the partial pair', async () => {
    const world = queryWorld({ mutateState: (id, screen) => { if (id === 'q07.device.tertiary-da') mutate(screen); } });
    await assert.rejects(world.collect()); assert.equal(world.observer.failed, true); assert.equal(world.observer.restored, false);
    assert.equal(world.observer.journal.pending.index, 1); assert.ok(world.observer.journal.pending.capture);
    const failure = world.observer.journal.failure;
    await assert.rejects(world.observer.handle({ kind: 'queries-start' }, () => 1000)); assert.throws(() => world.observer.report);
    assert.deepEqual(world.observer.journal.failure, failure);
});
for (const [name, mutate] of [
    ['worker executable', fact => { fact.worker.executable = '/other/node'; }],
    ['worker marker', fact => { fact.workerCommand = '/node --other-worker'; }],
    ['ordered output', fact => { fact.terminalOutput.outputProcessing = true; }],
    ['lineage', fact => { fact.worker.ppid++; }],
]) test('independent callback ' + name + ' mismatch forbids admission', async () => {
    const world = queryWorld({ mutateOwnership: mutate }); await assert.rejects(world.collect());
    assert.equal(world.packets.length, 0); assert.equal(world.observer.failed, true);
});
test('wrong pane/profile, incomplete attributes, and changed history fail before writing', async () => {
    for (const mutateRpc of [
        (method, result) => { if (method === 'listSessions') result.sessions[0].kind = 'ssh'; return result; },
        (method, result) => { if (method === 'readScreen') result.attributeLines = []; return result; },
        (method, result) => { if (method === 'readScrollback') result.lines = ['unmeasured history']; return result; },
    ]) {
        const world = queryWorld({ mutateRpc }); await assert.rejects(world.collect()); assert.equal(world.packets.length, 0);
    }
});
test('child cannot supply a replacement query packet through the admission response', async () => {
    const world = queryWorld({ mutateAction: (kind, action) => kind === 'queries-admit' ? { ...action, commandHex: '61' } : action });
    await assert.rejects(world.collect()); assert.equal(world.packets.length, 0); assert.equal(world.observer.restored, false);
});
test('encoded or still-owned input prevents exclusive reader handoff', async () => {
    for (const change of [input => { input.readableEncoding = 'utf8'; }, input => { input.on('data', () => {}); }]) {
        const world = queryWorld(); change(world.input); await assert.rejects(world.collect(), /exclusive/);
        assert.equal(world.packets.length, 0); assert.equal(world.observer.restored, false);
    }
});
test('parent rederivation rejects malformed journals, seal/restoration changes, reused fences and forged expectations', async () => {
    const world = queryWorld(); await world.collect(); const valid = reportInput(world.observer.report);
    const changes = [
        input => { input.actions[1].commandHex = '61'; }, input => { input.frames.pop(); },
        input => { input.frames[1].capture = structuredClone(input.frames[0].capture); },
        input => { input.frames[1].before.screen.cols++; }, input => { input.frames[1].capture.byteLength++; },
        input => { input.frames[1].after.screen.lines[2] = 'rehashed mutation'; input.frames[1].after.sha256 = snapshotHash(input.frames[1].after.screen); },
        input => { input.seal.status.failed = true; }, input => { input.seal.status.disposed = false; },
        input => { input.seal.status.lastAttempt.hex += '00'; }, input => { input.seal.ownership.worker.ppid++; },
        input => { input.frames.at(-1).after.screen.lines[0] = 'leftover'; },
    ];
    for (const change of changes) { const input = structuredClone(valid); change(input); assert.throws(() => buildQueryReport(input)); }
    const forged = structuredClone(valid); forged.frames[2].capture.hex = '00'; forged.frames[2].capture.byteLength = 1;
    forged.frames[2].expectedReply = '\x00';
    const report = buildQueryReport(forged);
    assert.equal(report.observations[1].contractMatched, false); assert.notEqual(report.observations[1].expectedHex, '00');
});
test('changing getters are copied once and caller mutation cannot alter captured state or reports', async () => {
    const calls = [];
    const world = queryWorld({ mutateRpc: (method, result) => {
        if (method !== 'readScreen') return result;
        let count = 0; Object.defineProperty(result, 'rows', { enumerable: true, get() { count++; return count === 1 ? 10 : 99; } });
        calls.push(() => count); return result;
    } });
    await world.collect(); assert.ok(calls.every(count => count() === 1));
    const input = reportInput(world.observer.report), source = input.frames; let reads = 0;
    Object.defineProperty(input, 'frames', { enumerable: true, get() { reads++; return reads === 1 ? source : []; } });
    const report = buildQueryReport(input); assert.equal(reads, 1);
    source[0].capture.hex = 'ff'; assert.notEqual(report.frames[0].capture.hex, 'ff');
});
test('finite source-selected controls cannot become externally supplied packet arguments', () => {
    assert.equal(queryPlan.length, 44); assert.equal(queryPlan.filter(item => item.kind === 'query').length, 40);
    assert.ok(Object.isFrozen(queryPlan) && queryPlan.every(Object.isFrozen));
    assert.throws(() => queryAction({ kind: 'query', id: 'arbitrary' }, { runId: 'a'.repeat(32) }));
});
test('byte budget overflow retains exact partial bytes and cannot seal or retry', async () => {
    const world = queryWorld({ mutateReply: (id, reply) => id === 'q07.device.tertiary-da' ? Buffer.alloc(513, 0x78) : reply });
    await assert.rejects(world.collect(), error => error.queryFailure.failed && error.queryFailure.attempts === 2
        && error.queryFailure.lastAttempt.hex === '78'.repeat(513));
    assert.equal(world.observer.restored, false); assert.equal(world.observer.journal.frames.length, 1);
    assert.equal(world.observer.journal.failure.rawFailure.lastAttempt.hex, '78'.repeat(513));
    assert.equal(world.input.listenerCount('data'), 0); assert.equal(world.input.isRaw, false);
});
test('controller failure after admission releases the raw reader and leaves an incomplete parent journal', async () => {
    const world = queryWorld({ mutateAction: kind => { if (kind === 'queries-admit') throw new Error('controller exited'); } });
    await assert.rejects(world.collect(), error => error.queryFailure.attempts === 0);
    assert.equal(world.observer.journal.actions.length, 1); assert.equal(world.observer.journal.frames.length, 0);
    assert.equal(world.packets.length, 0); assert.equal(world.observer.restored, false); assert.equal(world.input.listenerCount('data'), 0);
    assert.equal(world.observer.failed, true); await assert.rejects(world.observer.handle({ kind: 'queries-seal', captureStatus: {} }, () => 1000));
});
test('late raw bytes before sealing are sticky fatal even after all 44 state pairs', async () => {
    let world;
    world = queryWorld({ mutateAction: (kind, result) => {
        if (kind === 'queries-read' && result.recorded === 43) world.input.emit('data', Buffer.from([0xff]));
        return result;
    } });
    await assert.rejects(world.collect()); assert.equal(world.observer.failed, true); assert.equal(world.observer.restored, false);
    assert.equal(world.observer.journal.frames.length, 44); assert.equal(world.observer.journal.seal.status.failed, true);
});
test('malformed nonce arrays, sparse history lists, unknown request fields and unbounded deadlines cannot admit', async () => {
    const sparse = Array(1); assert.throws(() => queryPrior(sparse));
    for (const pair of [Array(2), [42000], [42000, 42000], [42000, 70000]]) {
        const world = queryWorld({ nextNonces: () => pair }); await assert.rejects(world.collect()); assert.equal(world.observer.restored, false);
    }
    const world = queryWorld();
    await assert.rejects(world.observer.handle({ kind: 'queries-start', packet: '\x1b[18t' }, () => 1000));
    assert.equal(world.packets.length, 0); assert.equal(world.observer.failed, true);
    const bounded = queryWorld(); await assert.rejects(bounded.observer.handle({ kind: 'queries-start' }, () => 4001));
    assert.equal(bounded.observer.restored, false);
});

for (const cursorVisible of [true, false]) test('fixed final command restores independently observed cursor visibility: ' + cursorVisible, async () => {
    const world = queryWorld({ cursorVisible }); await world.collect(); const report = world.observer.report;
    assert.equal(report.initial.before.screen.cursorVisible, cursorVisible);
    assert.equal(report.frames.at(-1).after.screen.cursorVisible, cursorVisible);
    assert.equal(report.actions.at(-1).commandHex, Buffer.from('\x1bc\x1b[?25' + (cursorVisible ? 'h' : 'l')).toString('hex'));
});

test('actual owner transport envelope is independently checked and removed before exact action validation', async () => {
    const world = queryWorld({ transport: true }); await world.collect();
    assert.equal(world.observer.report.observations.length, 40); assert.deepEqual(world.observer.report.readerCounts, [48, 42]);
    const wrong = queryWorld({ transport: true, mutateAction: (kind, value) => kind === 'queries-admit' ? { ...value, ownerPid: 999 } : value });
    await assert.rejects(wrong.collect()); assert.equal(wrong.packets.length, 0);
});

for (const count of [0, 109]) test('collector rejects initial owner reply with only ' + count + ' prior challenges before attaching a reader or writing', async () => {
    const world = queryWorld({ transport: true, mutateAction: (kind, reply) => kind === 'queries-start'
        ? { ...reply, priorChallenges: reply.priorChallenges.slice(0, count) } : reply });
    await assert.rejects(world.collect());
    assert.equal(world.packets.length, 0); assert.equal(world.observer.journal.actions.length, 0); assert.equal(world.observer.journal.frames.length, 0);
    assert.equal(world.input.listenerCount('data'), 0); assert.equal(world.input.isRaw, false); assert.equal(world.input.isPaused(), true);
});

test('authenticated pre-dispatch rejection aborts promptly with original cause and retains original raw request without capability', async () => {
    const original = new Error('Recorded query worker disappeared before dispatch.');
    const world = queryWorld({ transport: true, beforeDispatch: request => { if (request.kind === 'queries-read' && request.index === 1) throw original; } });
    await assert.rejects(world.collect()); assert.equal(world.ownerFailure(), original);
    const journal = world.observer.journal; assert.equal(journal.failed, true); assert.equal(world.observer.restored, false);
    assert.equal(journal.failure.error, original.message); assert.equal(journal.failure.rejectedOwnerRequest.kind, 'queries-read');
    assert.equal(journal.failure.rejectedOwnerRequest.index, 1); assert.ok(journal.failure.rejectedOwnerRequest.capture.hex);
    assert.equal(JSON.stringify(journal).includes('synthetic-capability-must-not-be-retained'), false);
    assert.equal(world.input.listenerCount('data'), 0);
});
test('an unauthenticated socket rejection cannot abort the owned query observer', () => {
    let rejected = false; const world = queryWorld();
    assert.equal(rejectQueryOwnerRequest({ authenticated: false, observer: world.observer, error: new Error('bad capability'), request: {}, reject: () => rejected = true }), false);
    assert.equal(world.observer.failed, false); assert.equal(rejected, false);
});

test('under the v2 marker the DECRQSS s witness owes ESC P 1 $ r 1 ; cols s, a negative reply is then a mismatch, and the marker is recorded and re-derived', async () => {
    const world = queryWorld({ marginsReport: true }); await world.collect(); const report = world.observer.report;
    const witness = item => item.id === 'q12.decrqss.horizontal-margins';
    assert.ok(report.observations.every(item => item.contractMatched));
    assert.equal(report.observations.find(witness).expectedEvidence, 'positive-reply');
    assert.equal(report.observations.find(witness).expectedHex, Buffer.from('\x1bP1$r1;60s\x1b\\').toString('hex'));
    assert.equal(report.policy, 'owned-exact-query-paired-raw-state-v2'); assert.match(report.marginsReport, /^query policy v2/);
    assert.deepEqual(buildQueryReport({ ...reportInput(report), marginsReport: true }), report);
    assert.notDeepEqual(buildQueryReport(reportInput(report)), report, 'the same journal without the marker derives a different report');
    const stale = queryWorld({ marginsReport: true, mutateReply: (id, reply) => id === 'q12.decrqss.horizontal-margins' ? '\x1bP0$r\x1b\\' : reply });
    await stale.collect(); const mismatched = stale.observer.report.observations.find(witness);
    assert.equal(mismatched.contractMatched, false); assert.equal(mismatched.expectedEvidence, 'positive-reply');
    assert.equal(mismatched.receivedHex, Buffer.from('\x1bP0$r\x1b\\').toString('hex'));
    for (const bad of ['yes', 1, null]) assert.throws(() => buildQueryReport({ ...reportInput(report), marginsReport: bad }));
});
test('without the marker (every earlier run) the negative expectation and the v1 policy are unchanged', async () => {
    const world = queryWorld(); await world.collect(); const report = world.observer.report;
    assert.equal(report.observations.find(item => item.id === 'q12.decrqss.horizontal-margins').expectedEvidence, 'negative-reply');
    assert.equal(report.policy, 'owned-exact-query-paired-raw-state-v1'); assert.equal(Object.hasOwn(report, 'marginsReport'), false);
    const forged = buildQueryReport({ ...reportInput(report), marginsReport: true });
    assert.notDeepEqual(forged, report, 'a forged marker cannot re-derive the archived report');
    assert.equal(forged.observations.find(item => item.id === 'q12.decrqss.horizontal-margins').contractMatched, false);
});
