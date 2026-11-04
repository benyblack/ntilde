import test from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:net';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { privateClipboardRequest, validatePrivateClipboardRequest } from './private-clipboard-request.mjs';

const paneId = '11111111-1111-1111-1111-111111111111', runId = 'a'.repeat(32);
const request = { paneId, runId, operation: 'begin' };
const result = { fixtureId: '22222222-2222-2222-2222-222222222222', state: 'active', valueSha256: 'b'.repeat(64),
    acceptedWrites: 0, restored: false, complete: false };
const frame = JSON.stringify({ v: 1, id: 1, result }) + '\n';
const windowsEndpoint = suffix => String.raw`\\.\pipe\nt-clip-${suffix}`;

test('synthetic Windows endpoint has the exact named-pipe prefix and no controls', () => {
    assert.equal(windowsEndpoint('test'), '\\\\.\\pipe\\nt-clip-test');
    assert.ok(!/[\x00-\x1f]/.test(windowsEndpoint('test')));
});

async function fixture(respond, work) {
    const root = mkdtempSync(join(tmpdir(), 'nt-clip-'));
    const endpoint = process.platform === 'win32' ? windowsEndpoint(`${process.pid}-${root.split(/[\\/]/).at(-1)}`) : join(root, 's');
    const sockets = new Set(); let calls = 0;
    const server = createServer({ allowHalfOpen: true }, socket => {
        sockets.add(socket); socket.on('close', () => sockets.delete(socket)); socket.on('error', () => {});
        let text = '';
        socket.on('data', bytes => { text += bytes.toString(); });
        socket.on('end', () => { calls++; respond(socket, JSON.parse(text)); });
    });
    await new Promise(resolve => server.listen(endpoint, resolve));
    try { await work(endpoint); }
    finally { for (const socket of sockets) socket.destroy(); await new Promise(resolve => server.close(resolve)); rmSync(root, { recursive: true, force: true }); }
    return calls;
}

test('one bounded request half-closes and accepts only a correlated complete frame', async () => {
    const calls = await fixture((socket, message) => {
        assert.deepEqual(message, { v: 1, id: 1, method: 'clipboardFixture', params: request });
        socket.end(frame);
    }, async endpoint => assert.deepEqual(await privateClipboardRequest(endpoint, request, 1000), result));
    assert.equal(calls, 1);
});

for (const [name, bytes] of [
    ['refused', JSON.stringify({ v: 1, id: 1, error: { code: 'PermissionDenied' } }) + '\n'],
    ['uncorrelated', frame.replace('"id":1', '"id":2')], ['duplicate', frame + frame],
    ['false error', JSON.stringify({ v: 1, id: 1, result, error: false }) + '\n'],
    ['truncated', frame.slice(0, -1)], ['oversized', 'x'.repeat(4097)], ['malformed', '{\n'],
    ['unknown data', JSON.stringify({ v: 1, id: 1, result: { ...result, value: 'personal text must not escape' } }) + '\n'],
    ['invalid UTF8', Buffer.from([0xff, 10])], ['disconnect', ''],
]) test(name + ' cannot produce a result or retry', async () => {
    assert.equal(await fixture(socket => socket.end(bytes), async endpoint => {
        await assert.rejects(privateClipboardRequest(endpoint, request, 1000));
    }), 1);
});

test('a response is not accepted until EOF, catching duplicate frames in a later chunk', async () => {
    assert.equal(await fixture(socket => { socket.write(frame); setImmediate(() => socket.end(frame)); },
        endpoint => assert.rejects(privateClipboardRequest(endpoint, request, 1000))), 1);
});

test('no EOF times out once, without retry or late result', async () => {
    assert.equal(await fixture(socket => socket.write(frame),
        endpoint => assert.rejects(privateClipboardRequest(endpoint, request, 25), /timed out/)), 1);
});

for (const changed of [{ runId: 'A'.repeat(32) }, { paneId: '00000000-0000-0000-0000-000000000000' },
    { operation: 'sendInput' }, { fixtureId: result.fixtureId }, { value: 'text' }, { extra: true }]) {
    test('reject bounded request ' + JSON.stringify(changed), () => {
        assert.throws(() => validatePrivateClipboardRequest({ ...request, ...changed }));
    });
}
