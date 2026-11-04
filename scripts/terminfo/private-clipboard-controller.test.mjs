import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { PrivateClipboardController, PRIVATE_CLIPBOARD_PROBES } from './private-clipboard-controller.mjs';
import { stockClipboardCallback } from './fixtures/private-clipboard-callback.mjs';
import { preparePrivateClipboardCallback } from './private-clipboard-source.mjs';
const runId = 'a'.repeat(32), paneId = '11111111-1111-1111-1111-111111111111';
const fixtureId = '22222222-2222-2222-2222-222222222222';
const nonce = 'terminfo-osc52-33333333-3333-3333-3333-333333333333';
const seed = 'terminfo-private-seed-' + runId;
const hash = value => createHash('sha256').update(value).digest('hex');

// Synthetic App storage only: no process launch, clipboard package, OS provider or terminal device.
function fixture(change = {}) {
    let value = seed, state = 'active', writes = 0, restored = false, armed;
    const calls = [];
    const snapshot = read => ({ fixtureId, state, valueSha256: hash(value), acceptedWrites: writes, restored,
        complete: state === 'closed', ...(read ? { value } : {}) });
    const api = {
        calls, binding: 'b'.repeat(64), hook: null,
        reset: () => { state = 'aborted'; value = seed; restored = true; armed = undefined; },
        output: text => {
            if (state !== 'active' || text !== armed) { api.reset(); return; }
            value = text; armed = undefined; writes++; restored = false;
        },
        rpc: async request => {
            calls.push(structuredClone(request));
            await api.hook?.(request);
            if (state !== 'active' && request.operation !== 'close') throw new Error('App refused inactive fixture');
            switch (request.operation) {
                case 'armWrite': armed = request.value; break;
                case 'seed': value = request.value; restored = false; break;
                case 'restore': value = seed; armed = undefined; restored = true; break;
                case 'close':
                    if (state === 'active' && !armed) { value = seed; restored = true; state = 'closed'; }
                    break;
            }
            return snapshot(request.operation === 'read');
        },
    };
    Object.assign(api, change);
    api.controller = new PrivateClipboardController({ runId, paneId, rpc: request => api.rpc(request),
        verifyBinding: async () => api.binding, budget: () => 1000 });
    return api;
}
async function runProbe(api, index, { result = { pass: false, observation: { reason: 'insufficient-evidence' } } } = {}) {
    return api.controller.transaction(PRIVATE_CLIPBOARD_PROBES[index], async f => {
        if (index !== 2) { await f.beforeOsc52Write(nonce); api.output(nonce); assert.equal(await f.readText(), nonce); }
        if (index !== 1) await f.writeText(nonce);
        return result;
    });
}
async function all(api) { for (let i = 0; i < 3; i++) await runProbe(api, i); }

test('lazy begin, exact three callbacks, independent per-probe restore/read then explicit final close', async () => {
    const api = fixture(); assert.deepEqual(api.calls, []);
    const negative = { pass: false, observation: { outcome: 'inconclusive', reason: 'insufficient-evidence' } };
    assert.equal(await runProbe(api, 0, { result: negative }), negative);
    assert.deepEqual(api.calls.slice(-2).map(x => x.operation), ['restore', 'read']);
    assert.equal(api.controller.evidence().complete, false);
    await runProbe(api, 1); await runProbe(api, 2);
    const evidence = await api.controller.finish();
    assert.equal(evidence.complete, true); assert.equal(evidence.fatal, false);
    assert.deepEqual(evidence.completedProbes, [...PRIVATE_CLIPBOARD_PROBES]);
    assert.equal(api.calls.filter(x => x.operation === 'begin').length, 1);
    assert.equal(api.calls.filter(x => x.operation === 'armWrite').length, 2);
    assert.equal(api.calls.filter(x => x.operation === 'seed').length, 2);
    assert.equal(api.calls.at(-1).operation, 'close');
    assert.equal(evidence.events.at(-1).result.acceptedWrites, 2);
    evidence.events.length = 0; assert.ok(api.controller.evidence().events.length > 0);
    await assert.rejects(api.controller.finish(), /cannot resume/);
});

