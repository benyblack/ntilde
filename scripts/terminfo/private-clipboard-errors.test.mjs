import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import * as model from './private-clipboard.mjs';
const hash = value => createHash('sha256').update(value).digest('hex');
const source = readFileSync(new URL('../terminfo-owned-runner.mjs', import.meta.url), 'utf8');
const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
for (const phase of ['authenticatedOwnerDispatch', 'runFailure']) test('actual parent ' + phase + ' catch retains primary plus abort/restoration failure', async () => {
    const primary = new Error('operation failed'), restore = new Error('restore failed');
    const combined = new AggregateError([primary, restore], 'operation and restoration failed');
    const state = { observer: { complete: false, abort: async () => { throw combined; } } };
    const helperStart = source.indexOf('async function retainPrivateClipboardAbort(');
    const helperEnd = source.indexOf('/** Preserve only', helperStart);
    assert.ok(helperStart > 0 && helperEnd > helperStart);
    const call = "await retainPrivateClipboardAbort(privateClipboardState, error, '" + phase + "', retainPrivateClipboardJournal);";
    assert.ok(source.includes(call), 'actual parent catch must invoke the tested retention path');
    let retained;
    const run = new AsyncFunction('privateClipboardState', 'error', 'createProcessBudget', 'retainPrivateClipboardJournal',
        'abortPrivateClipboardWithFacts', source.slice(helperStart, helperEnd) + call);
    await run(state, primary, () => () => 4000, () => { retained = structuredClone(state.failures); }, model.abortPrivateClipboardWithFacts);
    assert.equal(retained?.[0]?.phase, phase);
    assert.equal(retained[0].primary.messageSha256, hash(primary.message));
    assert.equal(retained[0].cleanup.kind, 'aggregate');
    assert.equal(retained[0].cleanup.errors[1].messageSha256, hash(restore.message));
});

test('bounded parent facts preserve falsy/undefined children without invoking arbitrary error getters or retaining their text', () => {
    const secret = 'PERSONAL CLIPBOARD OR INPUT VALUE'; let invoked = false;
    const accessor = {};
    for (const key of ['message', 'cause', 'errors']) Object.defineProperty(accessor, key, { get() { invoked = true; throw Error(secret); } });
    const opaque = model.privateClipboardFailureFacts(accessor);
    assert.equal(invoked, false); assert.equal(opaque.messageUnavailable, true);
    const aggregate = new AggregateError([undefined, null, false, 0, '', new Error(secret)], 'combined');
    aggregate.cause = undefined;
    const fact = model.privateClipboardFailureFacts(aggregate);
    assert.deepEqual(fact.errors.slice(0, 4).map(value => value.type), ['undefined', 'null', 'boolean', 'number']);
    assert.equal(fact.cause.type, 'undefined'); assert.equal(fact.errors[5].messageSha256, hash(secret));
    assert.equal(JSON.stringify(fact).includes(secret), false);
    const cyclic = new AggregateError([], 'cycle'); cyclic.errors.push(cyclic);
    assert.ok(JSON.stringify(model.privateClipboardFailureFacts(cyclic)).length < 10000);
    const huge = model.privateClipboardFailureFacts(new Error('x'.repeat(50000)));
    assert.equal(huge.messageTruncated, true); assert.equal(huge.messageCodeUnits, 50000);
    assert.equal(huge.messageSha256, hash('x'.repeat(8192)));
});
test('a rejection identical to the primary remains explicit and failure-fact retention is bounded', async () => {
    const state = { observer: { abort: async error => { throw error; } } };
    for (let index = 0; index < 20; index++) await model.abortPrivateClipboardWithFacts(state, undefined, 'runFailure', () => 4000);
    assert.equal(state.failures.length, 8); assert.equal(state.failureFactsTruncated, true); assert.equal(state.failures[0].primary.type, 'undefined');
    assert.equal(state.failures[0].samePrimary, true); assert.equal(state.failures[0].abort, 'rejected');
});

test('actual parent failure file preserves the original cause without reading exception text getters', () => {
    let invoked = false, retained;
    const primary = {};
    Object.defineProperty(primary, 'message', { get() { invoked = true; throw Error('foreign value'); } });
    const start = source.indexOf('function retainOwnedFailure('), end = source.indexOf('/** Preserve only', start);
    const run = new Function('output', 'error', 'privateState', 'privateClipboardFailureFacts', 'writeJson', 'join',
        source.slice(start, end) + 'return retainOwnedFailure(output,error,privateState);');
    const result = run('/synthetic', primary, { failures: [{ phase: 'runFailure' }] }, model.privateClipboardFailureFacts,
        (_path, value) => { retained = value; }, (a, b) => a + '/' + b);
    assert.equal(invoked, false); assert.equal(result.cause, primary); assert.equal(retained.primary.messageUnavailable, true);
    assert.equal(retained.privateClipboardFailures[0].phase, 'runFailure'); assert.equal(retained.error.includes('foreign value'), false);
});

for (const primary of [new Error('original run failed'), undefined]) test('actual parent lifecycle preserves run cause when process cleanup also fails, primary=' + typeof primary, async () => {
    const cleanup = new Error('process cleanup failed'), wrapped = new Error('retained run failure', { cause: primary });
    const start = source.indexOf('        let deadline;'), end = source.indexOf('\n    } catch (error) {\n        retainPreparationFailure', start);
    assert.ok(start > 0 && end > start);
    const dependencies = {
        done: Promise.reject(primary), server: { once() {}, listen(_socket, resolve) { resolve(); } }, config: { socket: '/synthetic' },
        privateClipboardState: undefined, queryState: undefined, retainPrivateClipboardJournal() {}, retainQueryJournal() {},
        retainPrivateClipboardAbort: async () => {}, retainOwnedFailure: () => wrapped,
        cleanupOwnedRun: async () => { throw cleanup; }, output: '/synthetic', workerIdentity: undefined, runId: 'a'.repeat(32),
        app: undefined, proofs: [], completed: false, scratch: '/synthetic', preserveScratch: false,
    };
    const helperStart = source.indexOf('async function captureOwnedCleanup('), helperEnd = source.indexOf('/** Preserve only', helperStart);
    const run = new AsyncFunction(...Object.keys(dependencies), source.slice(helperStart, helperEnd) + source.slice(start, end));
    await assert.rejects(run(...Object.values(dependencies)), error => error instanceof AggregateError && error.errors[0] === wrapped
        && error.errors[0].cause === primary && error.errors[1] === cleanup);
});
