import { stockClipboardCallback } from './fixtures/private-clipboard-callback.mjs';
import { preparePrivateClipboardCallback } from './private-clipboard-source.mjs';
import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { createPrivateClipboardWorker, ownedPrivateClipboardSuiteId } from './private-clipboard-worker.mjs';
import { PrivateClipboardBridge } from './private-clipboard-bridge.mjs';
import { PRIVATE_CLIPBOARD_PROBES } from './private-clipboard-controller.mjs';

const runId = 'a'.repeat(32), paneId = '11111111-1111-1111-1111-111111111111';
const fixtureId = '22222222-2222-2222-2222-222222222222';
const nonce = 'terminfo-osc52-33333333-3333-3333-3333-333333333333';
const other = 'terminfo-osc52-44444444-4444-4444-4444-444444444444';
const seed = 'terminfo-private-seed-' + runId;
const hash = value => createHash('sha256').update(value).digest('hex');
const deferred = () => { let resolve; const promise = new Promise(yes => { resolve = yes; }); return { promise, resolve }; };

function fixture() {
    let value = seed, count = 0, armed, restored = false, closed = false;
    const api = { calls: [], hook: null, response: null,
        output(text) { assert.equal(text, armed); value = text; count++; armed = undefined; restored = false; } };
    const bridge = new PrivateClipboardBridge({ runId, paneId, budget: () => 1000,
        verifyBinding: async () => 'b'.repeat(64), rpc: async request => {
            api.calls.push(request); await api.hook?.(request);
            if (closed) throw new Error('closed private fixture');
            if (request.operation === 'armWrite') armed = request.value;
            if (request.operation === 'seed') { value = request.value; restored = false; }
            if (request.operation === 'restore') { value = seed; restored = true; armed = undefined; }
            if (request.operation === 'close') closed = true;
            return { fixtureId, state: closed ? 'closed' : 'active', acceptedWrites: count,
                valueSha256: hash(value), restored, complete: closed, ...(request.operation === 'read' ? { value } : {}) };
        } });
    api.worker = createPrivateClipboardWorker(async frame => {
        if (frame.kind === 'abort') {
            const reason = new Error('worker aborted callback');
            try { await bridge.abort(reason); } catch (error) { if (error !== reason) throw error; }
            return null;
        }
        const result = await bridge.handle(frame.kind === 'complete' ? { kind: 'complete' } : frame);
        if (frame.kind === 'finish') return api.response ?? { runId, suiteId: ownedPrivateClipboardSuiteId,
            file: 'private-clipboard-observations.json', sha256: hash(JSON.stringify(result)),
            counts: { completedProbes: 3, acceptedWrites: 2 }, complete: true };
        if (frame.kind === 'read' && api.response) return api.response;
        return result;
    }, runId);
    return api;
}

async function probe(api, index, result = { pass: index === 1 }) {
    return api.worker.withClipboardFixture(PRIVATE_CLIPBOARD_PROBES[index], async f => {
        if (index !== 2) { await f.beforeOsc52Write(nonce); api.output(nonce); assert.equal(await f.readText(), nonce); }
        if (index !== 1) await f.writeText(other);
        return result;
    });
}
async function all(api) { for (let i = 0; i < 3; i++) await probe(api, i); }

test('three callback results survive unchanged only after parent restoration and final close', async () => {
    const api = fixture(), results = [{ pass: false }, { pass: true }, { pass: false }];
    assert.deepEqual(api.calls, []);
    for (let i = 0; i < 3; i++) {
        assert.equal(await probe(api, i, results[i]), results[i]);
        assert.deepEqual(api.calls.slice(-2).map(x => x.operation), ['restore', 'read']);
    }
    assert.equal(JSON.parse(api.worker.summary).complete, false);
    const binding = await api.worker.finish(); assert.equal(binding.complete, true);
    assert.equal(JSON.parse(api.worker.summary).complete, true);
    assert.equal(api.calls.at(-1).operation, 'close');
    await assert.rejects(api.worker.finish(), /cannot resume/);
});

test('worker cannot emit original write before parent arm acknowledgment', async () => {
    const api = fixture(), entered = deferred(), release = deferred(); let emitted = false;
    api.hook = async request => { if (request.operation === 'armWrite') { entered.resolve(); await release.promise; } };
    const work = api.worker.withClipboardFixture(PRIVATE_CLIPBOARD_PROBES[0], async f => {
        await f.beforeOsc52Write(nonce); emitted = true; api.output(nonce); await f.readText(); await f.writeText(other);
    });
    await entered.promise; assert.equal(emitted, false); release.resolve(); await work;
    assert.equal(emitted, true);
});