test('callback failure still restores and cancels pending arm, remains sticky through collector catch', async () => {
    const api = fixture();
    await assert.rejects(api.controller.transaction(PRIVATE_CLIPBOARD_PROBES[0], async f => {
        await f.beforeOsc52Write(nonce); throw new Error('callback failed');
    }), /callback failed/);
    assert.deepEqual(api.calls.slice(-2).map(x => x.operation), ['restore', 'read']);
    await assert.rejects(runProbe(api, 0), /cannot resume/);
    await assert.rejects(api.controller.finish(), /cannot resume/);
    assert.equal(api.controller.evidence().complete, false);
    assert.equal(api.calls.filter(x => x.operation === 'begin').length, 1);
    assert.equal(api.calls.some(x => x.operation === 'close'), false);
    api.output(nonce); // Restore cancelled the arm; late terminal input aborts instead of being accepted.
    assert.equal(api.controller.evidence().fatal, true);
});

for (const index of [0, 2]) {
    test('caught invalid seed remains fatal in callback ' + index, async () => {
        const api = fixture();
        for (let i = 0; i < index; i++) await runProbe(api, i);
        await assert.rejects(api.controller.transaction(PRIVATE_CLIPBOARD_PROBES[index], async f => {
            if (index === 0) {
                await f.beforeOsc52Write(nonce); api.output(nonce); await f.readText();
            }
            await assert.rejects(async () => await f.writeText('invalid'), /nonce/);
            return { pass: false };
        }));
        assert.equal(api.controller.evidence().fatal, true);
        assert.equal(api.controller.evidence().complete, false);
        assert.deepEqual(api.calls.slice(-2).map(x => x.operation), ['restore', 'read']);
        await assert.rejects(api.controller.finish(), /cannot resume/);
        assert.equal(api.calls.some(x => x.operation === 'close'), false);
    });
}

test('caught wrong-order control cannot consume the required seed phase', async () => {
    const api = fixture();
    await runProbe(api, 0); await runProbe(api, 1);
    await assert.rejects(api.controller.transaction(PRIVATE_CLIPBOARD_PROBES[2], async f => {
        await assert.rejects(async () => await f.readText(), /order/);
    }));
    assert.equal(api.controller.evidence().fatal, true);
    assert.deepEqual(api.calls.slice(-2).map(x => x.operation), ['restore', 'read']);
    await assert.rejects(api.controller.finish(), /cannot resume/);
});

test('unconsumed control stays fatal after unrelated asynchronous work settles', async () => {
    const api = fixture();
    await runProbe(api, 0); await runProbe(api, 1);
    await assert.rejects(api.controller.transaction(PRIVATE_CLIPBOARD_PROBES[2], async f => {
        f.writeText(nonce);
        await new Promise(resolve => setImmediate(resolve));
    }));
    assert.equal(api.controller.evidence().fatal, true);
    assert.deepEqual(api.calls.slice(-2).map(x => x.operation), ['restore', 'read']);
    await assert.rejects(api.controller.finish(), /cannot resume/);
});

test('unconsumed control rejection is handled but cannot become completion', async () => {
    const api = fixture();
    await runProbe(api, 0); await runProbe(api, 1);
    await assert.rejects(api.controller.transaction(PRIVATE_CLIPBOARD_PROBES[2], async f => {
        f.writeText('invalid');
        await new Promise(resolve => setImmediate(resolve));
    }));
    assert.equal(api.controller.evidence().fatal, true);
    assert.deepEqual(api.calls.slice(-2).map(x => x.operation), ['restore', 'read']);
    await assert.rejects(api.controller.finish(), /cannot resume/);
});

test('control chaining uses native Promise results and preserves the whole transaction', async () => {
    const api = fixture();
    await api.controller.transaction(PRIVATE_CLIPBOARD_PROBES[0], f => {
        const arm = f.beforeOsc52Write(nonce);
        assert.ok(arm instanceof Promise);
        const chain = arm.then(() => api.output(nonce)).then(() => f.readText()).then(() => f.writeText(nonce));
        assert.equal(Object.getPrototypeOf(chain), Promise.prototype);
        return chain;
    });
    await runProbe(api, 1); await runProbe(api, 2);
    assert.equal((await api.controller.finish()).complete, true);
});

