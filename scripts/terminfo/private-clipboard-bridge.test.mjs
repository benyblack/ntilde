import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { PrivateClipboardBridge } from './private-clipboard-bridge.mjs';
import { PRIVATE_CLIPBOARD_PROBES } from './private-clipboard-controller.mjs';

const runId = 'a'.repeat(32), paneId = '11111111-1111-1111-1111-111111111111';
const fixtureId = '22222222-2222-2222-2222-222222222222';
const nonce = 'terminfo-osc52-33333333-3333-3333-3333-333333333333';
const seed = 'terminfo-private-seed-' + runId;
const hash = value => createHash('sha256').update(value).digest('hex');
const deferred = () => {
    let resolve;
    const promise = new Promise(yes => { resolve = yes; });
    return { promise, resolve };
};

// Synthetic private storage, not an App process, native clipboard or PTY proof.
function fixture() {
    let value = seed, count = 0, armed, restored = false, closed = false;
    const api = { calls: [], hook: null, binding: 'b'.repeat(64),
        output(text) {
            assert.equal(text, armed); value = text; count++; armed = undefined; restored = false;
        },
    };
    api.bridge = new PrivateClipboardBridge({ runId, paneId, budget: () => 1000,
        verifyBinding: async () => api.binding,
        rpc: async request => {
            api.calls.push(structuredClone(request));
            await api.hook?.(request);
            if (closed) throw new Error('closed synthetic fixture');
            if (request.operation === 'armWrite') armed = request.value;
            if (request.operation === 'seed') { value = request.value; restored = false; }
            if (request.operation === 'restore') { value = seed; armed = undefined; restored = true; }
            if (request.operation === 'close') { assert.equal(armed, undefined); closed = true; }
            return { fixtureId, state: closed ? 'closed' : 'active', valueSha256: hash(value), acceptedWrites: count,
                restored, complete: closed, ...(request.operation === 'read' ? { value } : {}) };
        },
    });
    return api;
}

async function probe(api, index) {
    await api.bridge.handle({ kind: 'start', probeId: PRIVATE_CLIPBOARD_PROBES[index] });
    if (index !== 2) {
        await api.bridge.handle({ kind: 'arm', value: nonce }); api.output(nonce);
        assert.equal(await api.bridge.handle({ kind: 'read' }), nonce);
    }
    if (index !== 1) await api.bridge.handle({ kind: 'seed', value: nonce });
    await api.bridge.handle({ kind: 'complete' });
}

async function all(api) { for (let i = 0; i < 3; i++) await probe(api, i); }

test('lazy parent transaction exposes only ordered controls and independently restores each callback', async () => {
    const api = fixture(); assert.deepEqual(api.calls, []);
    for (let i = 0; i < 3; i++) {
        await probe(api, i);
        assert.deepEqual(api.calls.slice(-2).map(x => x.operation), ['restore', 'read']);
        assert.equal(api.bridge.evidence().complete, false);
    }
    const evidence = await api.bridge.handle({ kind: 'finish' });
    assert.equal(evidence.complete, true);
    assert.deepEqual(evidence.completedProbes, [...PRIVATE_CLIPBOARD_PROBES]);
    assert.equal(evidence.events.at(-1).result.acceptedWrites, 2);
    assert.equal(api.calls.filter(x => x.operation === 'begin').length, 1);
    assert.equal(api.calls.filter(x => x.operation === 'seed').length, 2);
    assert.equal(api.calls.at(-1).operation, 'close');
    assert.equal(api.bridge.evidence().bridgeFinished, true);
    const count = api.calls.length;
    await assert.rejects(api.bridge.handle({ kind: 'finish' }), /closed/);
    await api.bridge.abort(); assert.equal(api.calls.length, count);
    evidence.events.length = 0; assert.ok(api.bridge.evidence().events.length);
});