test('callback exception restores through parent without completing proxy or changing rejection reason', async () => {
    const api = fixture(), original = new Error('callback failed');
    await assert.rejects(api.worker.withClipboardFixture(PRIVATE_CLIPBOARD_PROBES[0], async f => {
        await f.beforeOsc52Write(nonce); throw original;
    }), error => error === original);
    assert.deepEqual(api.calls.slice(-2).map(x => x.operation), ['restore', 'read']);
    assert.equal(JSON.parse(api.worker.summary).complete, false);
    await assert.rejects(api.worker.finish());
});

for (const ignored of ['pending', 'settled']) {
    test('unconsumed worker control remains fatal: ' + ignored, async () => {
        const api = fixture();
        await assert.rejects(api.worker.withClipboardFixture(PRIVATE_CLIPBOARD_PROBES[0], async f => {
            f.beforeOsc52Write(nonce);
            if (ignored === 'settled') await new Promise(resolve => setImmediate(resolve));
        }));
        assert.equal(JSON.parse(api.worker.summary).complete, false);
        assert.equal(api.calls.some(x => x.operation === 'close'), false);
    });
}

test('caught control refusal cannot resume worker or close parent fixture', async () => {
    const api = fixture();
    await assert.rejects(api.worker.withClipboardFixture(PRIVATE_CLIPBOARD_PROBES[0], async f => {
        await assert.rejects(async () => await f.beforeOsc52Write('invalid'));
        return { pass: true };
    }));
    await assert.rejects(api.worker.finish());
    assert.equal(api.calls.some(x => x.operation === 'armWrite' || x.operation === 'close'), false);
});

test('foreign canonical readback is rejected before reaching original grader', async () => {
    const api = fixture(); api.response = other; let graded = false;
    await assert.rejects(api.worker.withClipboardFixture(PRIVATE_CLIPBOARD_PROBES[0], async f => {
        await f.beforeOsc52Write(nonce); api.output(nonce); await f.readText(); graded = true;
    }));
    assert.equal(graded, false); assert.equal(JSON.parse(api.worker.summary).complete, false);
});

for (const change of [{ runId: 'c'.repeat(32) }, { complete: false }, { sha256: 'invalid' }, { counts: { completedProbes: 3, acceptedWrites: 1 } }]) {
    test('invalid final parent binding cannot complete worker: ' + Object.keys(change)[0], async () => {
        const api = fixture(); await all(api);
        api.response = { runId, suiteId: ownedPrivateClipboardSuiteId, file: 'private-clipboard-observations.json',
            sha256: 'd'.repeat(64), counts: { completedProbes: 3, acceptedWrites: 2 }, complete: true, ...change };
        await assert.rejects(api.worker.finish()); assert.equal(JSON.parse(api.worker.summary).complete, false);
    });
}

const adapted = preparePrivateClipboardCallback(stockClipboardCallback);
const controls = `const randomUUID = () => ${JSON.stringify(nonce.slice(15))};\n`
    + String.raw`const osc52Reply = /\x1b\]52;c;([A-Za-z0-9+/=]*)(?:\x07|\x1b\\)/;` + '\n';
const { liveClipboardProbe: callback } = await import('data:text/javascript;base64,'
    + Buffer.from(controls + adapted + '\nexport { liveClipboardProbe };').toString('base64'));



test('actual pinned Q11 callbacks keep original write packet and false/true/false grades', async () => {
    const api = fixture(), packets = [], grades = [];
    for (const [index, kind] of ['roundtrip', 'write', 'read'].entries()) {
        const result = await callback({
            withClipboardFixture: work => api.worker.withClipboardFixture(PRIVATE_CLIPBOARD_PROBES[index], work),
            write: packet => {
                assert.equal(packet, '\x1b]52;c;' + btoa(nonce) + '\x07');
                packets.push(packet); api.output(nonce);
            },
            queryCursorPosition: async () => ({ row: 1, col: 1 }),
            queryOutcome: async packet => {
                assert.equal(packet, '\x1b]52;c;?\x07');
                return { raw: '\x1b]52;c;\x07', match: true, reason: 'reply' };
            },
        }, kind);
        grades.push(result.pass);
        assert.deepEqual(api.calls.slice(-2).map(x => x.operation), ['restore', 'read']);
    }
    assert.deepEqual(grades, [false, true, false]); assert.equal(packets.length, 2);
    assert.equal(api.calls.filter(x => x.operation === 'seed').length, 2);
    await api.worker.finish(); assert.equal(JSON.parse(api.worker.summary).complete, true);
});

test('default context still refuses all Q11 callbacks before any OSC52 byte or query', async () => {
    let calls = 0;
    const context = { write: () => calls++, queryCursorPosition: () => calls++, queryOutcome: () => calls++ };
    for (const kind of ['roundtrip', 'write', 'read']) {
        const result = await callback(context, kind);
        assert.equal(result.pass, false); assert.equal(result.observation.reason, 'policy-refused');
    }
    assert.equal(calls, 0);
});