test('preserves original and restoration errors and never manufactures completion', async () => {
    const api = fixture();
    api.hook = async request => { if (request.operation === 'restore' && api.calls.filter(x => x.operation === 'restore').length === 2) throw new Error('restore failed'); };
    await assert.rejects(api.controller.transaction(PRIVATE_CLIPBOARD_PROBES[0], async () => { throw new Error('work failed'); }),
        error => error instanceof AggregateError && error.errors.some(x => /work failed/.test(x.message))
            && error.errors.some(x => /restore failed/.test(x.message)));
    assert.equal(api.controller.evidence().complete, false);
});

for (const operation of ['begin', 'armWrite', 'seed', 'read', 'restore', 'close']) {
    test(operation + ' refusal is fatal, no retry and no successful close', async () => {
        const api = fixture();
        api.hook = async request => { if (request.operation === operation) throw new Error('refused'); };
        if (operation === 'close') { await all(api); await assert.rejects(api.controller.finish()); }
        else await assert.rejects(runProbe(api, 0));
        assert.equal(api.controller.evidence().complete, false);
        assert.equal(api.controller.evidence().fatal, true);
        const attempts = api.calls.filter(x => x.operation === operation);
        assert.equal(attempts.length, 1); // A failed restore/read is never replayed by finally cleanup.
    });
}

for (const mutation of [result => ({ ...result, fixtureId: paneId }), result => ({ ...result, acceptedWrites: 1 }),
    result => ({ ...result, valueSha256: '0'.repeat(64) }), result => ({ ...result, state: 'aborted' }),
    result => ({ ...result, complete: true }), result => ({ ...result, value: 'unknown host text' })]) {
    test('malformed/mismatched App facts refuse completion without retaining unknown text', async () => {
        const api = fixture(), rpc = api.rpc;
        api.rpc = async request => { const result = await rpc(request); return request.operation === 'begin' ? mutation(result) : result; };
        await assert.rejects(runProbe(api, 0));
        const evidence = api.controller.evidence(); assert.equal(evidence.fatal, true); assert.equal(evidence.complete, false);
        assert.ok(!JSON.stringify(evidence).includes('unknown host text'));
    });
}

test('identity loss after App response retains actual facts and blocks all later writes/close', async () => {
    const api = fixture(), rpc = api.rpc;
    api.rpc = async request => { const value = await rpc(request); if (request.operation === 'armWrite') api.binding = 'c'.repeat(64); return value; };
    await assert.rejects(runProbe(api, 0));
    const arm = api.controller.evidence().events.find(x => x.request.operation === 'armWrite');
    assert.equal(arm.rpcStarted, true); assert.equal(arm.postVerified, false); assert.equal(arm.result.state, 'active');
    assert.equal(api.calls.some(x => x.operation === 'close'), false);
});

for (const phase of ['baseline', 'terminal-write']) {
    test('foreign canonical read value is rejected before retention at ' + phase, async () => {
        const api = fixture(), rpc = api.rpc;
        const foreign = 'terminfo-osc52-44444444-4444-4444-4444-444444444444';
        api.rpc = async request => {
            const result = await rpc(request);
            const target = phase === 'baseline' ? result.value === seed : result.value === nonce;
            return request.operation === 'read' && target ? { ...result, value: foreign, valueSha256: hash(foreign) } : result;
        };
        await assert.rejects(runProbe(api, 0));
        const evidence = api.controller.evidence();
        assert.equal(evidence.fatal, true); assert.equal(evidence.complete, false);
        assert.ok(!JSON.stringify(evidence).includes(foreign));
        assert.ok(!JSON.stringify(evidence).includes(hash(foreign)));
        assert.ok(evidence.events.some(x => x.request.operation === 'read' && x.failed && !x.result));
        await assert.rejects(api.controller.finish(), /cannot resume/);
        assert.equal(api.calls.some(x => x.operation === 'close'), false);
    });
}