test('arm acknowledgement is awaited before the caller can send its unchanged terminal write', async () => {
    const api = fixture(), entered = deferred(), release = deferred();
    await api.bridge.handle({ kind: 'start', probeId: PRIVATE_CLIPBOARD_PROBES[0] });
    api.hook = async request => { if (request.operation === 'armWrite') { entered.resolve(); await release.promise; } };
    let acknowledged = false;
    const arm = api.bridge.handle({ kind: 'arm', value: nonce }).then(() => { acknowledged = true; });
    await entered.promise; assert.equal(acknowledged, false);
    release.resolve(); await arm; api.output(nonce);
    assert.equal(await api.bridge.handle({ kind: 'read' }), nonce);
    await api.bridge.handle({ kind: 'seed', value: nonce });
    await api.bridge.handle({ kind: 'complete' });
});

for (const frame of [null, [], {}, { kind: 'start', probeId: PRIVATE_CLIPBOARD_PROBES[0], token: 'forbidden' },
    { kind: 'start', probeId: 'other' }, { kind: 'arm', value: 'other' }, { kind: 'read', value: nonce },
    { kind: 'complete', result: true }, { kind: 'close' }]) {
    test('malformed frame fails before RPC and cannot resume: ' + JSON.stringify(frame), async () => {
        const api = fixture();
        await assert.rejects(api.bridge.handle(frame));
        assert.deepEqual(api.calls, []); assert.equal(api.bridge.evidence().bridgeFailed, true);
        await assert.rejects(api.bridge.handle({ kind: 'start', probeId: PRIVATE_CLIPBOARD_PROBES[0] }));
        assert.deepEqual(api.calls, []);
    });
}

test('begin failure settles start with the original error before a callback fixture exists', async () => {
    const api = fixture(), original = new Error('original independent binding failure');
    api.hook = () => { throw original; };
    await assert.rejects(api.bridge.handle({ kind: 'start', probeId: PRIVATE_CLIPBOARD_PROBES[0] }), error => error === original);
    assert.equal(api.bridge.evidence().complete, false);
    assert.equal(api.calls.filter(x => x.operation === 'begin').length, 1);
});

test('missing callback phases are sticky even when a collector catches completion failure', async () => {
    const api = fixture();
    await api.bridge.handle({ kind: 'start', probeId: PRIVATE_CLIPBOARD_PROBES[0] });
    await assert.rejects(api.bridge.handle({ kind: 'complete' }), /omitted/);
    assert.deepEqual(api.calls.slice(-2).map(x => x.operation), ['restore', 'read']);
    await assert.rejects(api.bridge.handle({ kind: 'finish' }));
    assert.equal(api.calls.some(x => x.operation === 'close'), false);
});

for (const frame of [{ kind: 'arm', value: nonce }, { kind: 'read' }, { kind: 'seed', value: nonce }, { kind: 'complete' }, { kind: 'finish' },
    { kind: 'start', probeId: PRIVATE_CLIPBOARD_PROBES[1] }]) {
    test('pre-callback control or out-of-order start cannot gain a fixture: ' + frame.kind, async () => {
        const api = fixture(); await assert.rejects(api.bridge.handle(frame));
        assert.deepEqual(api.calls, []); assert.equal(api.bridge.evidence().complete, false);
    });
}

test('external abort rejects the active callback and awaits restoration without a close claim', async () => {
    const api = fixture(), original = new Error('parent ownership lost');
    await api.bridge.handle({ kind: 'start', probeId: PRIVATE_CLIPBOARD_PROBES[0] });
    await api.bridge.handle({ kind: 'arm', value: nonce });
    await assert.rejects(api.bridge.abort(original), error => error === original);
    assert.deepEqual(api.calls.slice(-2).map(x => x.operation), ['restore', 'read']);
    assert.equal(api.bridge.evidence().complete, false);
    assert.equal(api.calls.some(x => x.operation === 'close'), false);
    const count = api.calls.length;
    await assert.rejects(api.bridge.abort(), error => error === original);
    assert.equal(api.calls.length, count);
});