test('validated source-known read survives later binding failure as actual failed-run evidence', async () => {
    const api = fixture(), rpc = api.rpc;
    api.rpc = async request => {
        const result = await rpc(request);
        if (request.operation === 'read') api.binding = 'c'.repeat(64);
        return result;
    };
    await assert.rejects(runProbe(api, 0));
    const read = api.controller.evidence().events.find(x => x.request.operation === 'read');
    assert.equal(read.result.value, seed); assert.equal(read.result.valueSha256, hash(seed));
    assert.equal(read.postVerified, false); assert.equal(read.failed, true);
    assert.equal(api.controller.evidence().fatal, true);
    assert.equal(api.controller.evidence().complete, false);
});

test('reset after begin cannot revive, rebind or complete the one fixture', async () => {
    const api = fixture();
    await assert.rejects(api.controller.transaction(PRIVATE_CLIPBOARD_PROBES[0], async () => { api.reset(); throw new Error('reset'); }));
    await assert.rejects(runProbe(api, 0)); await assert.rejects(api.controller.finish());
    assert.equal(api.calls.filter(x => x.operation === 'begin').length, 1);
});

test('wrong probe, duplicate probe and premature finalization are terminal refusals', async () => {
    const wrong = fixture(); await assert.rejects(runProbe(wrong, 1)); assert.equal(wrong.calls.length, 0);
    const duplicate = fixture(); await runProbe(duplicate, 0); await assert.rejects(runProbe(duplicate, 0));
    assert.equal(duplicate.calls.filter(x => x.operation === 'begin').length, 1);
    const early = fixture(); await assert.rejects(early.controller.finish()); assert.equal(early.calls.length, 0);
});

test('retained callback handle cannot act outside its transaction or in the following probe', async () => {
    const api = fixture(); let old;
    await api.controller.transaction(PRIVATE_CLIPBOARD_PROBES[0], async f => {
        old = f; await f.beforeOsc52Write(nonce); api.output(nonce); await f.readText(); await f.writeText(nonce);
    });
    const calls = api.calls.length;
    await assert.rejects(old.beforeOsc52Write(nonce), /inactive/);
    assert.equal(api.calls.length, calls); assert.equal(api.controller.evidence().fatal, true);
});

test('ignored stale control rejection stays handled and produces only fatal evidence', async () => {
    const api = fixture(); let old;
    await api.controller.transaction(PRIVATE_CLIPBOARD_PROBES[0], async f => {
        old = f; await f.beforeOsc52Write(nonce); api.output(nonce); await f.readText(); await f.writeText(nonce);
    });
    const calls = api.calls.length;
    old.readText();
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(api.calls.length, calls);
    assert.equal(api.controller.evidence().fatal, true);
    await assert.rejects(api.controller.finish(), /cannot resume/);
});

test('ignored overlapping control rejection stays handled while restoration completes', async () => {
    const api = fixture(); let release, entered;
    const arrived = new Promise(resolve => { entered = resolve; });
    const held = new Promise(resolve => { release = resolve; });
    api.hook = async request => { if (request.operation === 'armWrite') { entered(); await held; } };
    await assert.rejects(api.controller.transaction(PRIVATE_CLIPBOARD_PROBES[0], async f => {
        const first = Promise.resolve(f.beforeOsc52Write(nonce));
        const drained = first.catch(() => {});
        await arrived;
        f.readText();
        release(); await drained;
        await new Promise(resolve => setImmediate(resolve));
    }));
    assert.equal(api.controller.evidence().fatal, true);
    assert.deepEqual(api.calls.slice(-2).map(x => x.operation), ['restore', 'read']);
    await assert.rejects(api.controller.finish(), /cannot resume/);
});

test('fire-and-forget arm cannot outlive transaction restoration or authorize another call', async () => {
    const api = fixture(); let release, entered;
    const enteredPromise = new Promise(resolve => { entered = resolve; });
    const held = new Promise(resolve => { release = resolve; });
    api.hook = async request => { if (request.operation === 'armWrite') { entered(); await held; } };
    let arm;
    const transaction = api.controller.transaction(PRIVATE_CLIPBOARD_PROBES[0], async f => { arm = Promise.resolve(f.beforeOsc52Write(nonce)); await enteredPromise; });
    await enteredPromise; release();
    await assert.rejects(transaction, /did not await/); await assert.rejects(arm, /not awaited/);
    assert.deepEqual(api.calls.slice(-2).map(x => x.operation), ['restore', 'read']);
    assert.equal(api.controller.evidence().complete, false);
});


test('unexplained write-count growth at restore cannot complete a transaction', async () => {
    const api = fixture(), rpc = api.rpc;
    api.rpc = async request => {
        const result = await rpc(request);
        return request.operation === 'restore' ? { ...result, acceptedWrites: 1 } : result;
    };
    await assert.rejects(runProbe(api, 0));
    assert.equal(api.controller.evidence().fatal, true);
    assert.equal(api.calls.filter(x => x.operation === 'restore').length, 1);
});

test('incomplete callback controls, wrong nonce and nested transactions restore but stay fatal', async () => {
    for (const work of [async () => ({}), async f => f.beforeOsc52Write('arbitrary text'),
        async (_f, api) => api.controller.transaction(PRIVATE_CLIPBOARD_PROBES[0], async () => ({}))]) {
        const api = fixture();
        await assert.rejects(api.controller.transaction(PRIVATE_CLIPBOARD_PROBES[0], f => work(f, api)));
        assert.equal(api.controller.evidence().fatal, true);
        assert.deepEqual(api.calls.slice(-2).map(x => x.operation), ['restore', 'read']);
    }
});


test('actual pinned callback bodies preserve stock bytes/grader while controller completes private lifecycle', async () => {
    const api = fixture(), packets = [];
    const controls = `const randomUUID = () => ${JSON.stringify(nonce.slice(15))};\n`
        + String.raw`const osc52Reply = /\x1b\]52;c;([A-Za-z0-9+/=]*)(?:\x07|\x1b\\)/;` + '\n';
    const { liveClipboardProbe: probe } = await import('data:text/javascript;base64,'
        + Buffer.from(controls + preparePrivateClipboardCallback(stockClipboardCallback) + '\nexport { liveClipboardProbe };').toString('base64'));
    const results = [];
    for (const [index, kind] of ['roundtrip', 'write', 'read'].entries()) {
        results.push(await probe({
            withClipboardFixture: work => api.controller.transaction(PRIVATE_CLIPBOARD_PROBES[index], work),
            write: packet => { packets.push(packet); api.output(atob(packet.slice(7, -1))); },
            queryCursorPosition: async () => ({ row: 1, col: 1 }),
            queryOutcome: async packet => { packets.push(packet); return { raw: '\x1b]52;c;\x07', match: true, reason: 'reply' }; },
        }, kind));
    }
    assert.deepEqual(results.map(x => x.pass), [false, true, false]);
    assert.deepEqual(packets, ['\x1b]52;c;' + btoa(nonce) + '\x07', '\x1b]52;c;?\x07',
        '\x1b]52;c;' + btoa(nonce) + '\x07', '\x1b]52;c;?\x07']);
    assert.equal((await api.controller.finish()).complete, true);
});


test('concurrent transaction refusal while arm is pending prevents later terminal output', async () => {
    const api = fixture(); let release, entered, outputs = 0;
    const arrived = new Promise(resolve => { entered = resolve; });
    const held = new Promise(resolve => { release = resolve; });
    api.hook = async request => { if (request.operation === 'armWrite') { entered(); await held; } };
    const first = api.controller.transaction(PRIVATE_CLIPBOARD_PROBES[0], async f => {
        await f.beforeOsc52Write(nonce); outputs++; api.output(nonce);
    });
    await arrived;
    await assert.rejects(runProbe(api, 0), /overlapping/);
    release(); await assert.rejects(first);
    assert.equal(outputs, 0); assert.equal(api.controller.evidence().fatal, true);
    assert.deepEqual(api.calls.slice(-2).map(x => x.operation), ['restore', 'read']);
});