test('overlapping control poisons the callback, settles both requests, and restores once', async () => {
    const api = fixture(), entered = deferred(), release = deferred();
    await api.bridge.handle({ kind: 'start', probeId: PRIVATE_CLIPBOARD_PROBES[0] });
    api.hook = async request => { if (request.operation === 'armWrite') { entered.resolve(); await release.promise; } };
    const arm = assert.rejects(api.bridge.handle({ kind: 'arm', value: nonce }));
    await entered.promise;
    const overlap = assert.rejects(api.bridge.handle({ kind: 'complete' }), /Overlapping|ended/);
    release.resolve(); await Promise.all([arm, overlap]);
    assert.equal(api.bridge.evidence().bridgeFailed, true);
    assert.equal(api.bridge.evidence().complete, false);
    assert.equal(api.calls.some(x => x.operation === 'close'), false);
});

test('overlap during final close cannot produce completed bridge evidence', async () => {
    const api = fixture(), entered = deferred(), release = deferred(); await all(api);
    api.hook = async request => { if (request.operation === 'close') { entered.resolve(); await release.promise; } };
    const finish = assert.rejects(api.bridge.handle({ kind: 'finish' }));
    await entered.promise;
    await assert.rejects(api.bridge.handle({ kind: 'read' }), /Overlapping/);
    release.resolve(); await finish;
    assert.equal(api.bridge.evidence().bridgeFailed, true);
    assert.equal(api.bridge.evidence().complete, false);
    assert.equal(api.bridge.evidence().bridgeFinished, false);
});


test('binding drift during abort prevents any cleanup RPC and cannot produce completion', async () => {
    const api = fixture();
    await api.bridge.handle({ kind: 'start', probeId: PRIVATE_CLIPBOARD_PROBES[0] });
    const before = api.calls.length, original = new Error('original parent failure');
    api.binding = 'c'.repeat(64);
    await assert.rejects(api.bridge.abort(original), error => error instanceof AggregateError
        && error.errors[0] === original && /binding changed/.test(error.errors[1].message));
    assert.equal(api.calls.length, before);
    assert.equal(api.bridge.evidence().complete, false);
    await assert.rejects(api.bridge.handle({ kind: 'finish' }), error => error === original);
});

test('callback admission cannot expose a fixture after external abort during begin', async () => {
    const api = fixture(), entered = deferred(), release = deferred();
    api.hook = async request => { if (request.operation === 'begin') { entered.resolve(); await release.promise; } };
    const original = new Error('parent cancelled during begin');
    const start = assert.rejects(api.bridge.handle({ kind: 'start', probeId: PRIVATE_CLIPBOARD_PROBES[0] }));
    await entered.promise;
    const abort = assert.rejects(api.bridge.abort(original), error => error === original);
    release.resolve(); await Promise.all([start, abort]);
    assert.equal(api.bridge.evidence().complete, false);
    assert.equal(api.bridge.evidence().bridgeFailed, true);
    assert.equal(api.calls.some(x => x.operation === 'armWrite' || x.operation === 'close'), false);
    assert.deepEqual(api.calls.slice(-2).map(x => x.operation), ['restore', 'read']);
});

test('callback rejection preserves both original failure and independent restore failure', async () => {
    const api = fixture();
    await api.bridge.handle({ kind: 'start', probeId: PRIVATE_CLIPBOARD_PROBES[0] });
    const original = new Error('original callback exception'), cleanup = new Error('restore transport refused');
    api.hook = request => { if (request.operation === 'restore') throw cleanup; };
    await assert.rejects(api.bridge.abort(original), error => error instanceof AggregateError
        && error.errors[0] === original && error.errors[1] === cleanup);
    assert.equal(api.bridge.evidence().complete, false);
    assert.equal(api.bridge.evidence().bridgeFailed, true);
    assert.equal(api.calls.some(x => x.operation === 'close'), false);
});
